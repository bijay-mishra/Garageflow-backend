using GarageFlow.Api.Contracts;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using GarageFlow.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Controllers;

/// <summary>
/// Handing a finished vehicle back — collection or home delivery, and following
/// the driver while it is on the way.
/// </summary>
/// <remarks>
/// One controller for all three audiences, because they are all looking at the
/// same record from different angles: staff see every handover, a customer sees
/// only their own, and a driver acts on the ones assigned to them. The scoping
/// is applied per action rather than by splitting the routes, so there is one
/// place to check that a customer cannot follow somebody else's car.
/// </remarks>
[Authorize]
[ApiController]
[Route("api/deliveries")]
[Produces("application/json")]
public class DeliveriesController(
    GarageFlowDbContext db,
    DeliveryService deliveries,
    CurrentUserService currentUser,
    TimeProvider clock) : ControllerBase
{
    /// <summary>
    /// Lists handovers. Staff see all; a customer sees only their own.
    /// </summary>
    /// <remarks>
    /// <c>status</c> narrows to one; <c>active</c> (the default) hides finished
    /// and cancelled ones, which is what both the dashboard map and the
    /// customer's app want.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<ApiResponse<PagedList<DeliveryDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedList<DeliveryDto>>>> List(
        [FromQuery] TableQuery query,
        [FromQuery] string? status,
        [FromQuery] bool active = true,
        CancellationToken ct = default)
    {
        var rows = db.Deliveries.AsNoTracking().AsQueryable();

        var customerId = await currentUser.CustomerIdAsync(User, ct);
        if (customerId is not null) rows = rows.Where(d => d.CustomerId == customerId);

        if (!string.IsNullOrWhiteSpace(status))
            rows = rows.Where(d => d.Status == status);
        else if (active)
            rows = rows.Where(d => d.Status != "Delivered" && d.Status != "Cancelled");

        var projected = ToDto(rows).OrderByProperty(query.SortBy, query.Descending);

        if (string.IsNullOrWhiteSpace(query.SortBy))
        {
            // Working order: what is moving, then what is waiting on the
            // customer, then everything else.
            projected = projected
                .OrderByDescending(d => d.Status == "OutForDelivery")
                .ThenByDescending(d => d.Status == "AwaitingChoice")
                .ThenByDescending(d => d.CreatedAt);
        }

        var page = await projected.ToPagedListAsync(query, ct);

        return Ok(ApiResponse<PagedList<DeliveryDto>>.Ok(
            page,
            page.Count == 0 ? "Nothing waiting to go out." : $"{page.Count} handover(s)."));
    }

    /// <summary>One handover.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType<ApiResponse<DeliveryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<DeliveryDto>>> Get(string id, CancellationToken ct)
    {
        var dto = await ScopedAsync(id, ct);

        if (dto is null)
            return NotFound(ApiResponse.Failure($"Handover '{id}' was not found."));

        return Ok(ApiResponse<DeliveryDto>.Ok(dto, "Handover loaded."));
    }

    /// <summary>
    /// What home delivery would cost, before committing to it.
    /// </summary>
    /// <remarks>
    /// Separate from choosing so the app can show a price next to the button
    /// rather than after it. Quoting twice is free and changes nothing.
    /// </remarks>
    [HttpGet("{id}/quote")]
    [ProducesResponseType<ApiResponse<DeliveryQuoteDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<DeliveryQuoteDto>>> QuoteFor(string id, CancellationToken ct)
    {
        var delivery = await FindScopedAsync(id, ct);

        if (delivery is null)
            return NotFound(ApiResponse.Failure($"Handover '{id}' was not found."));

        var job = await db.JobCards.AsNoTracking().Include(j => j.Lines)
            .FirstOrDefaultAsync(j => j.Id == delivery.JobCardId, ct);

        if (job is null)
            return NotFound(ApiResponse.Failure("The job for this handover no longer exists."));

        var quote = await deliveries.QuoteAsync(job, delivery.CustomerId, ct);

        var dto = new DeliveryQuoteDto
        {
            Available = quote.Ok,
            DistanceKm = Math.Round(quote.DistanceKm, 2),
            Fee = quote.Fee,
            Reason = quote.Error,
        };

        return Ok(ApiResponse<DeliveryQuoteDto>.Ok(
            dto,
            quote.Ok
                ? quote.Fee > 0
                    ? $"{quote.DistanceKm:N1} km — Rs {quote.Fee:N0} to deliver."
                    : $"{quote.DistanceKm:N1} km — free delivery on this bill."
                : quote.Error!));
    }

    /// <summary>
    /// The customer says how they want the vehicle back.
    /// </summary>
    /// <remarks>
    /// Staff can also set it, because plenty of customers will say it over the
    /// phone. Either way the fee is fixed at this moment and not recomputed.
    /// </remarks>
    [HttpPost("{id}/choose")]
    [ProducesResponseType<ApiResponse<DeliveryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<DeliveryDto>>> Choose(
        string id, ChooseDeliveryRequest request, CancellationToken ct)
    {
        var delivery = await FindScopedAsync(id, ct, tracked: true);

        if (delivery is null)
            return NotFound(ApiResponse.Failure($"Handover '{id}' was not found."));

        var (updated, message) = await deliveries.ChooseAsync(delivery, request.Method, ct);

        if (updated is null)
            return BadRequest(ApiResponse.Failure(message));

        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<DeliveryDto>.Ok((await ScopedAsync(id, ct))!, message));
    }

    /// <summary>The driver sets off. Staff or the assigned mechanic.</summary>
    [HttpPost("{id}/start")]
    [Authorize(Roles = "Owner,Manager,Advisor,Mechanic")]
    [ProducesResponseType<ApiResponse<DeliveryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<DeliveryDto>>> Start(
        string id, StartDeliveryRequest request, CancellationToken ct)
    {
        var delivery = await db.Deliveries.FirstOrDefaultAsync(d => d.Id == id, ct);

        if (delivery is null)
            return NotFound(ApiResponse.Failure($"Handover '{id}' was not found."));

        // A mechanic always drives as themselves. Letting the request name the
        // driver would let one person log a trip under another's name.
        var mechanicName = await currentUser.MechanicNameAsync(User, ct);
        var driver = mechanicName ?? request.Driver?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(driver))
            return BadRequest(ApiResponse.Failure("Say who is driving."));

        var error = await deliveries.StartAsync(delivery, driver, ct);

        if (error is not null) return BadRequest(ApiResponse.Failure(error));

        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<DeliveryDto>.Ok(
            (await ScopedAsync(id, ct))!, $"{driver} is on the way. Tracking is live."));
    }

    /// <summary>
    /// The driver's phone reports where it is.
    /// </summary>
    /// <remarks>
    /// Called every few seconds while the app is open, so it is deliberately the
    /// cheapest endpoint here — one row written, one row updated, no projection
    /// built and nothing returned but a confirmation.
    ///
    /// Only the assigned driver may ping. Otherwise anyone signed in could push
    /// a car's position anywhere on the map.
    /// </remarks>
    [HttpPost("{id}/ping")]
    [Authorize(Roles = "Owner,Manager,Advisor,Mechanic")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse>> Ping(
        string id, DeliveryPingRequest request, CancellationToken ct)
    {
        var delivery = await db.Deliveries.FirstOrDefaultAsync(d => d.Id == id, ct);

        if (delivery is null)
            return NotFound(ApiResponse.Failure($"Handover '{id}' was not found."));

        var mechanicName = await currentUser.MechanicNameAsync(User, ct);

        if (mechanicName is not null && delivery.Driver != mechanicName)
            return NotFound(ApiResponse.Failure($"Handover '{id}' was not found."));

        var error = await deliveries.PingAsync(
            delivery, request.Latitude, request.Longitude, request.AccuracyMetres, ct);

        if (error is not null) return BadRequest(ApiResponse.Failure(error));

        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse.Success("Position recorded."));
    }

    /// <summary>The vehicle is handed over.</summary>
    [HttpPost("{id}/complete")]
    [Authorize(Roles = "Owner,Manager,Advisor,Mechanic")]
    [ProducesResponseType<ApiResponse<DeliveryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<DeliveryDto>>> Complete(string id, CancellationToken ct)
    {
        var delivery = await db.Deliveries.FirstOrDefaultAsync(d => d.Id == id, ct);

        if (delivery is null)
            return NotFound(ApiResponse.Failure($"Handover '{id}' was not found."));

        var mechanicName = await currentUser.MechanicNameAsync(User, ct);

        if (mechanicName is not null && delivery.Driver != mechanicName && delivery.Driver != "")
            return NotFound(ApiResponse.Failure($"Handover '{id}' was not found."));

        var error = await deliveries.CompleteAsync(delivery, ct);

        if (error is not null) return BadRequest(ApiResponse.Failure(error));

        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<DeliveryDto>.Ok((await ScopedAsync(id, ct))!, "Handed over. Nice work."));
    }

    /// <summary>
    /// Where the driver is, and the route so far.
    /// </summary>
    /// <remarks>
    /// Polled by both the dashboard map and the customer following their own
    /// car. <c>secondsSinceUpdate</c> is the honest part: tracking only runs
    /// while the driver has the app open, so a position can be minutes old and
    /// the client has to be able to say so rather than showing a stale dot as
    /// though it were live.
    /// </remarks>
    [HttpGet("{id}/track")]
    [ProducesResponseType<ApiResponse<DeliveryTrackDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<DeliveryTrackDto>>> Track(string id, CancellationToken ct)
    {
        var dto = await ScopedAsync(id, ct);

        if (dto is null)
            return NotFound(ApiResponse.Failure($"Handover '{id}' was not found."));

        var trail = await db.DeliveryPoints.AsNoTracking()
            .Where(p => p.DeliveryId == id)
            .OrderBy(p => p.At)
            .Select(p => new DeliveryPointDto(p.Latitude, p.Longitude, p.At))
            .ToListAsync(ct);

        var origin = await db.Workshops.AsNoTracking()
            .Where(w => w.Latitude != null)
            .Select(w => new { w.Latitude, w.Longitude })
            .FirstOrDefaultAsync(ct);

        var now = clock.GetUtcNow().UtcDateTime;

        var track = new DeliveryTrackDto
        {
            Delivery = dto,
            OriginLatitude = origin?.Latitude,
            OriginLongitude = origin?.Longitude,
            Trail = trail,
            SecondsSinceUpdate = dto.DriverAt is { } at ? (int)(now - at).TotalSeconds : null,
        };

        return Ok(ApiResponse<DeliveryTrackDto>.Ok(track, "Tracking loaded."));
    }

    // ── Scoping ──────────────────────────────────────────────────────────────

    /// <summary>The delivery, if this caller is allowed to see it.</summary>
    private async Task<Delivery?> FindScopedAsync(string id, CancellationToken ct, bool tracked = false)
    {
        var rows = tracked ? db.Deliveries : db.Deliveries.AsNoTracking();
        var query = rows.Where(d => d.Id == id);

        var customerId = await currentUser.CustomerIdAsync(User, ct);
        if (customerId is not null) query = query.Where(d => d.CustomerId == customerId);

        return await query.FirstOrDefaultAsync(ct);
    }

    private async Task<DeliveryDto?> ScopedAsync(string id, CancellationToken ct)
    {
        var query = db.Deliveries.AsNoTracking().Where(d => d.Id == id);

        var customerId = await currentUser.CustomerIdAsync(User, ct);
        if (customerId is not null) query = query.Where(d => d.CustomerId == customerId);

        return await ToDto(query).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Projection kept an expression tree so the list can page and sort in SQL.
    /// </summary>
    /// <remarks>
    /// The vehicle comes through the job card rather than being stored on the
    /// delivery: a handover is about a job, and the job already knows which car.
    /// </remarks>
    private static IQueryable<DeliveryDto> ToDto(IQueryable<Delivery> source) =>
        source.Select(d => new DeliveryDto
        {
            Id = d.Id,
            JobCardId = d.JobCardId,
            CustomerId = d.CustomerId,
            CustomerName = d.Customer!.Name,
            CustomerPhone = d.Customer.Phone,
            VehiclePlate = d.JobCard!.Vehicle!.Plate,
            VehicleLabel = d.JobCard.Vehicle.Make + " " + d.JobCard.Vehicle.Model + " " + d.JobCard.Vehicle.Year,
            Method = d.Method,
            Status = d.Status,
            Address = d.Address,
            Latitude = d.Latitude,
            Longitude = d.Longitude,
            DistanceKm = d.DistanceKm,
            Fee = d.Fee,
            Driver = d.Driver,
            DriverLatitude = d.DriverLatitude,
            DriverLongitude = d.DriverLongitude,
            DriverAt = d.DriverAt,
            CreatedAt = d.CreatedAt,
            ChosenAt = d.ChosenAt,
            StartedAt = d.StartedAt,
            CompletedAt = d.CompletedAt,
        });
}
