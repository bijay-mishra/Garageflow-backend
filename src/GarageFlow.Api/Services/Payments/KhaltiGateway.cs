using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace GarageFlow.Api.Services.Payments;

/// <summary>
/// Khalti ePayment (KPG-2).
/// </summary>
/// <remarks>
/// Cleaner than eSewa's form post: a server-to-server call returns a
/// <c>payment_url</c> and a <c>pidx</c>, the customer is sent to the URL, and the
/// pidx is looked up afterwards to find out what happened. Nothing sensitive
/// ever passes through the browser, so there is no signature to verify — the
/// lookup <em>is</em> the verification.
///
/// Amounts are in paisa. Sending rupees is the classic Khalti integration bug
/// and it fails in the worst direction: a Rs 1,000 bill charged as Rs 10.
/// </remarks>
public class KhaltiGateway(
    IOptions<PaymentOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<KhaltiGateway> logger) : IPaymentGateway
{
    private readonly PaymentOptions _options = options.Value;
    private KhaltiOptions Khalti => _options.Khalti;

    public string Provider => "Khalti";

    /// <summary>
    /// False until a secret key is configured. Khalti issues one per merchant,
    /// including sandbox merchants, so there is no usable shared test key to
    /// ship — better to hide the button than to offer one that dead-ends.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Khalti.SecretKey);

    public async Task<PaymentStart> StartAsync(
        string reference, decimal amount, string description, CancellationToken ct = default)
    {
        var client = Client();

        var body = new Dictionary<string, object>
        {
            ["return_url"] = $"{_options.CallbackBaseUrl}/api/payments/callback/khalti",
            ["website_url"] = Khalti.WebsiteUrl,
            // Paisa, not rupees. Rounded away from zero so a half-paisa never
            // silently under-collects.
            ["amount"] = (long)Math.Round(amount * 100, MidpointRounding.AwayFromZero),
            ["purchase_order_id"] = reference,
            ["purchase_order_name"] = description,
        };

        using var response = await client.PostAsJsonAsync($"{Khalti.BaseUrl}/epayment/initiate/", body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Khalti initiate failed for {Reference}: {Status} {Body}",
                reference, response.StatusCode, text);

            // Khalti's own message is usually specific and useful ("amount
            // should be greater than Rs 10"), so it is passed through.
            throw new PaymentGatewayException(ReadError(text) ?? "Khalti could not start this payment.");
        }

        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;

        var url = root.TryGetProperty("payment_url", out var u) ? u.GetString() : null;
        var pidx = root.TryGetProperty("pidx", out var p) ? p.GetString() : null;

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(pidx))
            throw new PaymentGatewayException("Khalti did not return a payment link.");

        // The pidx travels back as a field so the caller can store it — the
        // lookup later needs it, and Khalti's callback may not be trusted to
        // carry it intact.
        return new PaymentStart("redirect", url, new Dictionary<string, string> { ["pidx"] = pidx });
    }

    public async Task<PaymentVerdict> VerifyAsync(
        string reference, decimal amount, string? callbackData, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(callbackData))
            return PaymentVerdict.No("This Khalti payment has no lookup id to check.");

        try
        {
            var client = Client();

            using var response = await client.PostAsJsonAsync(
                $"{Khalti.BaseUrl}/epayment/lookup/",
                new Dictionary<string, string> { ["pidx"] = callbackData },
                ct);

            var text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Khalti lookup failed for {Reference}: {Status}", reference, response.StatusCode);
                return PaymentVerdict.No("Khalti could not confirm this payment. It will be checked again shortly.");
            }

            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;

            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            var transactionId = root.TryGetProperty("transaction_id", out var t) ? t.GetString() : null;
            var paidPaisa = root.TryGetProperty("total_amount", out var a) ? a.GetInt64() : 0;

            if (status != "Completed")
            {
                return PaymentVerdict.No(status switch
                {
                    "Pending" or "Initiated" => "Khalti is still processing this payment.",
                    "User canceled" => "The Khalti payment was cancelled.",
                    "Expired" => "The Khalti payment link expired. Start again.",
                    "Refunded" => "That Khalti payment was refunded.",
                    _ => $"Khalti reported the payment as {status?.ToLowerInvariant() ?? "unknown"}.",
                });
            }

            // Confirming the amount as well as the status: a Completed status
            // for the wrong sum would otherwise settle a bill that was not paid.
            var expectedPaisa = (long)Math.Round(amount * 100, MidpointRounding.AwayFromZero);

            if (paidPaisa < expectedPaisa)
            {
                logger.LogWarning(
                    "Khalti paid {Paid} paisa against {Expected} for {Reference}",
                    paidPaisa, expectedPaisa, reference);

                return PaymentVerdict.No(
                    $"Khalti received Rs {paidPaisa / 100m:N2} against a bill of Rs {amount:N2}.");
            }

            return PaymentVerdict.Ok(transactionId ?? callbackData);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogError(ex, "Khalti lookup failed for {Reference}", reference);
            return PaymentVerdict.No("Could not reach Khalti to confirm. The payment will be checked again shortly.");
        }
    }

    private HttpClient Client()
    {
        var client = httpClientFactory.CreateClient(nameof(KhaltiGateway));

        // Khalti's own scheme — "Key <secret>", not Bearer.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Key", Khalti.SecretKey);

        return client;
    }

    /// <summary>Khalti reports field errors as arrays; this pulls out the first sentence.</summary>
    private static string? ReadError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);

            foreach (var property in json.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() > 0)
                    return property.Value[0].GetString();

                if (property.Value.ValueKind == JsonValueKind.String && property.NameEquals("detail"))
                    return property.Value.GetString();
            }
        }
        catch (JsonException)
        {
            // Not JSON — nothing useful to show a customer.
        }

        return null;
    }

    /// <summary>Formats an amount the way Khalti's own messages do.</summary>
    internal static string Rupees(long paisa) => (paisa / 100m).ToString("N2", CultureInfo.InvariantCulture);
}

/// <summary>A gateway refused a request, with a message worth showing the customer.</summary>
public class PaymentGatewayException(string message) : Exception(message);
