namespace GarageFlow.Api.Services.Payments;

/// <summary>
/// Gateway credentials and endpoints.
/// </summary>
/// <remarks>
/// Everything here ships pointed at the providers' public sandboxes, so the flow
/// works end to end on a fresh clone with nothing to sign up for. Going live is
/// four values: each provider's <c>MerchantCode</c>/<c>SecretKey</c>, and
/// swapping the two base URLs for the production ones.
///
/// The secrets below are the providers' own published test credentials — they
/// are in eSewa's and Khalti's public documentation and are worth nothing. Real
/// keys must never be committed: put them in user-secrets or environment
/// variables, exactly as the JWT signing key already is.
/// </remarks>
public class PaymentOptions
{
    public const string SectionName = "Payments";

    /// <summary>
    /// Where the gateway sends the customer back to. Must be reachable from the
    /// customer's phone, which on a real device is not <c>localhost</c>.
    /// </summary>
    public string CallbackBaseUrl { get; set; } = "http://localhost:5100";

    public EsewaOptions Esewa { get; set; } = new();
    public KhaltiOptions Khalti { get; set; } = new();

    /// <summary>
    /// How long a started payment stays claimable before it is treated as
    /// abandoned. A customer who opened the wallet page and wandered off should
    /// not block the invoice forever.
    /// </summary>
    public int PendingMinutes { get; set; } = 30;
}

public class EsewaOptions
{
    /// <summary>Sandbox merchant code. <c>EPAYTEST</c> is eSewa's published test value.</summary>
    public string MerchantCode { get; set; } = "EPAYTEST";

    /// <summary>
    /// HMAC-SHA256 key used to sign the request and to check the response.
    /// eSewa's published sandbox secret.
    /// </summary>
    public string SecretKey { get; set; } = "8gBm/:&EnhH.1/q";

    /// <summary>Where the customer's browser is posted to. Production drops the <c>rc-</c>.</summary>
    public string FormUrl { get; set; } =
        "https://rc-epay.esewa.com.np/api/epay/main/v2/form";

    /// <summary>Server-to-server status check, used when the callback is not trusted.</summary>
    public string StatusUrl { get; set; } =
        "https://rc.esewa.com.np/api/epay/transaction/status/";
}

public class KhaltiOptions
{
    /// <summary>
    /// Live secret key. Khalti's sandbox issues one per test merchant; the
    /// placeholder below fails cleanly with the provider's own message rather
    /// than pretending to work.
    /// </summary>
    public string SecretKey { get; set; } = "";

    /// <summary>Sandbox base. Production is <c>https://khalti.com/api/v2</c>.</summary>
    public string BaseUrl { get; set; } = "https://dev.khalti.com/api/v2";

    /// <summary>Shown on Khalti's own payment page.</summary>
    public string WebsiteUrl { get; set; } = "http://localhost:5000";
}
