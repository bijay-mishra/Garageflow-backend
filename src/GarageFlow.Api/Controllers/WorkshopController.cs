using GarageFlow.Api.Contracts;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using GarageFlow.Api.Services;
using GarageFlow.Api.Services.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Controllers;

/// <summary>
/// The workshop's own details — who it is, where it is, and how to reach it.
/// </summary>
/// <remarks>
/// Readable by everyone signed in, because all three clients want it: the
/// dashboard prints it on bills, the customer app shows the address and a map
/// with directions, the mechanic app shows the phone number. Writable only by an
/// owner or manager — this is the name on the tax invoice.
/// </remarks>
[Authorize]
[ApiController]
[Route("api/workshop")]
[Produces("application/json")]
public class WorkshopController(
    GarageFlowDbContext db,
    CurrentUserService currentUser,
    PaymentService payments,
    ActivityLog activity,
    TimeProvider clock) : ControllerBase
{
    /// <summary>The signed-in user's workshop.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<WorkshopDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<WorkshopDto>>> Get(CancellationToken ct)
    {
        var workshop = await LoadAsync(ct);

        return Ok(ApiResponse<WorkshopDto>.Ok(ToDto(workshop), "Workshop loaded."));
    }

    /// <summary>Updates the workshop. Owner or manager only.</summary>
    [HttpPut]
    [Authorize(Roles = "Owner,Manager")]
    [ProducesResponseType<ApiResponse<WorkshopDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<WorkshopDto>>> Update(
        UpdateWorkshopRequest request, CancellationToken ct)
    {
        var workshop = await LoadAsync(ct);

        if (request.Name is not null) workshop.Name = request.Name.Trim();
        if (request.LegalName is not null) workshop.LegalName = request.LegalName.Trim();
        if (request.Address is not null) workshop.Address = request.Address.Trim();
        if (request.Phone is not null) workshop.Phone = request.Phone.Trim();
        if (request.Email is not null) workshop.Email = request.Email.Trim();
        if (request.TaxNumber is not null) workshop.TaxNumber = request.TaxNumber.Trim();
        if (request.OpeningHours is not null) workshop.OpeningHours = request.OpeningHours.Trim();
        if (request.InvoiceFooter is not null) workshop.InvoiceFooter = request.InvoiceFooter.Trim();

        // Same rule as a customer's pin: cleared explicitly, and only ever moved
        // as a complete pair. See UpdateCustomerRequest.ClearLocation.
        if (request.ClearLocation == true)
        {
            workshop.Latitude = null;
            workshop.Longitude = null;
        }
        else if (request.Latitude is { } lat && request.Longitude is { } lng)
        {
            workshop.Latitude = lat;
            workshop.Longitude = lng;
        }

        if (request.DeliveryEnabled is { } enabled) workshop.DeliveryEnabled = enabled;
        if (request.DeliveryBaseFee is { } baseFee) workshop.DeliveryBaseFee = baseFee;
        if (request.DeliveryPerKm is { } perKm) workshop.DeliveryPerKm = perKm;
        if (request.DeliveryFreeAbove is { } freeAbove) workshop.DeliveryFreeAbove = freeAbove;
        if (request.DeliveryMaxKm is { } maxKm) workshop.DeliveryMaxKm = maxKm;

        workshop.UpdatedAt = clock.GetUtcNow().UtcDateTime;

        activity.Add("Workshop details updated", "customer");
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<WorkshopDto>.Ok(ToDto(workshop), "Workshop details saved."));
    }

    /// <summary>
    /// The caller's workshop row, created on first read.
    /// </summary>
    /// <remarks>
    /// Created rather than 404'd because every tenant has a workshop by
    /// definition — the row is just a place to put its details, and demanding a
    /// setup step before the dashboard will load would be ceremony. A fresh
    /// tenant gets blanks and fills them in on the settings screen.
    /// </remarks>
    private async Task<Workshop> LoadAsync(CancellationToken ct)
    {
        var user = await currentUser.GetAsync(User, ct);
        var code = user?.CompanyCode ?? DbSeeder.DemoCompanyCode;

        var workshop = await db.Workshops.FirstOrDefaultAsync(w => w.CompanyCode == code, ct);

        if (workshop is not null) return workshop;

        workshop = new Workshop
        {
            CompanyCode = code,
            UpdatedAt = clock.GetUtcNow().UtcDateTime,
        };

        db.Workshops.Add(workshop);
        await db.SaveChangesAsync(ct);

        return workshop;
    }

    private WorkshopDto ToDto(Workshop w) => new()
    {
        Name = w.Name,
        LegalName = w.LegalName,
        Address = w.Address,
        Phone = w.Phone,
        Email = w.Email,
        TaxNumber = w.TaxNumber,
        Latitude = w.Latitude,
        Longitude = w.Longitude,
        OpeningHours = w.OpeningHours,
        InvoiceFooter = w.InvoiceFooter,
        CanDeliver = w.CanDeliver,
        DeliveryEnabled = w.DeliveryEnabled,
        DeliveryBaseFee = w.DeliveryBaseFee,
        DeliveryPerKm = w.DeliveryPerKm,
        DeliveryFreeAbove = w.DeliveryFreeAbove,
        DeliveryMaxKm = w.DeliveryMaxKm,
        // Computed, not stored: which gateways work is a question about
        // configuration on this server right now, not about the workshop.
        OnlineProviders = payments.AvailableProviders,
    };
}
