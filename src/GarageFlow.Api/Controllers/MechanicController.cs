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
/// The mechanic app's view of the workshop: the jobs assigned to the signed-in
/// mechanic, and the two things they can do to one.
/// </summary>
/// <remarks>
/// Every action here scopes to the caller's own <c>MechanicName</c>, read from
/// the database rather than the token — see <see cref="CurrentUserService"/>.
/// There is no endpoint that takes a mechanic name as a parameter, so one
/// mechanic cannot ask for another's work by changing a query string.
/// </remarks>
[Authorize(Roles = Vocabulary.MechanicRole)]
[ApiController]
[Route("api/mechanic")]
[Produces("application/json")]
public class MechanicController(
    GarageFlowDbContext db,
    CurrentUserService currentUser,
    NotificationService notifications,
    JobServiceAppender serviceLines,
    DeliveryService deliveries,
    ActivityLog activity,
    TimeProvider clock) : ControllerBase
{
    /// <summary>Jobs assigned to the signed-in mechanic.</summary>
    /// <remarks>
    /// Ordered the way a mechanic works: overdue first, then by promised date,
    /// then by priority. <c>status</c> narrows to one status; <c>active</c>
    /// (the default) hides finished and cancelled work.
    /// </remarks>
    [HttpGet("jobs")]
    [ProducesResponseType<ApiResponse<PagedList<MechanicJobDto>>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<PagedList<MechanicJobDto>>>> Jobs(
        [FromQuery] TableQuery query,
        [FromQuery] string? status,
        [FromQuery] bool active = true,
        CancellationToken ct = default)
    {
        var mechanicName = await currentUser.MechanicNameAsync(User, ct);

        if (mechanicName is null)
            return Forbid();

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var jobs = db.JobCards.AsNoTracking().Where(j => j.Mechanic == mechanicName);

        if (!string.IsNullOrWhiteSpace(status))
            jobs = jobs.Where(j => j.Status == status);
        else if (active)
            jobs = jobs.Where(j => Vocabulary.OpenJobStatuses.Contains(j.Status));

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            jobs = jobs.Where(j =>
                EF.Functions.Like(j.Id, $"%{term}%") ||
                EF.Functions.Like(j.Vehicle!.Plate, $"%{term}%") ||
                EF.Functions.Like(j.Vehicle.Make, $"%{term}%") ||
                EF.Functions.Like(j.Vehicle.Model, $"%{term}%") ||
                EF.Functions.Like(j.Vehicle.Customer!.Name, $"%{term}%"));
        }

        var projected = jobs.ToMechanicDto(today);

        projected = string.IsNullOrWhiteSpace(query.SortBy)
            // The default order is the working order: what is late, then what is
            // due soonest, then what is most urgent.
            ? projected
                .OrderByDescending(j => j.IsOverdue)
                .ThenBy(j => j.PromisedAt)
                .ThenByDescending(j => j.Priority == "Urgent")
                .ThenByDescending(j => j.Priority == "High")
            : projected.OrderByProperty(query.SortBy, query.Descending);

        var page = await projected.ToPagedListAsync(query, ct);

        return Ok(ApiResponse<PagedList<MechanicJobDto>>.Ok(
            page,
            page.Count == 0 ? "No jobs assigned to you." : $"{page.Count} job(s) assigned to you."));
    }

    /// <summary>Counts for the tiles above the mechanic's job list.</summary>
    [HttpGet("summary")]
    [ProducesResponseType<ApiResponse<MechanicSummaryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<MechanicSummaryDto>>> Summary(CancellationToken ct)
    {
        var mechanicName = await currentUser.MechanicNameAsync(User, ct);

        if (mechanicName is null)
            return Forbid();

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var mine = db.JobCards.AsNoTracking().Where(j => j.Mechanic == mechanicName);

        // One round trip rather than five counts: the whole summary is a single
        // grouped aggregate over the same filtered set.
        var summary = new MechanicSummaryDto(
            AssignedTotal: await mine.CountAsync(j => Vocabulary.OpenJobStatuses.Contains(j.Status), ct),
            InProgress: await mine.CountAsync(j => j.Status == "In Progress", ct),
            AwaitingParts: await mine.CountAsync(j => j.Status == "Awaiting Parts", ct),
            CompletedToday: await mine.CountAsync(j => j.CompletedAt == today, ct),
            Overdue: await mine.CountAsync(
                j => j.PromisedAt < today && Vocabulary.OpenJobStatuses.Contains(j.Status), ct));

        return Ok(ApiResponse<MechanicSummaryDto>.Ok(summary, "Summary loaded."));
    }

    /// <summary>One assigned job, with its lines.</summary>
    [HttpGet("jobs/{id}")]
    [ProducesResponseType<ApiResponse<MechanicJobDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MechanicJobDto>>> Job(string id, CancellationToken ct)
    {
        var mechanicName = await currentUser.MechanicNameAsync(User, ct);

        if (mechanicName is null)
            return Forbid();

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var job = await db.JobCards.AsNoTracking()
            .Where(j => j.Id == id && j.Mechanic == mechanicName)
            .ToMechanicDto(today)
            .FirstOrDefaultAsync(ct);

        // A job that exists but belongs to someone else answers exactly as one
        // that does not exist, so this cannot be used to enumerate job ids.
        if (job is null)
            return NotFound(ApiResponse.Failure($"Job '{id}' was not found among your assigned jobs."));

        return Ok(ApiResponse<MechanicJobDto>.Ok(job, "Job loaded."));
    }

    /// <summary>Moves an assigned job to a new status.</summary>
    /// <remarks>
    /// Completing or delivering stamps <c>CompletedAt</c>, exactly as the
    /// dashboard does — the date must not depend on which client made the
    /// change. The customer is notified if they have an app login.
    /// </remarks>
    [HttpPut("jobs/{id}/status")]
    [ProducesResponseType<ApiResponse<MechanicJobDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MechanicJobDto>>> UpdateStatus(
        string id, UpdateJobStatusRequest request, CancellationToken ct)
    {
        var mechanicName = await currentUser.MechanicNameAsync(User, ct);

        if (mechanicName is null)
            return Forbid();

        var job = await db.JobCards
            .Include(j => j.Vehicle)
            .FirstOrDefaultAsync(j => j.Id == id && j.Mechanic == mechanicName, ct);

        if (job is null)
            return NotFound(ApiResponse.Failure($"Job '{id}' was not found among your assigned jobs."));

        if (job.Status == request.Status && request.Odometer is null && string.IsNullOrWhiteSpace(request.Note))
            return BadRequest(ApiResponse.Failure($"This job is already {request.Status}."));

        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var previousStatus = job.Status;

        job.Status = request.Status;

        // Stamped on the way in, cleared on the way back out — reopening a job
        // that was finished by mistake must not leave a completion date behind.
        if (Vocabulary.DoneJobStatuses.Contains(job.Status))
            job.CompletedAt ??= today;
        else
            job.CompletedAt = null;

        if (request.Odometer is { } odometer)
        {
            job.Odometer = odometer;

            // The vehicle's reading only ever goes up: a mistyped low number on
            // one job should not rewrite the vehicle's history.
            if (job.Vehicle is not null && odometer > job.Vehicle.Odometer)
                job.Vehicle.Odometer = odometer;
        }

        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            // Appended rather than replacing: the complaint is the customer's
            // words, and the mechanic's note is a second voice on the same card.
            var note = request.Note.Trim();
            job.Complaint = string.IsNullOrWhiteSpace(job.Complaint)
                ? note
                : $"{job.Complaint}\n[{today:yyyy-MM-dd} {mechanicName}] {note}";
        }

        activity.Add($"Job {job.Id} marked {job.Status} by {mechanicName}", "job");

        // Same rule as the dashboard: finishing the work is what asks the
        // customer how they want the car back. It must not depend on which
        // client marked it done.
        if (job.Status == "Completed")
            await deliveries.OpenAsync(job, ct);

        if (previousStatus != job.Status && job.Vehicle is not null)
        {
            await notifications.NotifyCustomerAsync(
                job.Vehicle.CustomerId,
                $"Your vehicle is now {job.Status}",
                $"{job.Vehicle.Make} {job.Vehicle.Model} ({job.Vehicle.Plate}) — job {job.Id}.",
                "job",
                job.Id,
                ct);
        }

        await db.SaveChangesAsync(ct);

        var dto = await db.JobCards.AsNoTracking()
            .Where(j => j.Id == id)
            .ToMechanicDto(today)
            .FirstAsync(ct);

        return Ok(ApiResponse<MechanicJobDto>.Ok(dto, $"Job {job.Id} marked {job.Status}."));
    }

    /// <summary>
    /// Adds services from the workshop's price list to an assigned job — the
    /// mechanic sees the state of the car and puts a wash on it.
    /// </summary>
    /// <remarks>
    /// The mechanic chooses <em>which</em> service, never what it costs: the
    /// price is copied from the catalogue, and there is no field on this request
    /// to override it. That is the whole reason this is a separate endpoint
    /// rather than letting the app edit job lines.
    ///
    /// The customer is notified, because they are about to be charged more than
    /// they agreed to. Finding out at the counter is how a shop loses someone.
    /// </remarks>
    [HttpPost("jobs/{id}/services")]
    [ProducesResponseType<ApiResponse<MechanicJobDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MechanicJobDto>>> AddServices(
        string id, AddJobServicesRequest request, CancellationToken ct)
    {
        var mechanicName = await currentUser.MechanicNameAsync(User, ct);

        if (mechanicName is null)
            return Forbid();

        var job = await db.JobCards
            .Include(j => j.Lines)
            .Include(j => j.Vehicle)
            .FirstOrDefaultAsync(j => j.Id == id && j.Mechanic == mechanicName, ct);

        if (job is null)
            return NotFound(ApiResponse.Failure($"Job '{id}' was not found among your assigned jobs."));

        var result = await serviceLines.AppendAsync(job, request.ServiceIds, ct: ct);

        if (result.Error is not null)
            return BadRequest(ApiResponse.Failure(result.Error));

        if (result.Added.Count == 0)
        {
            return BadRequest(ApiResponse.Failure(
                $"This job already has {string.Join(", ", result.AlreadyOn)}."));
        }

        var names = string.Join(", ", result.Added.Select(l => l.Description));

        activity.Add($"{names} added to job {job.Id} by {mechanicName}", "job");

        if (job.Vehicle is not null)
        {
            await notifications.NotifyCustomerAsync(
                job.Vehicle.CustomerId,
                "Extra work added",
                $"{names} added to job {job.Id} for {job.Vehicle.Plate} — Rs {result.Total:N0}.",
                "job",
                job.Id,
                ct);
        }

        await db.SaveChangesAsync(ct);

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var dto = await db.JobCards.AsNoTracking()
            .Where(j => j.Id == id)
            .ToMechanicDto(today)
            .FirstAsync(ct);

        var skipped = result.AlreadyOn.Count == 0
            ? ""
            : $" {string.Join(", ", result.AlreadyOn)} was already on it.";

        return Ok(ApiResponse<MechanicJobDto>.Ok(dto, $"{names} added.{skipped}"));
    }
}
