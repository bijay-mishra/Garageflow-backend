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
/// The workshop's price list — washing, detailing, alignment, pickup and drop.
/// </summary>
/// <remarks>
/// Reading is open to everyone signed in, because all three clients need it: the
/// dashboard to price a job card, the customer app to offer extras when booking,
/// the mechanic app to add one mid-job. Writing is staff only — a mechanic may
/// put a wash on a car, but not decide what a wash costs.
///
/// Nothing here is ever deleted once it has been sold. See <see cref="Delete"/>.
/// </remarks>
[Authorize]
[ApiController]
[Route("api/services")]
[Produces("application/json")]
public class ServicesController(
    GarageFlowDbContext db,
    CurrentUserService currentUser,
    ActivityLog activity,
    TimeProvider clock) : ControllerBase
{
    /// <summary>Lists services.</summary>
    /// <remarks>
    /// <c>category</c> narrows to one group, <c>vehicleType</c> to what is
    /// offered for that body class (entries with no restriction always match),
    /// and <c>activeOnly</c> hides retired rows. Search covers name and
    /// description.
    ///
    /// A customer always gets the active, bookable list whatever they ask for —
    /// the parameters can only narrow it further, never widen it.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<ApiResponse<PagedList<ServiceDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedList<ServiceDto>>>> List(
        [FromQuery] TableQuery query,
        [FromQuery] string? category,
        [FromQuery] string? vehicleType,
        [FromQuery] bool? activeOnly,
        [FromQuery] bool? bookableOnly,
        CancellationToken ct = default)
    {
        var services = db.Services.AsNoTracking().AsQueryable();

        // Applied first and unconditionally: a customer must never see a retired
        // price or an internal-only extra, whatever they put in the query string.
        //
        // Keyed on the role rather than on having a customer record, so somebody
        // who has signed up but joined no garage is still treated as a customer
        // here — the price list is the one thing they can reasonably look at
        // before joining, and they should see the public version of it.
        var isCustomer = !(await currentUser.CustomerScopeAsync(User, ct)).IsStaff;

        if (isCustomer)
            services = services.Where(s => s.IsActive && s.IsBookable);
        else
        {
            if (activeOnly == true) services = services.Where(s => s.IsActive);
            if (bookableOnly == true) services = services.Where(s => s.IsBookable);
        }

        if (!string.IsNullOrWhiteSpace(category))
            services = services.Where(s => s.Category == category);

        if (!string.IsNullOrWhiteSpace(vehicleType))
        {
            // An empty column means "every vehicle", so it has to match too.
            // Comma-wrapping both sides keeps "Van" out of a search for "an" and
            // stops "Bus" matching a row that only lists "Bus Shelter" one day.
            var needle = $",{vehicleType.Trim()},";
            services = services.Where(s =>
                s.VehicleTypes == "" || ("," + s.VehicleTypes + ",").Contains(needle));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            services = services.Where(s =>
                EF.Functions.Like(s.Name, $"%{term}%") ||
                EF.Functions.Like(s.Description, $"%{term}%") ||
                EF.Functions.Like(s.Category, $"%{term}%"));
        }

        var projected = services.ToDto().OrderByProperty(query.SortBy, query.Descending);

        if (string.IsNullOrWhiteSpace(query.SortBy))
            projected = projected.OrderBy(s => s.Category).ThenBy(s => s.Name);

        var page = await projected.ToPagedListAsync(query, ct);

        return Ok(ApiResponse<PagedList<ServiceDto>>.Ok(
            page,
            page.Count == 0 ? "No services found." : $"{page.Count} service(s) found."));
    }

    /// <summary>One service.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType<ApiResponse<ServiceDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ServiceDto>>> Get(string id, CancellationToken ct)
    {
        var service = await db.Services.AsNoTracking().Where(s => s.Id == id).ToDto().FirstOrDefaultAsync(ct);

        if (service is null)
            return NotFound(ApiResponse.Failure($"Service '{id}' was not found."));

        return Ok(ApiResponse<ServiceDto>.Ok(service, "Service loaded."));
    }

    /// <summary>Adds a service to the price list. Staff only.</summary>
    [HttpPost]
    [Authorize(Roles = "Owner,Manager,Advisor")]
    [ProducesResponseType<ApiResponse<ServiceDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<ServiceDto>>> Create(
        CreateServiceRequest request, CancellationToken ct)
    {
        if (InvalidVehicleTypes(request.AppliesTo) is { } bad)
            return BadRequest(ApiResponse.Failure(bad));

        var name = request.Name.Trim();

        // Two rows called "Car wash" is how a price list stops being an answer
        // to "what do we charge?" and starts being a question.
        if (await db.Services.AnyAsync(s => s.Name == name, ct))
            return BadRequest(ApiResponse.Failure($"A service called '{name}' already exists."));

        var service = new Service
        {
            Id = Ids.Next(await db.Services.IgnoreQueryFilters().Select(s => s.Id).ToListAsync(ct), "SVC"),
            Name = name,
            Description = request.Description.Trim(),
            Category = request.Category,
            Price = request.Price,
            DurationMinutes = request.DurationMinutes,
            VehicleTypes = Service.Join(request.AppliesTo),
            IsActive = request.IsActive,
            IsBookable = request.IsBookable,
            CreatedAt = DateOnly.FromDateTime(clock.GetLocalNow().DateTime),
        };

        db.Services.Add(service);
        activity.Add($"Service '{service.Name}' added to the price list", "job");
        await db.SaveChangesAsync(ct);

        var dto = await db.Services.AsNoTracking().Where(s => s.Id == service.Id).ToDto().FirstAsync(ct);

        return CreatedAtAction(
            nameof(Get),
            new { id = service.Id },
            ApiResponse<ServiceDto>.Ok(dto, $"'{service.Name}' added to the price list."));
    }

    /// <summary>Updates a service. Staff only. Only the fields sent are applied.</summary>
    /// <remarks>
    /// Changing a price never touches a job card that already carries this
    /// service — those lines were copied when they were added, on purpose. The
    /// new price applies from the next time someone picks it.
    /// </remarks>
    [HttpPut("{id}")]
    [Authorize(Roles = "Owner,Manager,Advisor")]
    [ProducesResponseType<ApiResponse<ServiceDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ServiceDto>>> Update(
        string id, UpdateServiceRequest request, CancellationToken ct)
    {
        var service = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);

        if (service is null)
            return NotFound(ApiResponse.Failure($"Service '{id}' was not found."));

        if (request.AppliesTo is not null && InvalidVehicleTypes(request.AppliesTo) is { } bad)
            return BadRequest(ApiResponse.Failure(bad));

        if (request.Name is not null)
        {
            var name = request.Name.Trim();

            if (await db.Services.AnyAsync(s => s.Name == name && s.Id != id, ct))
                return BadRequest(ApiResponse.Failure($"A service called '{name}' already exists."));

            service.Name = name;
        }

        if (request.Description is not null) service.Description = request.Description.Trim();
        if (request.Category is not null) service.Category = request.Category;
        if (request.Price is { } price) service.Price = price;
        if (request.DurationMinutes is { } duration) service.DurationMinutes = duration;
        if (request.AppliesTo is not null) service.VehicleTypes = Service.Join(request.AppliesTo);
        if (request.IsBookable is { } bookable) service.IsBookable = bookable;

        var retired = request.IsActive is false && service.IsActive;
        if (request.IsActive is { } active) service.IsActive = active;

        if (retired) activity.Add($"Service '{service.Name}' retired from the price list", "job");

        await db.SaveChangesAsync(ct);

        var dto = await db.Services.AsNoTracking().Where(s => s.Id == id).ToDto().FirstAsync(ct);

        return Ok(ApiResponse<ServiceDto>.Ok(
            dto,
            retired ? $"'{service.Name}' retired — it stays on past jobs." : $"'{service.Name}' updated successfully."));
    }

    /// <summary>
    /// Removes a service from the price list. Staff only.
    /// </summary>
    /// <remarks>
    /// Refused once the service has been sold or booked. Deleting it would strip
    /// the link from lines that were genuinely billed, and the shop would lose
    /// the ability to ask what washing earned last year. Retiring it — a PUT with
    /// <c>isActive: false</c> — stops it being offered and keeps the history,
    /// which is what "we don't do that any more" actually means.
    /// </remarks>
    [HttpDelete("{id}")]
    [Authorize(Roles = "Owner,Manager")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse>> Delete(string id, CancellationToken ct)
    {
        var service = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);

        if (service is null)
            return NotFound(ApiResponse.Failure($"Service '{id}' was not found."));

        var usedOnJobs = await db.JobLines.CountAsync(l => l.ServiceId == id, ct);
        var usedOnBookings = await db.BookingServices.CountAsync(bs => bs.ServiceId == id, ct);

        if (usedOnJobs + usedOnBookings > 0)
        {
            return BadRequest(ApiResponse.Failure(
                $"'{service.Name}' is on {usedOnJobs} job card(s) and {usedOnBookings} booking(s). " +
                "Retire it instead — it will stop being offered and stay on the work already done."));
        }

        db.Services.Remove(service);
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse.Success($"'{service.Name}' removed from the price list."));
    }

    /// <summary>Null when every entry is a known vehicle type, else the message to return.</summary>
    private static string? InvalidVehicleTypes(IEnumerable<string> types)
    {
        var unknown = types
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .FirstOrDefault(t => !Vocabulary.VehicleTypes.Contains(t));

        return unknown is null
            ? null
            : $"'{unknown}' is not a vehicle type. Use one of: {string.Join(", ", Vocabulary.VehicleTypes)}.";
    }
}
