using GarageFlow.Api.Contracts;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using GarageFlow.Api.Mapping;
using GarageFlow.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Controllers;

/// <summary>Workshop customers and the vehicles they own.</summary>
[Authorize]
[ApiController]
[Route("api/customers")]
[Produces("application/json")]
public class CustomersController(GarageFlowDbContext db, ActivityLog activity, TimeProvider clock) : ControllerBase
{
    /// <summary>Lists customers, newest first.</summary>
    /// <remarks>
    /// Paged with <c>skip</c>/<c>take</c>; omit <c>take</c> for every row.
    /// <c>count</c> is always the full total. Search matches name, phone or email.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<ApiResponse<PagedList<CustomerDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedList<CustomerDto>>>> List(
        [FromQuery] TableQuery query, CancellationToken ct)
    {
        var customers = db.Customers.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            customers = customers.Where(c =>
                EF.Functions.Like(c.Name, $"%{term}%") ||
                EF.Functions.Like(c.Phone, $"%{term}%") ||
                EF.Functions.Like(c.Email, $"%{term}%"));
        }

        var projected = customers.ToDto().OrderByProperty(query.SortBy, query.Descending);

        // No explicit sort: newest first.
        if (string.IsNullOrWhiteSpace(query.SortBy))
            projected = projected.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id);

        var page = await projected.ToPagedListAsync(query, ct);

        return Ok(ApiResponse<PagedList<CustomerDto>>.Ok(
            page,
            page.Count == 0 ? "No customers found." : $"{page.Count} customer(s) found."));
    }

    /// <summary>Gets one customer.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType<ApiResponse<CustomerDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<CustomerDto>>> Get(string id, CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking().Where(c => c.Id == id).ToDto().FirstOrDefaultAsync(ct);

        if (customer is null)
            return NotFound(ApiResponse.Failure($"Customer '{id}' was not found."));

        return Ok(ApiResponse<CustomerDto>.Ok(customer, "Customer loaded."));
    }

    /// <summary>Lists the vehicles belonging to a customer.</summary>
    [HttpGet("{id}/vehicles")]
    [ProducesResponseType<ApiResponse<PagedList<VehicleDto>>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<PagedList<VehicleDto>>>> Vehicles(
        string id, [FromQuery] TableQuery query, CancellationToken ct)
    {
        if (!await db.Customers.AnyAsync(c => c.Id == id, ct))
            return NotFound(ApiResponse.Failure($"Customer '{id}' was not found."));

        var page = await db.Vehicles.AsNoTracking()
            .Where(v => v.CustomerId == id)
            .OrderBy(v => v.Id)
            .ToDto()
            .ToPagedListAsync(query, ct);

        return Ok(ApiResponse<PagedList<VehicleDto>>.Ok(page, $"{page.Count} vehicle(s) found."));
    }

    /// <summary>Creates a customer. The id, avatar colour and created date are assigned here.</summary>
    [HttpPost]
    [ProducesResponseType<ApiResponse<CustomerDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<CustomerDto>>> Create(
        CreateCustomerRequest request, CancellationToken ct)
    {
        var existingIds = await db.Customers
            // Unfiltered: ids are unique across every company, so a second
            // company must not start again at CUS-001.
            .IgnoreQueryFilters()
            .Select(c => c.Id)
            .ToListAsync(ct);
        var count = existingIds.Count;

        var customer = new Customer
        {
            Id = Ids.Next(existingIds, "CUS"),
            Name = request.Name.Trim(),
            Phone = request.Phone.Trim(),
            Email = request.Email.Trim(),
            Address = request.Address.Trim(),
            // Both or neither: half a coordinate pair is not a location, and
            // storing one would put a pin on the equator or the prime meridian.
            Latitude = request.Longitude is null ? null : request.Latitude,
            Longitude = request.Latitude is null ? null : request.Longitude,
            CreatedAt = DateOnly.FromDateTime(clock.GetLocalNow().DateTime),
            // Cycle the palette so consecutive customers get distinct avatars.
            AvatarColor = Vocabulary.AvatarColors[count % Vocabulary.AvatarColors.Length],
        };

        db.Customers.Add(customer);
        activity.Add($"New customer {customer.Name} added", "customer");
        await db.SaveChangesAsync(ct);

        var dto = await db.Customers.AsNoTracking().Where(c => c.Id == customer.Id).ToDto().FirstAsync(ct);

        return CreatedAtAction(
            nameof(Get),
            new { id = customer.Id },
            ApiResponse<CustomerDto>.Ok(dto, $"Customer \"{customer.Name}\" added successfully."));
    }

    /// <summary>Updates a customer. Only the fields present in the body are applied.</summary>
    [HttpPut("{id}")]
    [ProducesResponseType<ApiResponse<CustomerDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<CustomerDto>>> Update(
        string id, UpdateCustomerRequest request, CancellationToken ct)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);

        if (customer is null)
            return NotFound(ApiResponse.Failure($"Customer '{id}' was not found."));

        if (request.Name is not null) customer.Name = request.Name.Trim();
        if (request.Phone is not null) customer.Phone = request.Phone.Trim();
        if (request.Email is not null) customer.Email = request.Email.Trim();
        if (request.Address is not null) customer.Address = request.Address.Trim();
        if (request.AvatarColor is not null) customer.AvatarColor = request.AvatarColor;

        // Clearing wins over setting, so a request that somehow asks for both
        // ends with no pin rather than a silently kept one.
        if (request.ClearLocation == true)
        {
            customer.Latitude = null;
            customer.Longitude = null;
        }
        else if (request.Latitude is { } lat && request.Longitude is { } lng)
        {
            // Only moved as a pair. A body carrying one half is ignored rather
            // than applied, which would drag the existing pin onto a meridian.
            customer.Latitude = lat;
            customer.Longitude = lng;
        }

        await db.SaveChangesAsync(ct);

        var dto = await db.Customers.AsNoTracking().Where(c => c.Id == id).ToDto().FirstAsync(ct);

        return Ok(ApiResponse<CustomerDto>.Ok(dto, $"Customer \"{customer.Name}\" updated successfully."));
    }

    /// <summary>
    /// Deletes a customer along with their vehicles, job cards and invoices.
    /// </summary>
    /// <remarks>
    /// This removes financial records. The cascade is spelled out here rather
    /// than left to the database so the intent is visible: if you need invoices
    /// to survive, block the delete instead when <c>Invoices.Any()</c>.
    /// </remarks>
    [HttpDelete("{id}")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse>> Delete(string id, CancellationToken ct)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);

        if (customer is null)
            return NotFound(ApiResponse.Failure($"Customer '{id}' was not found."));

        var vehicleIds = await db.Vehicles.Where(v => v.CustomerId == id).Select(v => v.Id).ToListAsync(ct);

        // Job cards restrict deletion of their vehicle, so they go first.
        await db.JobCards.Where(j => vehicleIds.Contains(j.VehicleId)).ExecuteDeleteAsync(ct);
        await db.Invoices.Where(i => i.CustomerId == id).ExecuteDeleteAsync(ct);

        db.Customers.Remove(customer); // vehicles cascade with the customer
        activity.Add($"Customer {customer.Name} removed", "customer");
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse.Success($"Customer \"{customer.Name}\" deleted successfully."));
    }
}
