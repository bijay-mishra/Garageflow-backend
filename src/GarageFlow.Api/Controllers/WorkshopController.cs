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
    // The tenant, not the signed-in user's stored row: during impersonation the
    // two disagree, and the row is the operator's.
    TenantContext tenant,
    PaymentService payments,
    PhotoStorage photos,
    ActivityLog activity,
    TimeProvider clock) : ControllerBase
{
    /// <summary>The signed-in user's workshop.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<WorkshopDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<WorkshopDto>>> Get(CancellationToken ct)
    {
        var workshop = await LoadAsync(ct);

        if (workshop is null) return NoWorkshop();

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

        if (workshop is null) return NoWorkshop();

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

        if (request.BankName is not null) workshop.BankName = request.BankName.Trim();
        if (request.BankAccountName is not null) workshop.BankAccountName = request.BankAccountName.Trim();
        if (request.BankAccountNumber is not null) workshop.BankAccountNumber = request.BankAccountNumber.Trim();
        if (request.BankBranch is not null) workshop.BankBranch = request.BankBranch.Trim();

        if (request.About is not null) workshop.About = request.About.Trim();
        if (request.IsListed is { } listed) workshop.IsListed = listed;

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
    /// Sets the workshop's logo. Owner or manager only.
    /// </summary>
    /// <remarks>
    /// Its own endpoint rather than a field on the PUT above, because a file is
    /// not a string: it needs a multipart body, a size limit and a type check,
    /// and folding that into the settings form would mean re-uploading the image
    /// every time somebody corrected a phone number.
    /// </remarks>
    [HttpPost("logo")]
    [Authorize(Roles = "Owner,Manager")]
    [ProducesResponseType<ApiResponse<WorkshopDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(PhotoStorage.MaxLogoRequestBytes)]
    public async Task<ActionResult<ApiResponse<WorkshopDto>>> UploadLogo(
        IFormFile file, CancellationToken ct)
    {
        var workshop = await LoadAsync(ct);

        if (workshop is null) return NoWorkshop();

        var problem = await CheckLogoAsync(file, ct);
        if (problem is not null) return BadRequest(ApiResponse.Failure(problem));

        var previous = workshop.LogoPath;

        workshop.LogoPath = await photos.SaveLogoAsync(file, workshop.CompanyCode, ct);
        workshop.UpdatedAt = clock.GetUtcNow().UtcDateTime;

        activity.Add("Workshop logo updated", "customer");
        await db.SaveChangesAsync(ct);

        // Only once the row is committed, so a failed save leaves the old logo
        // in place rather than none. Same order as the profile photo.
        if (previous is not null) photos.Delete(previous);

        return Ok(ApiResponse<WorkshopDto>.Ok(ToDto(workshop), "Logo updated."));
    }

    /// <summary>Removes the workshop's logo. Owner or manager only.</summary>
    [HttpDelete("logo")]
    [Authorize(Roles = "Owner,Manager")]
    [ProducesResponseType<ApiResponse<WorkshopDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<WorkshopDto>>> DeleteLogo(CancellationToken ct)
    {
        var workshop = await LoadAsync(ct);

        if (workshop is null) return NoWorkshop();

        var previous = workshop.LogoPath;

        workshop.LogoPath = null;
        workshop.UpdatedAt = clock.GetUtcNow().UtcDateTime;

        activity.Add("Workshop logo removed", "customer");
        await db.SaveChangesAsync(ct);

        if (previous is not null) photos.Delete(previous);

        return Ok(ApiResponse<WorkshopDto>.Ok(ToDto(workshop), "Logo removed."));
    }

    /// <summary>
    /// Why an uploaded logo is unacceptable, or null when it is fine.
    /// </summary>
    /// <remarks>
    /// Shared by this controller and the operator console, which both accept a
    /// logo for the same reason and must accept exactly the same set of files —
    /// two copies of an allow-list is how one of them ends up laxer.
    /// </remarks>
    internal static async Task<string?> CheckLogoAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return "Choose a logo to upload.";

        if (file.Length > PhotoStorage.MaxLogoBytes)
            return "That image is too large. Please choose one under 1 MB.";

        if (!PhotoStorage.IsAllowedLogo(file.ContentType))
            return $"That file type is not supported. Use one of: {PhotoStorage.AllowedLogoList}.";

        // SVG is markup and gets served from this API's own origin, so a hostile
        // one would run there. Read and screened before a byte reaches disk.
        if (file.ContentType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(file.OpenReadStream());
            var markup = await reader.ReadToEndAsync(ct);

            if (PhotoStorage.LooksLikeScriptedSvg(markup))
            {
                return "That SVG contains script and was not saved. "
                       + "Export it as a plain image, or upload a PNG.";
            }
        }

        return null;
    }

    /// <summary>
    /// The caller's workshop row, created on first read, or null if the caller
    /// belongs to no company.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Created rather than 404'd because every tenant has a workshop by
    /// definition — the row is just a place to put its details, and demanding a
    /// setup step before the dashboard will load would be ceremony. A fresh
    /// tenant gets blanks and fills them in on the settings screen.
    /// </para>
    /// <para>
    /// Every tenant, though — and not everyone signed in is one. A superadmin
    /// carries an empty company code, and a customer who has joined no garage
    /// carries one too. This used to mint them a <c>Workshop</c> with a blank
    /// code, which then appeared in the operator console as a nameless company
    /// that could not be opened (there is no <c>/companies/</c> route) and so
    /// could not be deleted either. A company you cannot name is not a company;
    /// the honest answer here is that there is nothing to load.
    /// </para>
    /// </remarks>
    private async Task<Workshop?> LoadAsync(CancellationToken ct)
    {
        // From the token rather than the caller's stored User row. The two
        // disagree during an impersonated session: the id in the token is the
        // operator's, whose own row carries no company, so a database lookup
        // answered for nobody — and used to *create* a blank-coded workshop to
        // answer with, which is where the nameless company in the console came
        // from. The token's company is what every tenant-filtered query on this
        // request is already scoped to.
        var code = tenant.CompanyCode ?? DbSeeder.DemoCompanyCode;

        if (string.IsNullOrWhiteSpace(code)) return null;

        var workshop = await db.Workshops.FirstOrDefaultAsync(w => w.CompanyCode == code, ct);

        if (workshop is not null) return workshop;

        workshop = new Workshop
        {
            CompanyCode = code,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
            UpdatedAt = clock.GetUtcNow().UtcDateTime,
        };

        db.Workshops.Add(workshop);
        await db.SaveChangesAsync(ct);

        return workshop;
    }

    /// <summary>The answer when the caller belongs to no company.</summary>
    private ActionResult NoWorkshop() =>
        NotFound(ApiResponse.Failure("Your account is not attached to a workshop."));

    private WorkshopDto ToDto(Workshop w) => new()
    {
        Name = w.Name,
        LegalName = w.LegalName,
        Address = w.Address,
        Phone = w.Phone,
        Email = w.Email,
        TaxNumber = w.TaxNumber,
        LogoUrl = PhotoStorage.PublicUrl(Request, w.LogoPath),
        Latitude = w.Latitude,
        Longitude = w.Longitude,
        OpeningHours = w.OpeningHours,
        InvoiceFooter = w.InvoiceFooter,
        About = w.About,
        IsListed = w.IsListed,
        BankName = w.BankName,
        BankAccountName = w.BankAccountName,
        BankAccountNumber = w.BankAccountNumber,
        BankBranch = w.BankBranch,
        CanBankTransfer = w.CanBankTransfer,
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
