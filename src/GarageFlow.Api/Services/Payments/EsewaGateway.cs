using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace GarageFlow.Api.Services.Payments;

/// <summary>
/// eSewa ePay v2.
/// </summary>
/// <remarks>
/// The flow is a signed HTML form POST rather than a REST call: the customer's
/// browser posts the fields to eSewa, authorises there, and is redirected back
/// to our success URL carrying a base64 blob of the result.
///
/// Two things about that blob matter. It is signed, so it can be checked — and
/// it arrives through the customer's own browser, so it must be. This class
/// verifies the signature and then, because a signature only proves the message
/// was not edited, asks eSewa's status endpoint directly. A payment is settled
/// when eSewa says it is, not when the URL says so.
/// </remarks>
public class EsewaGateway(
    IOptions<PaymentOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<EsewaGateway> logger) : IPaymentGateway
{
    private readonly PaymentOptions _options = options.Value;
    private EsewaOptions Esewa => _options.Esewa;

    public string Provider => "eSewa";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Esewa.MerchantCode) && !string.IsNullOrWhiteSpace(Esewa.SecretKey);

    public Task<PaymentStart> StartAsync(
        string reference, decimal amount, string description, CancellationToken ct = default)
    {
        // eSewa wants plain decimal strings, and it signs exactly the text it is
        // sent — so the formatting here and the formatting in the signature have
        // to be the same string, produced once.
        var total = amount.ToString("0.##", CultureInfo.InvariantCulture);

        // The signature covers these three fields, in this order. eSewa rejects
        // any other order with an unhelpful generic error, which is why the
        // signed-fields list is spelled out rather than derived from the dictionary.
        var signedFieldNames = "total_amount,transaction_uuid,product_code";
        var payload =
            $"total_amount={total},transaction_uuid={reference},product_code={Esewa.MerchantCode}";

        var fields = new Dictionary<string, string>
        {
            ["amount"] = total,
            // The workshop absorbs these rather than passing them on; the parts
            // still have to be present and to add up to total_amount.
            ["tax_amount"] = "0",
            ["product_service_charge"] = "0",
            ["product_delivery_charge"] = "0",
            ["total_amount"] = total,
            ["transaction_uuid"] = reference,
            ["product_code"] = Esewa.MerchantCode,
            ["success_url"] = $"{_options.CallbackBaseUrl}/api/payments/callback/esewa",
            ["failure_url"] = $"{_options.CallbackBaseUrl}/api/payments/callback/esewa?failed=1",
            ["signed_field_names"] = signedFieldNames,
            ["signature"] = Sign(payload),
        };

        return Task.FromResult(new PaymentStart("form-post", Esewa.FormUrl, fields));
    }

    public async Task<PaymentVerdict> VerifyAsync(
        string reference, decimal amount, string? callbackData, CancellationToken ct = default)
    {
        // The callback blob is checked first because it is free and it catches a
        // tampered redirect immediately. It is never the final word.
        if (!string.IsNullOrWhiteSpace(callbackData))
        {
            var decoded = TryDecodeCallback(callbackData);

            if (decoded is null)
                return PaymentVerdict.No("The response from eSewa could not be read.");

            if (!decoded.SignatureValid)
                return PaymentVerdict.No("The response from eSewa failed its signature check.");

            if (!string.Equals(decoded.TransactionUuid, reference, StringComparison.Ordinal))
                return PaymentVerdict.No("That eSewa response belongs to a different payment.");
        }

        // Whatever the browser said, ask eSewa.
        var total = amount.ToString("0.##", CultureInfo.InvariantCulture);
        var url =
            $"{Esewa.StatusUrl}?product_code={Uri.EscapeDataString(Esewa.MerchantCode)}" +
            $"&total_amount={total}&transaction_uuid={Uri.EscapeDataString(reference)}";

        try
        {
            var client = httpClientFactory.CreateClient(nameof(EsewaGateway));
            using var response = await client.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("eSewa status check for {Reference} returned {Status}", reference, response.StatusCode);
                return PaymentVerdict.No("eSewa could not confirm this payment. Try again in a moment.");
            }

            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;

            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            var providerRef = root.TryGetProperty("ref_id", out var r) ? r.GetString() : null;

            return status switch
            {
                "COMPLETE" => PaymentVerdict.Ok(providerRef ?? reference),
                "PENDING" => PaymentVerdict.No("eSewa is still processing this payment."),
                "CANCELED" or "NOT_FOUND" => PaymentVerdict.No("The eSewa payment was cancelled."),
                _ => PaymentVerdict.No($"eSewa reported the payment as {status?.ToLowerInvariant() ?? "unknown"}."),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Deliberately not treated as a failure of the *payment* — the money
            // may well have moved. The attempt stays Pending and can be checked
            // again, which is far better than telling a customer who has paid
            // that they have not.
            logger.LogError(ex, "eSewa status check failed for {Reference}", reference);
            return PaymentVerdict.No("Could not reach eSewa to confirm. The payment will be checked again shortly.");
        }
    }

    /// <summary>HMAC-SHA256 over the payload, base64 — the format eSewa expects.</summary>
    private string Sign(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Esewa.SecretKey));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    private record CallbackData(string? TransactionUuid, string? Status, bool SignatureValid);

    /// <summary>
    /// Unpacks the base64 JSON eSewa appends to the success URL and re-checks its
    /// signature over the fields it says were signed.
    /// </summary>
    private CallbackData? TryDecodeCallback(string encoded)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            string? Read(string name) =>
                root.TryGetProperty(name, out var value) ? value.GetString() : null;

            var signedNames = Read("signed_field_names");
            var signature = Read("signature");

            if (signedNames is null || signature is null) return null;

            // Rebuilt from the field list eSewa itself names, so a change on
            // their side to what is signed does not silently break the check.
            var payload = string.Join(',', signedNames.Split(',').Select(name => $"{name}={Read(name)}"));

            return new CallbackData(
                Read("transaction_uuid"),
                Read("status"),
                // Fixed-time compare: a signature check that returns early leaks
                // how much of the value was right.
                CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(Sign(payload)),
                    Encoding.UTF8.GetBytes(signature)));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}
