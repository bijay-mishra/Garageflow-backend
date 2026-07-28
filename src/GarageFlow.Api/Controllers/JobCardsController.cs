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
public class JobCardsController(GarageFlowDbContext db, ActivityLog activity, TimeProvider clock) : ControllerBase
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

        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

        var job = new JobCard
        {
            Id = Ids.Next(await db.JobCards.Select(j => j.Id).ToListAsync(ct), "JOB", pad: 4),
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
        if (request.Mechanic is not null) job.Mechanic = request.Mechanic.Trim();
        if (request.Odometer is { } odometer) job.Odometer = odometer;
        if (request.PromisedAt is { } promisedAt) job.PromisedAt = promisedAt;

        if (request.Lines is not null)
        {
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
        }

        await db.SaveChangesAsync(ct);

        var dto = await db.JobCards.AsNoTracking().Where(j => j.Id == id).ToDto().FirstAsync(ct);

        return Ok(ApiResponse<JobCardDto>.Ok(
            dto,
            statusChanged ? $"Job card {job.Id} marked {job.Status}." : $"Job card {job.Id} updated successfully."));
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

        db.JobCards.Remove(job); // lines cascade
        activity.Add($"Job {job.Id} deleted", "job");
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse.Success($"Job card {job.Id} deleted successfully."));
    }

    private static List<JobLine> ToLines(IEnumerable<JobLineRequest> lines) =>
        lines.Select((l, index) => new JobLine
        {
            Description = l.Description.Trim(),
            Qty = l.Qty,
            UnitPrice = l.UnitPrice,
            Kind = l.Kind,
            SortOrder = index,
        }).ToList();
}
