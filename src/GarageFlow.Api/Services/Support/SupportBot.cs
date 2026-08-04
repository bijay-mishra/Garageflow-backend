using System.Text;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Services.Support;

/// <summary>
/// Answers a support question: scripted first, model second, human third.
/// </summary>
/// <remarks>
/// <para>
/// The order is the whole design. A scripted answer is instant, free and cannot
/// be wrong, so it goes first; the model handles what nobody anticipated; and
/// when neither can help, a person does. Each layer is allowed to decline, and
/// declining is cheap — the escalation path is always there.
/// </para>
/// <para>
/// The context this assembles is the security boundary for the AI layer.
/// <see cref="SupportAi"/> deliberately does no authorisation of its own, so
/// everything handed to it must already be scoped: a customer's context is
/// built from their own rows only, and a workshop's from its own settings. The
/// tenant query filter covers the company boundary; the explicit customer
/// filter here covers the one inside it.
/// </para>
/// </remarks>
public class SupportBot(GarageFlowDbContext db, SupportAi ai, TimeProvider clock)
{
    /// <summary>How many earlier turns to give the model for a follow-up.</summary>
    /// <remarks>
    /// Enough for "what about the other one?" to resolve, and short enough that
    /// a long thread does not grow the prompt without bound. A conversation
    /// past this length has usually earned a human anyway.
    /// </remarks>
    private const int HistoryTurns = 8;

    public bool AiAvailable => ai.IsConfigured;

    /// <summary>
    /// The bot's answer to the newest message in <paramref name="thread"/>.
    /// </summary>
    public async Task<SupportAnswer> AnswerAsync(
        SupportThread thread, string question, CancellationToken ct = default)
    {
        // Scripted first. Cheap, instant, and correct by construction.
        if (SupportKnowledge.Match(thread.Audience, question) is { } faq)
            return new SupportAnswer(faq.Answer, SupportAnswerSource.Faq);

        if (!ai.IsConfigured) return new SupportAnswer(null, SupportAnswerSource.Unanswered);

        var context = thread.Audience == SupportAudience.WorkshopToPlatform
            ? await WorkshopContextAsync(ct)
            : await CustomerContextAsync(thread.CustomerId, ct);

        var history = await db.SupportMessages.AsNoTracking()
            .Where(m => m.ThreadId == thread.Id)
            .OrderByDescending(m => m.Id)
            .Take(HistoryTurns)
            .Select(m => new { m.Sender, m.Body })
            .ToListAsync(ct);

        history.Reverse();

        return await ai.AnswerAsync(
            SupportKnowledge.SystemPrompt(thread.Audience),
            context,
            history
                .Select(m => (FromUser: m.Sender != SupportSender.Bot, Text: m.Body))
                .ToList(),
            question,
            ct);
    }

    /// <summary>
    /// What the model may know about a customer: their own vehicles, jobs and
    /// bills, and nothing else in the workshop.
    /// </summary>
    /// <remarks>
    /// The <c>CustomerId</c> filter is not decoration. Without it every query
    /// here returns the whole garage — the tenant filter only proves the rows
    /// belong to this company, not to this person. A null id therefore yields an
    /// empty context rather than an unfiltered one, which is the same rule
    /// CurrentUserService.CustomerScopeAsync enforces on every other read path.
    /// </remarks>
    private async Task<string> CustomerContextAsync(string? customerId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(customerId))
            return "No customer record is attached to this account, so no vehicle or billing history is available.";

        var context = new StringBuilder();

        var vehicles = await db.Vehicles.AsNoTracking()
            .Where(v => v.CustomerId == customerId)
            .Select(v => new { v.Id, v.Plate, v.Make, v.Model, v.Year })
            .ToListAsync(ct);

        context.AppendLine("Their vehicles:");
        if (vehicles.Count == 0) context.AppendLine("  (none on file)");
        foreach (var vehicle in vehicles)
            context.AppendLine($"  {vehicle.Plate} — {vehicle.Year} {vehicle.Make} {vehicle.Model}");

        var vehicleIds = vehicles.Select(v => v.Id).ToList();

        var jobs = await db.JobCards.AsNoTracking()
            .Where(j => vehicleIds.Contains(j.VehicleId))
            .OrderByDescending(j => j.CreatedAt)
            .Take(5)
            .Select(j => new
            {
                j.Id, j.Status, j.Complaint, j.CreatedAt, j.PromisedAt, j.CompletedAt,
                Plate = j.Vehicle!.Plate,
            })
            .ToListAsync(ct);

        context.AppendLine();
        context.AppendLine("Their recent jobs, newest first:");
        if (jobs.Count == 0) context.AppendLine("  (none)");
        foreach (var job in jobs)
        {
            context.AppendLine(
                $"  {job.Id} · {job.Plate} · {job.Status} · opened {job.CreatedAt:d MMM yyyy}"
                + $" · promised {job.PromisedAt:d MMM yyyy}"
                + (job.CompletedAt is { } done ? $" · completed {done:d MMM yyyy}" : "")
                + (string.IsNullOrWhiteSpace(job.Complaint) ? "" : $" · reported: {job.Complaint}"));
        }

        var invoices = await db.Invoices.AsNoTracking()
            .Where(i => i.CustomerId == customerId)
            .OrderByDescending(i => i.IssuedAt)
            .Take(5)
            .ToListAsync(ct);

        context.AppendLine();
        context.AppendLine("Their recent bills, newest first:");
        if (invoices.Count == 0) context.AppendLine("  (none)");
        foreach (var invoice in invoices)
        {
            // Status and Total are computed on the entity, so these are read
            // after materialising rather than inside the projection.
            context.AppendLine(
                $"  {invoice.Id} · {invoice.VehiclePlate} · issued {invoice.IssuedAt:d MMM yyyy}"
                + $" · total Rs {invoice.Total:N0} · paid Rs {invoice.Paid:N0} · {invoice.Status}");
        }

        var workshop = await db.Workshops.AsNoTracking().FirstOrDefaultAsync(ct);

        if (workshop is not null)
        {
            context.AppendLine();
            context.AppendLine($"Their garage: {workshop.Name}"
                + (string.IsNullOrWhiteSpace(workshop.Phone) ? "" : $", phone {workshop.Phone}")
                + (string.IsNullOrWhiteSpace(workshop.OpeningHours) ? "" : $", open {workshop.OpeningHours}"));
        }

        return context.ToString();
    }

    /// <summary>
    /// What the model may know about a workshop asking GarageFlow for help:
    /// how their own install is configured.
    /// </summary>
    /// <remarks>
    /// Configuration only — no customer names, no takings. A product-support
    /// question is answered from how the product is set up, and putting a
    /// workshop's commercial data in the prompt would be collecting it for no
    /// reason.
    /// </remarks>
    private async Task<string> WorkshopContextAsync(CancellationToken ct)
    {
        var workshop = await db.Workshops.AsNoTracking().FirstOrDefaultAsync(ct);

        if (workshop is null) return "No workshop record was found for this account.";

        var context = new StringBuilder();

        context.AppendLine($"Workshop: {workshop.Name} (company code {workshop.CompanyCode})");
        context.AppendLine($"Registered name: {Or(workshop.LegalName, "not set")}");
        context.AppendLine($"PAN: {Or(workshop.TaxNumber, "not set")}");
        context.AppendLine($"Logo: {(workshop.LogoPath is null ? "not uploaded" : "uploaded")}");
        context.AppendLine($"Listed in the customer app's directory: {(workshop.IsListed ? "yes" : "no")}");
        context.AppendLine($"Map pin set: {(workshop.HasLocation ? "yes" : "no")}");
        context.AppendLine($"Home delivery: {(workshop.CanDeliver ? "quoting" : workshop.DeliveryEnabled ? "switched on but needs a map pin" : "off")}");
        context.AppendLine($"Bank transfer details on file: {(workshop.CanBankTransfer ? "yes" : "no")}");
        context.AppendLine($"Modules enabled: {Or(workshop.EnabledModules, "none")}");

        var staff = await db.Users.CountAsync(u => u.CompanyCode == workshop.CompanyCode, ct);
        var branches = await db.Branches.CountAsync(bch => bch.CompanyCode == workshop.CompanyCode, ct);

        context.AppendLine($"Staff accounts: {staff}");
        context.AppendLine($"Branches: {branches}");
        context.AppendLine($"Today: {clock.GetUtcNow():d MMM yyyy}");

        return context.ToString();
    }

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
