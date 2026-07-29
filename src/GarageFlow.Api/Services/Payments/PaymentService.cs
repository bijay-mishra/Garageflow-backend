using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GarageFlow.Api.Services.Payments;

/// <summary>
/// Owns the money side of an online payment: which gateway, what state the
/// attempt is in, and when an invoice is allowed to move.
/// </summary>
/// <remarks>
/// The gateways know how to talk to eSewa and Khalti and nothing else. This
/// knows the rules that must hold whichever one is used:
///
/// <list type="bullet">
/// <item>an attempt is written before the customer leaves, so the callback has
/// something to find;</item>
/// <item>an invoice's <c>Paid</c> only moves on a verified Completed payment;</item>
/// <item>verifying twice is safe — the second call finds the payment already
/// settled and changes nothing. Gateways retry callbacks, and customers refresh
/// pages.</item>
/// </list>
/// </remarks>
public class PaymentService(
    GarageFlowDbContext db,
    IEnumerable<IPaymentGateway> gateways,
    IOptions<PaymentOptions> options,
    ActivityLog activity,
    NotificationService notifications,
    TimeProvider clock,
    ILogger<PaymentService> logger)
{
    private readonly PaymentOptions _options = options.Value;

    /// <summary>Providers with usable credentials, in the order they are offered.</summary>
    public IReadOnlyList<string> AvailableProviders =>
        Vocabulary.OnlineProviders
            .Where(name => Find(name)?.IsConfigured == true)
            .ToList();

    private IPaymentGateway? Find(string provider) =>
        gateways.FirstOrDefault(g => string.Equals(g.Provider, provider, StringComparison.OrdinalIgnoreCase));

    /// <summary>What a caller gets back after starting a payment.</summary>
    public record StartResult(
        Payment Payment,
        PaymentStart Start,
        string? Error)
    {
        public bool Ok => Error is null;
    }

    /// <summary>
    /// Opens an attempt against an invoice and asks the gateway where to send
    /// the customer.
    /// </summary>
    /// <remarks>
    /// Any earlier Pending attempt on the same invoice is abandoned first. A
    /// customer who bailed out of eSewa and came back to try Khalti should not
    /// leave two open attempts that could both later settle.
    /// </remarks>
    public async Task<StartResult> StartAsync(
        Invoice invoice, string provider, CancellationToken ct = default)
    {
        var gateway = Find(provider);

        if (gateway is null || !gateway.IsConfigured)
            return Fail($"{provider} is not set up for online payment.");

        var outstanding = invoice.Total - invoice.Paid;

        if (outstanding <= 0)
            return Fail($"Invoice {invoice.Id} is already paid in full.");

        var now = clock.GetUtcNow().UtcDateTime;

        // Stale attempts are closed rather than left lying around, so the unique
        // reference index and the reporting both stay honest.
        var stale = await db.Payments
            .Where(p => p.InvoiceId == invoice.Id && p.Status == "Pending")
            .ToListAsync(ct);

        foreach (var attempt in stale)
        {
            attempt.Status = "Cancelled";
            attempt.FailureReason = "Superseded by a newer attempt.";
        }

        // Random rather than sequential: the reference is handed to a third
        // party and echoed back through a browser, so it should not tell anyone
        // how many invoices the workshop has raised.
        var reference = $"{invoice.Id}-{Guid.NewGuid():N}"[..32];

        var payment = new Payment
        {
            InvoiceId = invoice.Id,
            Amount = outstanding,
            Method = gateway.Provider,
            Channel = Vocabulary.ChannelFor(gateway.Provider),
            Status = "Pending",
            Reference = reference,
            InitiatedAt = now,
            At = now,
        };

        PaymentStart start;

        try
        {
            start = await gateway.StartAsync(
                reference, outstanding, $"Invoice {invoice.Id}", ct);
        }
        catch (PaymentGatewayException ex)
        {
            return Fail(ex.Message);
        }

        // Khalti hands back a pidx that the later lookup cannot work without, so
        // it is stored now rather than hoped for on the callback.
        if (start.Fields.TryGetValue("pidx", out var pidx))
            payment.ProviderRef = pidx;

        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);

        return new StartResult(payment, start, null);

        static StartResult Fail(string message) =>
            new(null!, null!, message);
    }

    /// <summary>
    /// Confirms an attempt with the provider and, if the money is real, moves
    /// the invoice.
    /// </summary>
    /// <param name="reference">Our own reference, from the callback.</param>
    /// <param name="callbackData">
    /// eSewa's base64 blob or Khalti's pidx. Optional — for Khalti the stored
    /// pidx is used when the callback does not carry one.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<(Payment? Payment, bool Settled, string Message)> VerifyAsync(
        string reference, string? callbackData, CancellationToken ct = default)
    {
        var payment = await db.Payments
            .Include(p => p.Invoice)
            .FirstOrDefaultAsync(p => p.Reference == reference, ct);

        if (payment is null)
            return (null, false, "That payment could not be found.");

        // Idempotent. A gateway that retries its callback, or a customer who
        // refreshes the return page, must not double-credit the invoice.
        if (payment.Status == "Completed")
            return (payment, true, $"Invoice {payment.InvoiceId} is already settled.");

        if (payment.Status is "Cancelled" or "Failed")
            return (payment, false, payment.FailureReason ?? "That payment attempt is closed.");

        var gateway = Find(payment.Method);

        if (gateway is null)
            return (payment, false, $"{payment.Method} is no longer available.");

        var verdict = await gateway.VerifyAsync(
            reference, payment.Amount, callbackData ?? payment.ProviderRef, ct);

        if (!verdict.Settled)
        {
            // Left Pending on purpose. "Could not reach the gateway" and "the
            // customer cancelled" arrive here identically, and marking a payment
            // Failed because the network hiccupped would be the worse mistake —
            // the sweep below closes genuinely abandoned attempts on a timer.
            payment.FailureReason = verdict.Failure;
            await db.SaveChangesAsync(ct);

            return (payment, false, verdict.Failure ?? "The payment was not completed.");
        }

        var invoice = payment.Invoice!;
        var now = clock.GetUtcNow().UtcDateTime;

        // Clamped: the amount was the balance when the attempt started, and a
        // cash payment taken at the counter in the meantime must not push the
        // invoice into credit.
        var outstanding = invoice.Total - invoice.Paid;
        var applied = Math.Min(payment.Amount, Math.Max(outstanding, 0));

        payment.Amount = applied;
        payment.Status = "Completed";
        payment.ProviderRef = verdict.ProviderRef;
        payment.FailureReason = null;
        payment.At = now;

        invoice.Paid += applied;
        invoice.Method = payment.Method;

        activity.Add($"{payment.Method} payment received on {invoice.Id}", "invoice");

        await notifications.NotifyCustomerAsync(
            invoice.CustomerId,
            "Payment received",
            $"Rs {applied:N2} received for invoice {invoice.Id}. Thank you.",
            "invoice",
            invoice.Id,
            ct);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Payment {Reference} settled {Amount} on {Invoice} via {Provider}",
            reference, applied, invoice.Id, payment.Method);

        var settledInFull = invoice.Paid >= invoice.Total;

        return (payment, true, settledInFull
            ? $"Payment received. Invoice {invoice.Id} is settled in full."
            : $"Payment of Rs {applied:N2} received. Rs {invoice.Total - invoice.Paid:N2} still due.");
    }

    /// <summary>
    /// Closes attempts nobody ever came back from.
    /// </summary>
    /// <remarks>
    /// Called opportunistically when payments are listed rather than on a timer:
    /// the volume is tiny, and a background service would be machinery for a
    /// query that costs nothing. An abandoned attempt is harmless while it sits
    /// there — it holds no money — but it clutters the record and makes a
    /// customer's history read as though they tried and failed repeatedly.
    /// </remarks>
    public async Task<int> ExpireStaleAsync(CancellationToken ct = default)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime.AddMinutes(-_options.PendingMinutes);

        var stale = await db.Payments
            .Where(p => p.Status == "Pending" && p.InitiatedAt < cutoff)
            .ToListAsync(ct);

        foreach (var payment in stale)
        {
            payment.Status = "Cancelled";
            payment.FailureReason ??= "Not completed in time.";
        }

        if (stale.Count > 0) await db.SaveChangesAsync(ct);

        return stale.Count;
    }
}
