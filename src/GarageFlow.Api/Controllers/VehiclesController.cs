using GarageFlow.Api.Contracts;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using GarageFlow.Api.Mapping;
using GarageFlow.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Controllers;

/// <summary>Vehicles on the workshop's books.</summary>
[Authorize]
[ApiController]
[Route("api/vehicles")]
[Produces("application/json")]
public class VehiclesController(GarageFlowDbContext db, ActivityLog activity) : ControllerBase
{
    /// <summary>Lists vehicles.</summary>
    /// <remarks>
    /// Paged with <c>skip</c>/<c>take</c>; omit <c>take</c> for every row.
    /// Search matches plate, make, model or owner name. <c>fuel</c> filters to
    /// one of Petrol, Diesel, Electric, Hybrid or CNG.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<ApiResponse<PagedList<VehicleDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedList<VehicleDto>>>> List(
        [FromQuery] TableQuery query, [FromQuery] string? fuel, CancellationToken ct)
    {
        var vehicles = db.Vehicles.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(fuel))
            vehicles = vehicles.Where(v => v.Fuel == fuel);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            vehicles = vehicles.Where(v =>
                EF.Functions.Like(v.Plate, $"%{term}%") ||
                EF.Functions.Like(v.Make, $"%{term}%") ||
                EF.Functions.Like(v.Model, $"%{term}%") ||
                EF.Functions.Like(v.Customer!.Name, $"%{term}%"));
        }

        var projected = vehicles.ToDto().OrderByProperty(query.SortBy, query.Descending);

        if (string.IsNullOrWhiteSpace(query.SortBy))
            projected = projected.OrderByDescending(v => v.Id);

        var page = await projected.ToPagedListAsync(query, ct);

        return Ok(ApiResponse<PagedList<VehicleDto>>.Ok(
            page,
            page.Count == 0 ? "No vehicles found." : $"{page.Count} vehicle(s) found."));
    }

    /// <summary>Gets one vehicle.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType<ApiResponse<VehicleDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<VehicleDto>>> Get(string id, CancellationToken ct)
    {
        var vehicle = await db.Vehicles.AsNoTracking().Where(v => v.Id == id).ToDto().FirstOrDefaultAsync(ct);

        if (vehicle is null)
            return NotFound(ApiResponse.Failure($"Vehicle '{id}' was not found."));

        return Ok(ApiResponse<VehicleDto>.Ok(vehicle, "Vehicle loaded."));
    }

    /// <summary>Registers a vehicle against an existing customer.</summary>
    [HttpPost]
    [ProducesResponseType<ApiResponse<VehicleDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<VehicleDto>>> Create(
        CreateVehicleRequest request, CancellationToken ct)
    {
        if (!await db.Customers.AnyAsync(c => c.Id == request.CustomerId, ct))
            return BadRequest(ApiResponse.Failure($"Customer '{request.CustomerId}' does not exist."));

        var vehicle = new Vehicle
        {
            Id = Ids.Next(await db.Vehicles.Select(v => v.Id).ToListAsync(ct), "VEH"),
            CustomerId = request.CustomerId,
            Make = request.Make.Trim(),
            Model = request.Model.Trim(),
            Year = request.Year,
            Plate = request.Plate.Trim(),
            Vin = request.Vin.Trim(),
            Fuel = request.Fuel,
            Odometer = request.Odometer,
            Color = request.Color.Trim(),
        };

        db.Vehicles.Add(vehicle);
        activity.Add($"Vehicle {vehicle.Plate} checked in", "vehicle");
        await db.SaveChangesAsync(ct);

        var dto = await db.Vehicles.AsNoTracking().Where(v => v.Id == vehicle.Id).ToDto().FirstAsync(ct);

        return CreatedAtAction(
            nameof(Get),
            new { id = vehicle.Id },
            ApiResponse<VehicleDto>.Ok(dto, $"Vehicle {vehicle.Plate} added successfully."));
    }

    /// <summary>Updates a vehicle. Only the fields present in the body are applied.</summary>
    [HttpPut("{id}")]
    [ProducesResponseType<ApiResponse<VehicleDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<VehicleDto>>> Update(
        string id, UpdateVehicleRequest request, CancellationToken ct)
    {
        var vehicle = await db.Vehicles.FirstOrDefaultAsync(v => v.Id == id, ct);

        if (vehicle is null)
            return NotFound(ApiResponse.Failure($"Vehicle '{id}' was not found."));

        if (request.CustomerId is not null)
        {
            if (!await db.Customers.AnyAsync(c => c.Id == request.CustomerId, ct))
                return BadRequest(ApiResponse.Failure($"Customer '{request.CustomerId}' does not exist."));
            vehicle.CustomerId = request.CustomerId;
        }

        if (request.Make is not null) vehicle.Make = request.Make.Trim();
        if (request.Model is not null) vehicle.Model = request.Model.Trim();
        if (request.Year is { } year) vehicle.Year = year;
        if (request.Plate is not null) vehicle.Plate = request.Plate.Trim();
        if (request.Vin is not null) vehicle.Vin = request.Vin.Trim();
        if (request.Fuel is not null) vehicle.Fuel = request.Fuel;
        if (request.Odometer is { } odometer) vehicle.Odometer = odometer;
        if (request.Color is not null) vehicle.Color = request.Color.Trim();

        await db.SaveChangesAsync(ct);

        var dto = await db.Vehicles.AsNoTracking().Where(v => v.Id == id).ToDto().FirstAsync(ct);

        return Ok(ApiResponse<VehicleDto>.Ok(dto, $"Vehicle {vehicle.Plate} updated successfully."));
    }

    /// <summary>Deletes a vehicle and its job cards.</summary>
    /// <remarks>
    /// Invoices already raised are left alone — they reference the job card by
    /// id and keep their own plate snapshot, so the billing history survives.
    /// </remarks>
    [HttpDelete("{id}")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse>> Delete(string id, CancellationToken ct)
    {
        var vehicle = await db.Vehicles.FirstOrDefaultAsync(v => v.Id == id, ct);

        if (vehicle is null)
            return NotFound(ApiResponse.Failure($"Vehicle '{id}' was not found."));

        // The Vehicle → JobCard relationship is Restrict (see GarageFlowDbContext),
        // so the jobs have to go before the vehicle can.
        await db.JobCards.Where(j => j.VehicleId == id).ExecuteDeleteAsync(ct);

        db.Vehicles.Remove(vehicle);
        activity.Add($"Vehicle {vehicle.Plate} removed", "vehicle");
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse.Success($"Vehicle {vehicle.Plate} deleted successfully."));
    }
}
