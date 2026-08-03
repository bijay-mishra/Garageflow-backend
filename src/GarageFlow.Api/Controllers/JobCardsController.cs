using GarageFlow.Api.Contracts;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using GarageFlow.Api.Mapping;
using GarageFlow.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Controllers;

/// <summary>Repair orders — the workshop's core workflow.</summary>
[Authorize]
[ApiController]
[Route("api/job-cards")]
[Produces("application/json")]
public class JobCardsController(
    GarageFlowDbContext db,
    ActivityLog activity,
    NotificationService notifications,
    JobServiceAppender serviceLines,
    DeliveryService deliveries,
    PhotoStorage photos,
    WorkspaceService workspace,
    TimeProvider clock) : ControllerBase
{
    /// <summary>Lists job cards, newest first.</summary>
    /// <remarks>
    /// Paged with <c>skip</c>/<c>take</c>; omit <c>take</c> for every row.
    /// Search matches id, plate, customer name, mechanic or complaint.
    /// <c>status</c> filters to a single job status.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<ApiResponse<PagedList<JobCardDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedList<JobCardDto>>>> List(
        [FromQuery] TableQuery query, [FromQuery] string? status, CancellationToken ct)
    {
        var jobs = db.JobCards.AsNoTracking().AsQueryable();

        // The accounting year the topbar is set to. Applied before anything
        // else, so the count on the pager is the count within the year rather
        // than a total the table then contradicts.
        if (await workspace.StaffYearAsync(User, ct) is { } year)
            jobs = jobs.Where(j => j.CreatedAt >= year.Start && j.CreatedAt <= year.End);

        if (!string.IsNullOrWhiteSpace(status))
            jobs = jobs.Where(j => j.Status == status);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            jobs = jobs.Where(j =>
                EF.Functions.Like(j.Id, $"%{term}%") ||
                EF.Functions.Like(j.Vehicle!.Plate, $"%{term}%") ||
                EF.Functions.Like(j.Vehicle.Customer!.Name, $"%{term}%") ||
                EF.Functions.Like(j.Mechanic, $"%{term}%") ||
                EF.Functions.Like(j.Complaint, $"%{term}%"));
        }

        var projected = jobs.ToDto().OrderByProperty(query.SortBy, query.Descending);

        if (string.IsNullOrWhiteSpace(query.SortBy))
            projected = projected.OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id);

        var page = await projected.ToPagedListAsync(query, ct);

        return Ok(ApiResponse<PagedList<JobCardDto>>.Ok(
            page,
            page.Count == 0 ? "No job cards found." : $"{page.Count} job card(s) found."));
    }

    /// <summary>Gets one job card with its lines.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType<ApiResponse<JobCardDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<JobCardDto>>> Get(string id, CancellationToken ct)
    {
        var job = await db.JobCards.AsNoTracking().Where(j => j.Id == id).ToDto().FirstOrDefaultAsync(ct);

        if (job is null)
            return NotFound(ApiResponse.Failure($"Job card '{id}' was not found."));

        return Ok(ApiResponse<JobCardDto>.Ok(job, "Job card loaded."));
    }

    /// <summary>
    /// Opens a job card against a vehicle. The plate, vehicle label, customer
    /// and line total are all derived here, so the client never sends them.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<ApiResponse<JobCardDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<JobCardDto>>> Create(
        CreateJobCardRequest request, CancellationToken ct)
    {
        var vehicle = await db.Vehicles.AsNoTracking().FirstOrDefaultAsync(v => v.Id == request.VehicleId, ct);

        if (vehicle is null)
            return BadRequest(ApiResponse.Failure($"Vehicle '{request.VehicleId}' does not exist."));

        if (await UnknownServiceAsync(request.Lines, ct) is { } unknown)
            return BadRequest(ApiResponse.Failure(unknown));

        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

        var job = new JobCard
        {
            Id = Ids.Next(await db.JobCards.IgnoreQueryFilters().Select(j => j.Id).ToListAsync(ct), "JOB", pad: 4),
            VehicleId = request.VehicleId,
            Complaint = request.Complaint.Trim(),
            Status = request.Status,
            Priority = request.Priority,
            Mechanic = request.Mechanic.Trim(),
            Odometer = request.Odometer,
            CreatedAt = today,
            PromisedAt = request.PromisedAt == default ? today : request.PromisedAt,
            CompletedAt = Vocabulary.DoneJobStatuses.Contains(request.Status) ? today : null,
            Lines = ToLines(request.Lines),
        };

        db.JobCards.Add(job);

        // A job card is also the moment we learn the car's current mileage.
        if (request.Odometer > vehicle.Odometer)
        {
            var tracked = await db.Vehicles.FirstAsync(v => v.Id == vehicle.Id, ct);
            tracked.Odometer = request.Odometer;
        }

        activity.Add($"Job {job.Id} opened for {vehicle.Label}", "job");
        await db.SaveChangesAsync(ct);

        var dto = await db.JobCards.AsNoTracking().Where(j => j.Id == job.Id).ToDto().FirstAsync(ct);

        return CreatedAtAction(
            nameof(Get),
            new { id = job.Id },
            ApiResponse<JobCardDto>.Ok(dto, $"Job card {job.Id} created for {vehicle.Label}."));
    }

    /// <summary>
    /// Updates a job card. Only the fields present in the body are applied;
    /// sending <c>lines</c> replaces the whole set.
    /// </summary>
    /// <remarks>
    /// Moving into Completed or Delivered stamps <c>completedAt</c> with today's
    /// date if it is not already set.
    /// </remarks>
    [HttpPut("{id}")]
    [ProducesResponseType<ApiResponse<JobCardDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<JobCardDto>>> Update(
        string id, UpdateJobCardRequest request, CancellationToken ct)
    {
        var job = await db.JobCards.Include(j => j.Lines).FirstOrDefaultAsync(j => j.Id == id, ct);

        if (job is null)
            return NotFound(ApiResponse.Failure($"Job card '{id}' was not found."));

        if (request.VehicleId is not null)
        {
            if (!await db.Vehicles.AnyAsync(v => v.Id == request.VehicleId, ct))
                return BadRequest(ApiResponse.Failure($"Vehicle '{request.VehicleId}' does not exist."));
            job.VehicleId = request.VehicleId;
        }

        if (request.Complaint is not null) job.Complaint = request.Complaint.Trim();
        if (request.Priority is not null) job.Priority = request.Priority;
        if (request.Odometer is { } odometer) job.Odometer = odometer;
        if (request.PromisedAt is { } promisedAt) job.PromisedAt = promisedAt;

        // Reassignment tells the newly-assigned mechanic they have work — the
        // mechanic app has no other way to learn about a job it was just given.
        if (request.Mechanic is not null)
        {
            var newMechanic = request.Mechanic.Trim();

            if (!string.Equals(newMechanic, job.Mechanic, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(newMechanic))
            {
                await notifications.NotifyMechanicAsync(
                    newMechanic,
                    "New job assigned",
                    $"Job {job.Id} — due {job.PromisedAt:dd MMM}.",
                    "job",
                    job.Id,
                    ct);
            }

            job.Mechanic = newMechanic;
        }

        if (request.Lines is not null)
        {
            if (await UnknownServiceAsync(request.Lines, ct) is { } unknown)
                return BadRequest(ApiResponse.Failure(unknown));

            db.JobLines.RemoveRange(job.Lines);
            job.Lines = ToLines(request.Lines);
        }

        // Status changes are the notable event, so they drive the message.
        var statusChanged = request.Status is not null && request.Status != job.Status;

        if (statusChanged)
        {
            job.Status = request.Status!;

            if (Vocabulary.DoneJobStatuses.Contains(job.Status))
                job.CompletedAt ??= DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

            activity.Add($"{job.Id} marked {job.Status}", "job");

            // The customer app shows this as a push-style alert, so a status
            // change made from the dashboard has to reach it too — otherwise the
            // customer only hears about work the mechanic touched.
            var owner = await db.Vehicles.AsNoTracking()
                .Where(v => v.Id == job.VehicleId)
                .Select(v => new { v.CustomerId, v.Plate })
                .FirstOrDefaultAsync(ct);

            if (owner is not null)
            {
                await notifications.NotifyCustomerAsync(
                    owner.CustomerId,
                    $"Your vehicle is now {job.Status}",
                    $"{owner.Plate} — job {job.Id}.",
                    "job",
                    job.Id,
                    ct);
            }

            // Finishing the work is the moment the customer has a decision to
            // make: come and get it, or have it brought round. Opening the
            // handover here means they are asked once, automatically, rather
            // than when somebody at the desk remembers to ring them.
            if (job.Status == "Completed")
                await deliveries.OpenAsync(job, ct);
        }

        await db.SaveChangesAsync(ct);

        var dto = await db.JobCards.AsNoTracking().Where(j => j.Id == id).ToDto().FirstAsync(ct);

        return Ok(ApiResponse<JobCardDto>.Ok(
            dto,
            statusChanged ? $"Job card {job.Id} marked {job.Status}." : $"Job card {job.Id} updated successfully."));
    }

    /// <summary>
    /// Adds services from the price list to a job card — "and give it a wash
    /// while it is in".
    /// </summary>
    /// <remarks>
    /// Only ever appends. <c>PUT /api/job-cards/{id}</c> replaces the entire line
    /// set, so a client holding a stale copy would wipe whatever the mechanic
    /// added from the app five minutes ago; this cannot. Services already on the
    /// job are skipped rather than duplicated, and the response says which.
    ///
    /// Prices come from the catalogue. Edit the line afterwards to discount it —
    /// the line is the record of what was charged, not the price list.
    /// </remarks>
    [HttpPost("{id}/services")]
    [ProducesResponseType<ApiResponse<JobCardDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<JobCardDto>>> AddServices(
        string id, AddJobServicesRequest request, CancellationToken ct)
    {
        var job = await db.JobCards.Include(j => j.Lines).FirstOrDefaultAsync(j => j.Id == id, ct);

        if (job is null)
            return NotFound(ApiResponse.Failure($"Job card '{id}' was not found."));

        var result = await serviceLines.AppendAsync(job, request.ServiceIds, ct: ct);

        if (result.Error is not null)
            return BadRequest(ApiResponse.Failure(result.Error));

        if (result.Added.Count == 0)
        {
            return BadRequest(ApiResponse.Failure(
                $"Job {job.Id} already has {string.Join(", ", result.AlreadyOn)}."));
        }

        activity.Add(
            $"{string.Join(", ", result.Added.Select(l => l.Description))} added to job {job.Id}",
            "job");

        await db.SaveChangesAsync(ct);

        var dto = await db.JobCards.AsNoTracking().Where(j => j.Id == id).ToDto().FirstAsync(ct);

        var skipped = result.AlreadyOn.Count == 0
            ? ""
            : $" {string.Join(", ", result.AlreadyOn)} was already on it.";

        return Ok(ApiResponse<JobCardDto>.Ok(
            dto,
            $"{result.Added.Count} service(s) added to {job.Id}.{skipped}"));
    }

    /// <summary>Deletes a job card and its lines.</summary>
    [HttpDelete("{id}")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse>> Delete(string id, CancellationToken ct)
    {
        var job = await db.JobCards.FirstOrDefaultAsync(j => j.Id == id, ct);

        if (job is null)
            return NotFound(ApiResponse.Failure($"Job card '{id}' was not found."));

        db.JobCards.Remove(job); // lines and photo rows cascade
        activity.Add($"Job {job.Id} deleted", "job");
        await db.SaveChangesAsync(ct);

        // The rows cascade, the files do not — without this the photos would sit
        // on disk forever with nothing left in the database pointing at them.
        photos.DeleteJobFolder(job.Id);

        return Ok(ApiResponse.Success($"Job card {job.Id} deleted successfully."));
    }

    private static List<JobLine> ToLines(IEnumerable<JobLineRequest> lines) =>
        lines.Select((l, index) => new JobLine
        {
            Description = l.Description.Trim(),
            Qty = l.Qty,
            UnitPrice = l.UnitPrice,
            Kind = l.Kind,
            // Carried through as sent. The client picked the description and
            // price off the catalogue; keeping the id lets the shop report on
            // what each service earned, without stopping anyone editing the row.
            ServiceId = string.IsNullOrWhiteSpace(l.ServiceId) ? null : l.ServiceId.Trim(),
            SortOrder = index,
        }).ToList();

    /// <summary>
    /// Null when every <c>serviceId</c> on the request exists, else the message
    /// to return.
    /// </summary>
    /// <remarks>
    /// Checked up front rather than left to the foreign key: a constraint
    /// violation surfaces as a 500 with a SQL Server message in it, and the
    /// client cannot tell the user which line is wrong.
    /// </remarks>
    private async Task<string?> UnknownServiceAsync(
        IEnumerable<JobLineRequest> lines, CancellationToken ct)
    {
        var ids = lines
            .Select(l => l.ServiceId?.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToList();

        if (ids.Count == 0) return null;

        var known = await db.Services.Where(s => ids.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct);
        var missing = ids.Except(known).FirstOrDefault();

        return missing is null ? null : $"Service '{missing}' does not exist.";
    }
}
