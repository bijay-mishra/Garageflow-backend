using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Services;

/// <summary>
/// Getting a finished vehicle back to its owner.
/// </summary>
/// <remarks>
/// One place for the rules, because three callers reach them: the job card
/// completing, the customer choosing, and the driver on the road. The rule that
/// matters most is that a quote is a <em>snapshot</em> — distance and fee are
/// computed once, when the customer accepts, and never recomputed. A customer
/// who was quoted Rs 172 pays Rs 172 even if the shop re-prices delivery while
/// their car is on the way.
/// </remarks>
public class DeliveryService(
    GarageFlowDbContext db,
    NotificationService notifications,
    ActivityLog activity,
    TimeProvider clock,
    ILogger<DeliveryService> logger)
{
    /// <summary>How many trail points to keep once a delivery closes.</summary>
    /// <remarks>
    /// The shape of the trip is worth keeping; every 15-second sample of it is
    /// not. Sixty points is enough to draw a recognisable route across a city.
    /// </remarks>
    private const int TrailKeep = 60;

    /// <summary>A quote, or why one cannot be given.</summary>
    /// <param name="DistanceKm">Straight-line, from the workshop pin.</param>
    /// <param name="Fee">What it would cost. Zero when the bill earns free delivery.</param>
    /// <param name="Error">Why delivery is not on offer; null when it is.</param>
    public record Quote(double DistanceKm, decimal Fee, string? Error)
    {
        public bool Ok => Error is null;
    }

    /// <summary>
    /// What home delivery would cost for this job, or why it is not on offer.
    /// </summary>
    /// <remarks>
    /// Four ways this can fail, and each gets its own sentence because each has
    /// a different fix: the shop has not turned delivery on, the shop has not
    /// pinned itself, the customer has not pinned themselves, or the customer is
    /// simply too far away.
    /// </remarks>
    public async Task<Quote> QuoteAsync(JobCard job, string customerId, CancellationToken ct = default)
    {
        var workshop = await db.Workshops.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Latitude != null, ct);

        if (workshop is null || !workshop.HasLocation)
            return new Quote(0, 0, "The workshop has not set its own location yet, so it cannot quote delivery.");

        if (!workshop.DeliveryEnabled)
            return new Quote(0, 0, "This workshop does not offer home delivery.");

        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId, ct);

        if (customer is null || !customer.HasLocation)
        {
            return new Quote(0, 0,
                "We need your location on the map before we can deliver. Add it from your account, or collect in person.");
        }

        var distance = Geo.DistanceKm(
            workshop.Latitude!.Value, workshop.Longitude!.Value,
            customer.Latitude!.Value, customer.Longitude!.Value);

        if (workshop.DeliveryMaxKm > 0 && distance > workshop.DeliveryMaxKm)
        {
            return new Quote(distance, 0,
                $"You are {distance:N1} km away and this workshop delivers up to {workshop.DeliveryMaxKm:N0} km.");
        }

        // Billed total, so the free-above threshold is measured against what the
        // customer is actually paying rather than the pre-tax subtotal.
        var invoice = await db.Invoices.AsNoTracking()
            .Where(i => i.JobCardId == job.Id)
            .Select(i => new { i.Subtotal, i.TaxRate })
            .FirstOrDefaultAsync(ct);

        var billTotal = invoice is null
            // No invoice raised yet — quote against the job's own line total,
            // which is what the invoice will be built from.
            ? job.Lines.Sum(l => l.Qty * l.UnitPrice)
            : invoice.Subtotal + Math.Round(invoice.Subtotal * invoice.TaxRate, 2);

        return new Quote(distance, workshop.QuoteDelivery(distance, billTotal), null);
    }

    /// <summary>
    /// Opens a handover when a job is finished, and tells the customer.
    /// </summary>
    /// <remarks>
    /// Idempotent: a job that moves to Completed, back to In Progress and to
    /// Completed again must not leave the customer two choices to make. The
    /// unique index on JobCardId enforces it at the database as well.
    /// </remarks>
    public async Task<Delivery?> OpenAsync(JobCard job, CancellationToken ct = default)
    {
        var existing = await db.Deliveries.FirstOrDefaultAsync(d => d.JobCardId == job.Id, ct);

        if (existing is not null) return existing;

        var vehicle = await db.Vehicles.AsNoTracking()
            .Where(v => v.Id == job.VehicleId)
            .Select(v => new { v.CustomerId, v.Plate })
            .FirstOrDefaultAsync(ct);

        if (vehicle is null) return null;

        var now = clock.GetUtcNow().UtcDateTime;

        var delivery = new Delivery
        {
            Id = Ids.Next(await db.Deliveries.IgnoreQueryFilters().Select(d => d.Id).ToListAsync(ct), "DEL"),
            JobCardId = job.Id,
            CustomerId = vehicle.CustomerId,
            Method = "Pickup",
            Status = "AwaitingChoice",
            CreatedAt = now,
        };

        db.Deliveries.Add(delivery);

        activity.Add($"{vehicle.Plate} ready for handover ({job.Id})", "job");

        await notifications.NotifyCustomerAsync(
            vehicle.CustomerId,
            "Your vehicle is ready",
            $"{vehicle.Plate} is finished. Choose collection or home delivery in the app.",
            "job",
            job.Id,
            ct);

        return delivery;
    }

    /// <summary>
    /// Records the customer's choice and, for a delivery, fixes the price.
    /// </summary>
    /// <remarks>
    /// The fee is added to the job card as a <c>service</c> line so it flows into
    /// the invoice with everything else — a delivery charge that lived only on
    /// the delivery record would have to be remembered separately at billing
    /// time, and would be forgotten.
    /// </remarks>
    public async Task<(Delivery? Delivery, string Message)> ChooseAsync(
        Delivery delivery, string method, CancellationToken ct = default)
    {
        if (delivery.Status is not "AwaitingChoice" and not "Scheduled")
            return (null, $"That handover is already {delivery.Status.ToLowerInvariant()}.");

        var job = await db.JobCards.Include(j => j.Lines)
            .FirstOrDefaultAsync(j => j.Id == delivery.JobCardId, ct);

        if (job is null) return (null, "The job for this handover no longer exists.");

        var now = clock.GetUtcNow().UtcDateTime;

        // Changing the choice removes whatever was charged for the previous one,
        // so switching delivery → pickup does not leave the fee on the bill.
        var previousFee = job.Lines.Where(l => l.Description == DeliveryLineName).ToList();
        db.JobLines.RemoveRange(previousFee);
        foreach (var line in previousFee) job.Lines.Remove(line);

        if (method == "Pickup")
        {
            delivery.Method = "Pickup";
            delivery.Status = "Scheduled";
            delivery.Fee = 0;
            delivery.DistanceKm = null;
            delivery.Address = "";
            delivery.Latitude = null;
            delivery.Longitude = null;
            delivery.ChosenAt = now;

            return (delivery, "Collection booked. The workshop will hold the vehicle for you.");
        }

        var quote = await QuoteAsync(job, delivery.CustomerId, ct);

        if (!quote.Ok) return (null, quote.Error!);

        var customer = await db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == delivery.CustomerId, ct);

        // Snapshotted, not referenced. A customer who moves next year must not
        // change where this delivery went.
        delivery.Method = "HomeDelivery";
        delivery.Status = "Scheduled";
        delivery.Address = customer?.Address ?? "";
        delivery.Latitude = customer?.Latitude;
        delivery.Longitude = customer?.Longitude;
        delivery.DistanceKm = Math.Round(quote.DistanceKm, 2);
        delivery.Fee = quote.Fee;
        delivery.ChosenAt = now;

        if (quote.Fee > 0)
        {
            job.Lines.Add(new JobLine
            {
                JobCardId = job.Id,
                Description = DeliveryLineName,
                Qty = 1,
                UnitPrice = quote.Fee,
                Kind = "service",
                SortOrder = job.Lines.Count == 0 ? 0 : job.Lines.Max(l => l.SortOrder) + 1,
            });
        }

        activity.Add($"Home delivery booked for {job.Id} — {quote.DistanceKm:N1} km", "job");

        return (delivery, quote.Fee > 0
            ? $"Home delivery booked. {quote.DistanceKm:N1} km, Rs {quote.Fee:N0} added to your bill."
            : $"Home delivery booked, free on this bill. {quote.DistanceKm:N1} km.");
    }

    /// <summary>The driver sets off.</summary>
    public async Task<string?> StartAsync(Delivery delivery, string driver, CancellationToken ct = default)
    {
        if (delivery.Method != "HomeDelivery")
            return "That handover is a collection, not a delivery.";

        if (delivery.Status == "OutForDelivery") return null;

        if (delivery.Status != "Scheduled")
            return $"That handover is {delivery.Status.ToLowerInvariant()} and cannot be started.";

        delivery.Status = "OutForDelivery";
        delivery.Driver = driver;
        delivery.StartedAt = clock.GetUtcNow().UtcDateTime;

        await notifications.NotifyCustomerAsync(
            delivery.CustomerId,
            "Your vehicle is on its way",
            $"{driver} has set off with it. Follow the journey in the app.",
            "job",
            delivery.JobCardId,
            ct);

        return null;
    }

    /// <summary>
    /// Records where the driver is.
    /// </summary>
    /// <remarks>
    /// Accepted only while the delivery is live. A phone that keeps sending
    /// after the job is done — because the driver left the screen on — must not
    /// keep writing rows, and a delivery that has been cancelled must not appear
    /// to still be moving.
    /// </remarks>
    public async Task<string?> PingAsync(
        Delivery delivery, double latitude, double longitude, double? accuracy, CancellationToken ct = default)
    {
        if (!delivery.IsLive) return "That delivery is not out for delivery.";

        var now = clock.GetUtcNow().UtcDateTime;

        delivery.DriverLatitude = latitude;
        delivery.DriverLongitude = longitude;
        delivery.DriverAt = now;

        db.DeliveryPoints.Add(new DeliveryPoint
        {
            DeliveryId = delivery.Id,
            Latitude = latitude,
            Longitude = longitude,
            AccuracyMetres = accuracy,
            At = now,
        });

        return null;
    }

    /// <summary>The vehicle is handed over.</summary>
    public async Task<string?> CompleteAsync(Delivery delivery, CancellationToken ct = default)
    {
        if (delivery.Status == "Delivered") return null;

        if (delivery.Status is "Cancelled")
            return "That handover was cancelled.";

        var now = clock.GetUtcNow().UtcDateTime;

        delivery.Status = "Delivered";
        delivery.CompletedAt = now;

        // The car is with its owner, so the job is too. Kept in step here rather
        // than left to staff: a delivered vehicle whose job card still reads
        // "Completed" is how a workshop loses track of what is in the yard.
        var job = await db.JobCards.FirstOrDefaultAsync(j => j.Id == delivery.JobCardId, ct);

        if (job is not null && job.Status == "Completed")
        {
            job.Status = "Delivered";
            job.CompletedAt ??= DateOnly.FromDateTime(now);
        }

        await PruneTrailAsync(delivery.Id, ct);

        activity.Add($"{delivery.JobCardId} handed over ({delivery.Method})", "job");

        await notifications.NotifyCustomerAsync(
            delivery.CustomerId,
            delivery.Method == "HomeDelivery" ? "Delivered" : "Handed over",
            delivery.Method == "HomeDelivery"
                ? "Your vehicle has been delivered. Thank you."
                : "Your vehicle has been handed over. Thank you.",
            "job",
            delivery.JobCardId,
            ct);

        return null;
    }

    /// <summary>
    /// Thins a finished delivery's trail down to something worth keeping.
    /// </summary>
    /// <remarks>
    /// A 40-minute drive at one ping every 15 seconds is 160 rows for a line on
    /// a map. Keeping every Nth point preserves the shape of the route while
    /// dropping most of the volume; the first and last are always kept so the
    /// line still starts at the workshop and ends at the door.
    /// </remarks>
    private async Task PruneTrailAsync(string deliveryId, CancellationToken ct)
    {
        var points = await db.DeliveryPoints
            .Where(p => p.DeliveryId == deliveryId)
            .OrderBy(p => p.At)
            .ToListAsync(ct);

        if (points.Count <= TrailKeep) return;

        var step = (double)points.Count / TrailKeep;
        var keep = new HashSet<int> { 0, points.Count - 1 };

        for (var i = 0; i < TrailKeep; i++) keep.Add((int)(i * step));

        var drop = points.Where((_, index) => !keep.Contains(index)).ToList();

        db.DeliveryPoints.RemoveRange(drop);

        logger.LogInformation(
            "Pruned {Dropped} of {Total} trail points for {Delivery}",
            drop.Count, points.Count, deliveryId);
    }

    /// <summary>
    /// The description used for the delivery charge on a job card.
    /// </summary>
    /// <remarks>
    /// Matched by text when the customer changes their mind, which is why it is
    /// a constant rather than typed in two places. It is not a catalogue service:
    /// the price is computed per delivery, so there is nothing on the price list
    /// for it to point at.
    /// </remarks>
    public const string DeliveryLineName = "Home delivery";
}
