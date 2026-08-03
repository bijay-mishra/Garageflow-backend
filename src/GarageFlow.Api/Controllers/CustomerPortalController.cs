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
/// Everything the customer app reads: their vehicles, the work in progress on
/// them, the history behind them and the bills against them.
/// </summary>
/// <remarks>
/// Every query starts from the customer id on the signed-in account, resolved
/// from the database. No endpoint here takes a customer id, so there is nothing
/// to tamper with — the worst a modified request can do is ask about a vehicle
/// that then fails the ownership check.
/// </remarks>
[Authorize(Roles = Vocabulary.CustomerRole)]
[ApiController]
[Route("api/customer")]
[Produces("application/json")]
public class CustomerPortalController(
    GarageFlowDbContext db,
    CurrentUserService currentUser) : ControllerBase
{
    /// <summary>The signed-in customer's vehicles.</summary>
    [HttpGet("vehicles")]
    [ProducesResponseType<ApiResponse<PagedList<VehicleDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedList<VehicleDto>>>> Vehicles(
        [FromQuery] TableQuery query, CancellationToken ct)
    {
        var customerId = await currentUser.CustomerIdAsync(User, ct);

        if (customerId is null)
        {
            return Ok(ApiResponse<PagedList<VehicleDto>>.Ok(
                new PagedList<VehicleDto>([], 0), NoGarage));
        }

        var page = await db.Vehicles.AsNoTracking()
            .Where(v => v.CustomerId == customerId)
            .ToDto()
            .OrderBy(v => v.Plate)
            .ToPagedListAsync(query, ct);

        return Ok(ApiResponse<PagedList<VehicleDto>>.Ok(
            page,
            page.Count == 0 ? "No vehicles on your account yet." : $"{page.Count} vehicle(s)."));
    }

    /// <summary>
    /// Jobs on the customer's vehicles — the "track status" screen.
    /// </summary>
    /// <remarks>
    /// <c>active</c> (the default) returns only work still in the shop. Pass
    /// <c>active=false</c> for everything, which is what the history screen uses.
    /// </remarks>
    [HttpGet("jobs")]
    [ProducesResponseType<ApiResponse<PagedList<CustomerJobDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedList<CustomerJobDto>>>> Jobs(
        [FromQuery] TableQuery query,
        [FromQuery] bool active = true,
        [FromQuery] string? vehicleId = null,
        CancellationToken ct = default)
    {
        var customerId = await currentUser.CustomerIdAsync(User, ct);

        if (customerId is null)
        {
            return Ok(ApiResponse<PagedList<CustomerJobDto>>.Ok(
                new PagedList<CustomerJobDto>([], 0), NoGarage));
        }

        var jobs = db.JobCards.AsNoTracking()
            .Where(j => j.Vehicle!.CustomerId == customerId);

        if (active)
            jobs = jobs.Where(j => Vocabulary.OpenJobStatuses.Contains(j.Status));

        if (!string.IsNullOrWhiteSpace(vehicleId))
            jobs = jobs.Where(j => j.VehicleId == vehicleId);

        var projected = jobs.ToCustomerDto(BaseUrl).OrderByProperty(query.SortBy, query.Descending);

        if (string.IsNullOrWhiteSpace(query.SortBy))
            projected = projected.OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id);

        var page = await projected.ToPagedListAsync(query, ct);

        return Ok(ApiResponse<PagedList<CustomerJobDto>>.Ok(
            page,
            page.Count == 0
                ? active ? "Nothing is in the workshop right now." : "No jobs yet."
                : $"{page.Count} job(s)."));
    }

    /// <summary>One job on one of the customer's vehicles.</summary>
    [HttpGet("jobs/{id}")]
    [ProducesResponseType<ApiResponse<CustomerJobDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<CustomerJobDto>>> Job(string id, CancellationToken ct)
    {
        var customerId = await currentUser.CustomerIdAsync(User, ct);

        if (customerId is null)
            return NotFound(ApiResponse.Failure($"Job '{id}' was not found on your account."));

        var job = await db.JobCards.AsNoTracking()
            .Where(j => j.Id == id && j.Vehicle!.CustomerId == customerId)
            .ToCustomerDto(BaseUrl)
            .FirstOrDefaultAsync(ct);

        if (job is null)
            return NotFound(ApiResponse.Failure($"Job '{id}' was not found on your account."));

        return Ok(ApiResponse<CustomerJobDto>.Ok(job, "Job loaded."));
    }

    /// <summary>
    /// Completed work across the customer's vehicles, newest first — the
    /// service-history screen.
    /// </summary>
    [HttpGet("service-history")]
    [ProducesResponseType<ApiResponse<PagedList<CustomerJobDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedList<CustomerJobDto>>>> ServiceHistory(
        [FromQuery] TableQuery query,
        [FromQuery] string? vehicleId,
        CancellationToken ct)
    {
        var customerId = await currentUser.CustomerIdAsync(User, ct);

        if (customerId is null)
        {
            return Ok(ApiResponse<PagedList<CustomerJobDto>>.Ok(
                new PagedList<CustomerJobDto>([], 0), NoGarage));
        }

        var jobs = db.JobCards.AsNoTracking()
            .Where(j => j.Vehicle!.CustomerId == customerId
                        && Vocabulary.DoneJobStatuses.Contains(j.Status)
                        && j.CompletedAt != null);

        if (!string.IsNullOrWhiteSpace(vehicleId))
            jobs = jobs.Where(j => j.VehicleId == vehicleId);

        var page = await jobs
            .ToCustomerDto(BaseUrl)
            .OrderByDescending(j => j.CompletedAt)
            .ThenByDescending(j => j.Id)
            .ToPagedListAsync(query, ct);

        return Ok(ApiResponse<PagedList<CustomerJobDto>>.Ok(
            page,
            page.Count == 0 ? "No completed services yet." : $"{page.Count} completed service(s)."));
    }

    /// <summary>The customer's invoices.</summary>
    [HttpGet("invoices")]
    [ProducesResponseType<ApiResponse<PagedList<InvoiceDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedList<InvoiceDto>>>> Invoices(
        [FromQuery] TableQuery query, CancellationToken ct)
    {
        var customerId = await currentUser.CustomerIdAsync(User, ct);

        if (customerId is null)
        {
            return Ok(ApiResponse<PagedList<InvoiceDto>>.Ok(
                new PagedList<InvoiceDto>([], 0), NoGarage));
        }

        var page = await db.Invoices.AsNoTracking()
            .Where(i => i.CustomerId == customerId)
            .ToDto()
            .OrderByDescending(i => i.IssuedAt)
            .ThenByDescending(i => i.Id)
            .ToPagedListAsync(query, ct);

        return Ok(ApiResponse<PagedList<InvoiceDto>>.Ok(
            page,
            page.Count == 0 ? "No invoices yet." : $"{page.Count} invoice(s)."));
    }

    /// <summary>Origin photo URLs are built from — see JobPhotosController.</summary>
    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    /// <summary>
    /// What a signed-up customer sees before they have joined a garage.
    /// </summary>
    /// <remarks>
    /// These endpoints used to answer that case with <c>Forbid()</c> — a bare 403
    /// the app rendered as "You do not have permission to do that", which is both
    /// alarming and wrong: they have every permission, there is simply no garage
    /// to look at yet. An empty list with a sentence that names the next action is
    /// the truthful answer, and it is one the app can show as-is.
    /// </remarks>
    private const string NoGarage = "Join a garage to see your vehicles and bills.";
}
