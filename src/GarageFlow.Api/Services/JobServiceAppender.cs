using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Services;

/// <summary>
/// Puts catalogue services onto a job card as priced lines.
/// </summary>
/// <remarks>
/// Shared because three callers do the same thing and must do it identically:
/// an advisor adding a wash from the dashboard, a mechanic adding one from the
/// app when they see the state of the car, and a booking being converted with
/// the extras the customer ticked. If the pricing rule lived in each of them it
/// would drift, and the first symptom would be an invoice nobody can explain.
///
/// Scoped, so it shares the request's DbContext. Nothing here calls
/// <c>SaveChangesAsync</c> — the caller owns the transaction, which is what lets
/// a job card, its new lines and the customer's notification commit together.
/// </remarks>
public class JobServiceAppender(GarageFlowDbContext db)
{
    /// <summary>What happened, in a shape the controller can turn into a response.</summary>
    /// <param name="Added">Lines actually appended, in the order given.</param>
    /// <param name="AlreadyOn">Names skipped because the job already carried them.</param>
    /// <param name="Error">Set when nothing was done and the caller should return 400.</param>
    public record Result(List<JobLine> Added, List<string> AlreadyOn, string? Error)
    {
        public decimal Total => Added.Sum(l => l.Qty * l.UnitPrice);
    }

    /// <summary>
    /// Appends the given services to <paramref name="job"/>, skipping any it
    /// already carries.
    /// </summary>
    /// <remarks>
    /// The description and price are <em>copied</em> from the catalogue, not
    /// referenced. From here the line stands alone: re-pricing the wash next
    /// month leaves this job at what it was quoted, and an advisor can still
    /// discount the line without touching the price list.
    ///
    /// <paramref name="requireBookable"/> is set only for customer-facing
    /// callers. Staff and mechanics may add anything on the list — a courtesy
    /// wash is precisely the sort of thing the shop adds and a customer cannot
    /// order.
    /// </remarks>
    public async Task<Result> AppendAsync(
        JobCard job,
        IReadOnlyCollection<string> serviceIds,
        bool requireBookable = false,
        CancellationToken ct = default)
    {
        // Tapping a row twice in the app is one wash, not two.
        var wanted = serviceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (wanted.Count == 0)
            return new Result([], [], "Choose at least one service.");

        var found = await db.Services
            .Where(s => wanted.Contains(s.Id))
            .ToListAsync(ct);

        if (found.Count != wanted.Count)
        {
            var missing = wanted.Except(found.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
            return new Result([], [], $"Service '{missing.First()}' does not exist.");
        }

        if (found.FirstOrDefault(s => !s.IsActive) is { } retired)
            return new Result([], [], $"'{retired.Name}' is no longer offered.");

        if (requireBookable && found.FirstOrDefault(s => !s.IsBookable) is { } internalOnly)
            return new Result([], [], $"'{internalOnly.Name}' cannot be booked online — ask the workshop.");

        // Ordered by the caller's list so the job card reads the way the extras
        // were picked, rather than in whatever order SQL Server returned them.
        var ordered = wanted
            .Select(id => found.First(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var existing = job.Lines
            .Where(l => l.ServiceId is not null)
            .Select(l => l.ServiceId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = new List<JobLine>();
        var alreadyOn = new List<string>();
        var nextSort = job.Lines.Count == 0 ? 0 : job.Lines.Max(l => l.SortOrder) + 1;

        foreach (var service in ordered)
        {
            if (!existing.Add(service.Id))
            {
                alreadyOn.Add(service.Name);
                continue;
            }

            var line = new JobLine
            {
                JobCardId = job.Id,
                Description = service.Name,
                Qty = 1,
                UnitPrice = service.Price,
                Kind = "service",
                ServiceId = service.Id,
                SortOrder = nextSort++,
            };

            job.Lines.Add(line);
            added.Add(line);
        }

        return new Result(added, alreadyOn, null);
    }

    /// <summary>
    /// The same thing for a booking being converted, where the price to use is
    /// the one the customer was quoted rather than today's.
    /// </summary>
    /// <remarks>
    /// A booking made three weeks ago against a price that has since gone up is
    /// still the shop's word. The catalogue is read only for the name, so a
    /// service renamed in the meantime reads correctly on the job card.
    /// </remarks>
    public static List<JobLine> LinesFromBooking(Booking booking, int startSortOrder = 0)
    {
        var sort = startSortOrder;

        return booking.Services
            .OrderBy(s => s.Id)
            .Select(s => new JobLine
            {
                Description = s.Service?.Name ?? "Service",
                Qty = 1,
                UnitPrice = s.QuotedPrice,
                Kind = "service",
                ServiceId = s.ServiceId,
                SortOrder = sort++,
            })
            .ToList();
    }
}
