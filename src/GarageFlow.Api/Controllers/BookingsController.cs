using GarageFlow.Api.Contracts;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using GarageFlow.Api.Mapping;
using GarageFlow.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Controllers;

/// <summary>
/// Service requests raised from the customer app, and the workshop's answers.
/// </summary>
/// <remarks>
/// A booking is only a request. It carries no mechanic, no parts and no money,
/// and it becomes real work only when staff convert it — at which point a job
/// card is created and the two are linked. Keeping the two apart means a
/// customer can never create a job card, only ask for one.
/// </remarks>
[Authorize]
[ApiController]
[Route("api/bookings")]
[Produces("application/json")]
public class BookingsController(
    GarageFlowDbContext db,
    CurrentUserService currentUser,
    NotificationService notifications,
    ActivityLog activity,
    TimeProvider clock) : ControllerBase
{
    /// <summary>
    /// Lists bookings. Staff see every booking; a customer sees only their own,
    /// whatever they ask for.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<PagedList<BookingDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedList<BookingDto>>>> List(
        [FromQuery] TableQuery query,
        [FromQuery] string? status,
        CancellationToken ct = default)
    {
        var bookings = db.Bookings.AsNoTracking().AsQueryable();

        // The scoping is applied here, not left to a query parameter, so there
        // is no way for a customer to widen it.
        var customerId = await currentUser.CustomerIdAsync(User, ct);
        if (customerId is not null)
            bookings = bookings.Where(b => b.CustomerId == customerId);

        if (!string.IsNullOrWhiteSpace(status))
            bookings = bookings.Where(b => b.Status == status);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            bookings = bookings.Where(b =>
                EF.Functions.Like(b.Id, $"%{term}%") ||
                EF.Functions.Like(b.Complaint, $"%{term}%") ||
                EF.Functions.Like(b.Customer!.Name, $"%{term}%") ||
                EF.Functions.Like(b.Vehicle!.Plate, $"%{term}%"));
        }

        var projected = bookings.ToDto().OrderByProperty(query.SortBy, query.Descending);

        if (string.IsNullOrWhiteSpace(query.SortBy))
            projected = projected.OrderByDescending(b => b.CreatedAt);

        var page = await projected.ToPagedListAsync(query, ct);

        return Ok(ApiResponse<PagedList<BookingDto>>.Ok(
            page,
            page.Count == 0 ? "No bookings found." : $"{page.Count} booking(s) found."));
    }

    /// <summary>One booking.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType<ApiResponse<BookingDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<BookingDto>>> Get(string id, CancellationToken ct)
    {
        var bookings = db.Bookings.AsNoTracking().Where(b => b.Id == id);

        var customerId = await currentUser.CustomerIdAsync(User, ct);
        if (customerId is not null)
            bookings = bookings.Where(b => b.CustomerId == customerId);

        var booking = await bookings.ToDto().FirstOrDefaultAsync(ct);

        if (booking is null)
            return NotFound(ApiResponse.Failure($"Booking '{id}' was not found."));

        return Ok(ApiResponse<BookingDto>.Ok(booking, "Booking loaded."));
    }

    /// <summary>Books a service. Customer app only.</summary>
    [HttpPost]
    [Authorize(Roles = Vocabulary.CustomerRole)]
    [ProducesResponseType<ApiResponse<BookingDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<BookingDto>>> Create(
        CreateBookingRequest request, CancellationToken ct)
    {
        var customerId = await currentUser.CustomerIdAsync(User, ct);

        if (customerId is null)
            return Forbid();

        // The vehicle must be one of theirs. Without this a customer could book
        // against any plate in the workshop by guessing an id.
        var vehicle = await db.Vehicles
            .FirstOrDefaultAsync(v => v.Id == request.VehicleId && v.CustomerId == customerId, ct);

        if (vehicle is null)
            return BadRequest(ApiResponse.Failure("That vehicle is not on your account."));

        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);

        if (request.PreferredDate < today)
            return BadRequest(ApiResponse.Failure("Choose a date from today onwards."));

        // One live request per vehicle: a customer tapping twice should not
        // leave the workshop two identical jobs to sift through.
        var alreadyOpen = await db.Bookings.AnyAsync(
            b => b.VehicleId == vehicle.Id && (b.Status == "Requested" || b.Status == "Confirmed"), ct);

        if (alreadyOpen)
            return BadRequest(ApiResponse.Failure(
                $"There is already an open booking for {vehicle.Plate}. Cancel it before making another."));

        // Quoted here, not at conversion time. The customer saw a number before
        // they tapped Request and the shop is held to it — see BookingService.
        var (extras, extraNames, extrasError) = await QuoteExtrasAsync(request.ServiceIds, vehicle, ct);

        if (extrasError is not null)
            return BadRequest(ApiResponse.Failure(extrasError));

        var booking = new Booking
        {
            Id = Ids.Next(await db.Bookings.Select(b => b.Id).ToListAsync(ct), "BKG"),
            CustomerId = customerId,
            VehicleId = vehicle.Id,
            Complaint = request.Complaint.Trim(),
            PreferredDate = request.PreferredDate,
            PreferredTime = request.PreferredTime.Trim(),
            Status = "Requested",
            CreatedAt = now,
            Services = extras,
        };

        db.Bookings.Add(booking);

        var user = await currentUser.GetAsync(User, ct);

        activity.Add($"Booking {booking.Id} requested for {vehicle.Plate}", "job");

        // The extras are the part staff can act on before the car arrives — a
        // ceramic coat needs a bay for a day — so they go in the notification
        // rather than being left to whoever opens the booking.
        var extrasLine = extraNames.Count == 0 ? "" : $" Extras: {string.Join(", ", extraNames)}.";

        await notifications.NotifyStaffAsync(
            user!.CompanyCode,
            "New service booking",
            $"{user.FullName} requested {booking.PreferredDate:dd MMM} for {vehicle.Plate}.{extrasLine}",
            "booking",
            booking.Id,
            ct);

        await db.SaveChangesAsync(ct);

        var dto = await db.Bookings.AsNoTracking().Where(b => b.Id == booking.Id).ToDto().FirstAsync(ct);

        return CreatedAtAction(
            nameof(Get),
            new { id = booking.Id },
            ApiResponse<BookingDto>.Ok(dto, $"Booking {booking.Id} requested. We will confirm shortly."));
    }

    /// <summary>Confirms or rejects a booking. Staff only.</summary>
    [HttpPut("{id}/respond")]
    [Authorize(Roles = "Owner,Manager,Advisor")]
    [ProducesResponseType<ApiResponse<BookingDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<BookingDto>>> Respond(
        string id, RespondToBookingRequest request, CancellationToken ct)
    {
        var booking = await db.Bookings
            .Include(b => b.Vehicle)
            .FirstOrDefaultAsync(b => b.Id == id, ct);

        if (booking is null)
            return NotFound(ApiResponse.Failure($"Booking '{id}' was not found."));

        if (booking.Status is not "Requested")
            return BadRequest(ApiResponse.Failure($"That booking is already {booking.Status}."));

        booking.Status = request.Status;
        booking.StaffNote = request.StaffNote?.Trim();
        booking.RespondedAt = clock.GetUtcNow().UtcDateTime;

        await notifications.NotifyCustomerAsync(
            booking.CustomerId,
            request.Status == "Confirmed" ? "Booking confirmed" : "Booking declined",
            request.Status == "Confirmed"
                ? $"See you on {booking.PreferredDate:dd MMM} for {booking.Vehicle!.Plate}."
                : booking.StaffNote ?? "Please get in touch to arrange another time.",
            "booking",
            booking.Id,
            ct);

        await db.SaveChangesAsync(ct);

        var dto = await db.Bookings.AsNoTracking().Where(b => b.Id == id).ToDto().FirstAsync(ct);

        return Ok(ApiResponse<BookingDto>.Ok(dto, $"Booking {booking.Id} {request.Status.ToLowerInvariant()}."));
    }

    /// <summary>
    /// Turns a confirmed booking into a real job card. Staff only.
    /// </summary>
    /// <remarks>
    /// The one place a booking becomes work. The complaint carries over, the
    /// promised date is the customer's preferred date, and the booking keeps a
    /// link to the job so the customer's history shows what came of their ask.
    /// </remarks>
    [HttpPost("{id}/convert")]
    [Authorize(Roles = "Owner,Manager,Advisor")]
    [ProducesResponseType<ApiResponse<JobCardDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<JobCardDto>>> Convert(
        string id, [FromQuery] string? mechanic, CancellationToken ct)
    {
        var booking = await db.Bookings
            .Include(b => b.Vehicle)
            .Include(b => b.Services)
            .ThenInclude(s => s.Service)
            .FirstOrDefaultAsync(b => b.Id == id, ct);

        if (booking is null)
            return NotFound(ApiResponse.Failure($"Booking '{id}' was not found."));

        if (booking.Status is "Converted")
            return BadRequest(ApiResponse.Failure($"Booking {booking.Id} is already job {booking.JobCardId}."));

        if (booking.Status is "Rejected" or "Cancelled")
            return BadRequest(ApiResponse.Failure($"That booking was {booking.Status.ToLowerInvariant()}."));

        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);

        var job = new JobCard
        {
            Id = Ids.Next(await db.JobCards.Select(j => j.Id).ToListAsync(ct), "JOB"),
            VehicleId = booking.VehicleId,
            Complaint = booking.Complaint,
            Status = "Open",
            Priority = "Normal",
            Mechanic = mechanic?.Trim() ?? "",
            Odometer = booking.Vehicle!.Odometer,
            CreatedAt = today,
            // The date the customer asked for is the date they are promised.
            PromisedAt = booking.PreferredDate < today ? today : booking.PreferredDate,
            // The extras arrive already priced, at what the customer was quoted
            // rather than at today's rate. The complaint stays unpriced — nobody
            // can quote a knocking noise before they have looked at it.
            Lines = JobServiceAppender.LinesFromBooking(booking),
        };

        db.JobCards.Add(job);

        booking.Status = "Converted";
        booking.JobCardId = job.Id;
        booking.RespondedAt ??= now;

        activity.Add($"Booking {booking.Id} became job {job.Id}", "job");

        await notifications.NotifyCustomerAsync(
            booking.CustomerId,
            "Your job card is open",
            $"Job {job.Id} has been opened for {booking.Vehicle.Plate}.",
            "job",
            job.Id,
            ct);

        if (!string.IsNullOrWhiteSpace(job.Mechanic))
        {
            await notifications.NotifyMechanicAsync(
                job.Mechanic,
                "New job assigned",
                $"Job {job.Id} — {booking.Vehicle.Plate}, due {job.PromisedAt:dd MMM}.",
                "job",
                job.Id,
                ct);
        }

        await db.SaveChangesAsync(ct);

        var dto = await db.JobCards.AsNoTracking().Where(j => j.Id == job.Id).ToDto().FirstAsync(ct);

        return CreatedAtAction(
            nameof(JobCardsController.Get),
            "JobCards",
            new { id = job.Id },
            ApiResponse<JobCardDto>.Ok(dto, $"Booking {booking.Id} became job {job.Id}."));
    }

    /// <summary>
    /// Cancels a booking. A customer may cancel their own; staff may cancel any.
    /// </summary>
    [HttpPut("{id}/cancel")]
    [ProducesResponseType<ApiResponse<BookingDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<BookingDto>>> Cancel(string id, CancellationToken ct)
    {
        var bookings = db.Bookings.Where(b => b.Id == id);

        var customerId = await currentUser.CustomerIdAsync(User, ct);
        if (customerId is not null)
            bookings = bookings.Where(b => b.CustomerId == customerId);

        var booking = await bookings.FirstOrDefaultAsync(ct);

        if (booking is null)
            return NotFound(ApiResponse.Failure($"Booking '{id}' was not found."));

        if (booking.Status is "Converted")
            return BadRequest(ApiResponse.Failure(
                $"That booking is already job {booking.JobCardId} — cancel the job instead."));

        if (booking.Status is "Cancelled")
            return BadRequest(ApiResponse.Failure("That booking is already cancelled."));

        booking.Status = "Cancelled";
        booking.RespondedAt = clock.GetUtcNow().UtcDateTime;

        await db.SaveChangesAsync(ct);

        var dto = await db.Bookings.AsNoTracking().Where(b => b.Id == id).ToDto().FirstAsync(ct);

        return Ok(ApiResponse<BookingDto>.Ok(dto, $"Booking {booking.Id} cancelled."));
    }

    /// <summary>
    /// Turns the customer's chosen extras into priced booking rows, or explains
    /// why one of them cannot be had.
    /// </summary>
    /// <remarks>
    /// Four rules, all checked here because the customer app is not the place to
    /// enforce any of them — an app can be old, and a request can be hand-made:
    ///
    /// <list type="bullet">
    /// <item>the service exists;</item>
    /// <item>it is still offered (<c>IsActive</c>);</item>
    /// <item>a customer may order it (<c>IsBookable</c>) — a courtesy wash is the
    /// shop's to give, not the customer's to demand;</item>
    /// <item>it is offered for this body class. Washing a bus and washing a
    /// scooter are different jobs at different prices, so the shop lists them
    /// separately and this stops the wrong one being picked.</item>
    /// </list>
    ///
    /// The message names the service rather than its id, because it is shown to
    /// the customer verbatim.
    /// </remarks>
    private async Task<(List<BookingService> Extras, List<string> Names, string? Error)> QuoteExtrasAsync(
        List<string> serviceIds, Vehicle vehicle, CancellationToken ct)
    {
        var wanted = serviceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (wanted.Count == 0) return ([], [], null);

        var found = await db.Services.AsNoTracking().Where(s => wanted.Contains(s.Id)).ToListAsync(ct);

        if (found.Count != wanted.Count)
        {
            var missing = wanted.Except(found.Select(s => s.Id), StringComparer.OrdinalIgnoreCase).First();
            return ([], [], $"Service '{missing}' does not exist.");
        }

        foreach (var service in found)
        {
            if (!service.IsActive || !service.IsBookable)
                return ([], [], $"'{service.Name}' is not available to book right now.");

            if (!service.AppliesToType(vehicle.Type))
                return ([], [], $"'{service.Name}' is not offered for a {vehicle.Type.ToLowerInvariant()}.");
        }

        // Ordered by the customer's own selection, so the estimate they were
        // shown reads back in the same order.
        var ordered = wanted
            .Select(id => found.First(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Only the id and the price are carried onto the row. Assigning the
        // Service navigation would attach a no-tracking instance to the graph
        // being added, and EF would try to INSERT the catalogue entry again.
        return (
            ordered.Select(s => new BookingService { ServiceId = s.Id, QuotedPrice = s.Price }).ToList(),
            ordered.Select(s => s.Name).ToList(),
            null);
    }
}
