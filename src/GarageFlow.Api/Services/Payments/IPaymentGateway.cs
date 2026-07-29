namespace GarageFlow.Api.Services.Payments;

/// <summary>
/// What the app is told so it can send the customer to the gateway.
/// </summary>
/// <param name="Method">
/// <c>redirect</c> — open <see cref="Url"/> directly.
/// <c>form-post</c> — the gateway wants an HTML form POST, so the app opens a
/// page we serve that posts <see cref="Fields"/> on its behalf.
/// </param>
/// <param name="Url">Where the customer goes.</param>
/// <param name="Fields">Form fields, for <c>form-post</c>. Empty otherwise.</param>
public record PaymentStart(string Method, string Url, IReadOnlyDictionary<string, string> Fields);

/// <summary>The outcome of checking a payment with the provider.</summary>
/// <param name="Settled">True only when the provider says the money is theirs.</param>
/// <param name="ProviderRef">The gateway's own transaction id, for reconciliation.</param>
/// <param name="Failure">Why not, when <paramref name="Settled"/> is false.</param>
public record PaymentVerdict(bool Settled, string? ProviderRef, string? Failure)
{
    public static PaymentVerdict Ok(string providerRef) => new(true, providerRef, null);
    public static PaymentVerdict No(string failure) => new(false, null, failure);
}

/// <summary>
/// One payment provider.
/// </summary>
/// <remarks>
/// Two methods because there are only ever two moments: send the customer off
/// with a signed request, and later ask the provider whether the money actually
/// arrived. The second is deliberately a *server-to-server* question rather than
/// trust in whatever came back on the callback URL — a callback is a string in a
/// browser's address bar, and anybody can type one.
/// </remarks>
public interface IPaymentGateway
{
    /// <summary>Matches a value in <see cref="Domain.Vocabulary.OnlineProviders"/>.</summary>
    string Provider { get; }

    /// <summary>False when the provider has no usable credentials configured.</summary>
    /// <remarks>
    /// Checked before a payment is offered, so a workshop that has signed up with
    /// eSewa but not Khalti shows one button rather than two, the second of which
    /// dead-ends.
    /// </remarks>
    bool IsConfigured { get; }

    /// <summary>
    /// Signs a request and returns where to send the customer.
    /// </summary>
    /// <param name="reference">Our own id for this attempt; comes back on the callback.</param>
    /// <param name="amount">Exact amount to collect.</param>
    /// <param name="description">Shown on the provider's page.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PaymentStart> StartAsync(
        string reference, decimal amount, string description, CancellationToken ct = default);

    /// <summary>
    /// Asks the provider whether <paramref name="reference"/> was actually paid.
    /// </summary>
    /// <param name="reference">Our own id for the attempt being checked.</param>
    /// <param name="amount">What the attempt was for, so a short payment is caught.</param>
    /// <param name="callbackData">
    /// Whatever the gateway handed back — eSewa's base64 blob, Khalti's pidx.
    /// Used as a hint; the provider's own answer is what decides.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<PaymentVerdict> VerifyAsync(
        string reference, decimal amount, string? callbackData, CancellationToken ct = default);
}
