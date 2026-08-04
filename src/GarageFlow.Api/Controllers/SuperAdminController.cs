using System.Security.Claims;
using GarageFlow.Api.Contracts;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using GarageFlow.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Controllers;

/// <summary>
/// The operator's console: every company on the platform.
/// </summary>
/// <remarks>
/// <para>
/// The only controller that reads across companies, and it does so explicitly —
/// every query here carries <c>IgnoreQueryFilters()</c> so that crossing the
/// boundary is visible in the source rather than implied by the caller's role.
/// </para>
/// <para>
/// It never touches a company's own records. To work inside a company the
/// superadmin mints a token scoped to it and uses the ordinary dashboard, which
/// means the tenant filter still applies and every screen behaves exactly as the
/// workshop sees it. That is the point: there is no second, privileged code path
/// through the app that could drift from the real one.
/// </para>
/// </remarks>
[Authorize(Roles = Vocabulary.SuperAdminRole)]
[ApiController]
[Route("api/superadmin")]
[Produces("application/json")]
public class SuperAdminController(
    GarageFlowDbContext db,
    TokenService tokens,
    IPasswordHasher<User> passwordHasher,
    PhotoStorage photos,
    TimeProvider clock,
    ILogger<SuperAdminController> logger) : ControllerBase
{
    /// <summary>Every company, with enough to see how each is doing.</summary>
    [HttpGet("companies")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<CompanyDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<CompanyDto>>>> Companies(
        CancellationToken ct)
    {
        // A blank code is not a company. Nothing should create one — see
        // WorkshopController.LoadAsync, which used to mint one for any caller
        // belonging to no company — but the console is where such a row would
        // surface, as a nameless "active" company with no page behind it and no
        // way to delete it. Filtering here means one escaping again is invisible
        // rather than alarming.
        var workshops = await db.Workshops.AsNoTracking()
            .Where(w => w.CompanyCode != "")
            .OrderBy(w => w.Name)
            .ToListAsync(ct);

        // Counted in one pass per table rather than a query per company: this
        // list is the console's home screen and gets opened often.
        var users = await db.Users.AsNoTracking()
            .GroupBy(u => u.CompanyCode)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Code, x => x.Count, ct);

        var customers = await db.Customers.IgnoreQueryFilters().AsNoTracking()
            .GroupBy(c => c.CompanyCode)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Code, x => x.Count, ct);

        var jobs = await db.JobCards.IgnoreQueryFilters().AsNoTracking()
            .GroupBy(j => j.CompanyCode)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Code, x => x.Count, ct);

        var lastSeen = await db.Users.AsNoTracking()
            .Where(u => u.LastLoginAt != null)
            .GroupBy(u => u.CompanyCode)
            .Select(g => new { Code = g.Key, At = g.Max(u => u.LastLoginAt) })
            .ToDictionaryAsync(x => x.Code, x => x.At, ct);

        var list = workshops.Select(w => new CompanyDto
        {
            CompanyCode = w.CompanyCode,
            Name = w.Name,
            LegalName = w.LegalName,
            Phone = w.Phone,
            Email = w.Email,
            Address = w.Address,
            LogoUrl = PhotoStorage.PublicUrl(Request, w.LogoPath),
            IsActive = w.IsActive,
            IsListed = w.IsListed,
            EnabledModules = Split(w.EnabledModules),
            UserCount = users.GetValueOrDefault(w.CompanyCode),
            CustomerCount = customers.GetValueOrDefault(w.CompanyCode),
            JobCount = jobs.GetValueOrDefault(w.CompanyCode),
            CreatedAt = w.CreatedAt,
            LastActiveAt = lastSeen.GetValueOrDefault(w.CompanyCode),
        }).ToList();

        return Ok(ApiResponse<IReadOnlyList<CompanyDto>>.Ok(
            list,
            list.Count == 1 ? "1 company." : $"{list.Count} companies."));
    }

    /// <summary>Everything the console needs to render its toggles.</summary>
    [HttpGet("modules")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<string>>>(StatusCodes.Status200OK)]
    public ActionResult<ApiResponse<IReadOnlyList<string>>> Modules() =>
        Ok(ApiResponse<IReadOnlyList<string>>.Ok(
            Vocabulary.Modules, $"{Vocabulary.Modules.Length} modules."));

    /// <summary>The menu catalogue, retired rows included.</summary>
    /// <remarks>
    /// The console sees inactive rows and the dashboard does not — retiring one
    /// is the operator's decision, so the screen that makes it has to show what
    /// has already been retired.
    /// </remarks>
    [HttpGet("menus")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<MenuItemDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<MenuItemDto>>>> Menus(
        [FromServices] MenuService menus, CancellationToken ct)
    {
        var catalogue = await menus.CatalogueAsync(ct);

        return Ok(ApiResponse<IReadOnlyList<MenuItemDto>>.Ok(
            catalogue.Select(MenusController.ToDto).ToList(),
            $"{catalogue.Count} menu row(s)."));
    }

    /// <summary>
    /// Changes a menu row for every company on the platform.
    /// </summary>
    /// <remarks>
    /// Wording, order, gating module and whether it exists. Not the key and not
    /// the route: both name a screen that has to exist in the client bundle, and
    /// editing either would produce a menu entry leading nowhere.
    /// </remarks>
    [HttpPut("menus/{key}")]
    [ProducesResponseType<ApiResponse<MenuItemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MenuItemDto>>> UpdateMenu(
        string key, UpdateMenuItemRequest request, CancellationToken ct)
    {
        var item = await db.MenuItems.FirstOrDefaultAsync(m => m.Key == key, ct);

        if (item is null)
            return NotFound(ApiResponse.Failure($"No menu row with key '{key}'."));

        if (request.Module is not null)
        {
            // Empty means "always visible". An unknown name would be stored and
            // silently gate nothing, which looks like the setting is broken.
            var module = request.Module.Trim();

            if (module.Length > 0 && !Vocabulary.Modules.Contains(module))
                return BadRequest(ApiResponse.Failure($"'{module}' is not a module."));

            item.Module = module.Length == 0 ? null : module;
        }

        if (request.Label is not null) item.Label = request.Label.Trim();
        if (request.LabelNe is not null) item.LabelNe = request.LabelNe.Trim();
        if (request.Icon is not null) item.Icon = request.Icon.Trim();
        if (request.SortOrder is { } order) item.SortOrder = order;

        if (request.IsActive is { } active)
        {
            if (!active && item.IsLocked)
            {
                return BadRequest(ApiResponse.Failure(
                    $"'{item.Label}' cannot be retired — every user needs it to navigate."));
            }

            item.IsActive = active;
        }

        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<MenuItemDto>.Ok(
            MenusController.ToDto(item), $"{item.Label} updated."));
    }

    /// <summary>
    /// Creates a company and its first owner.
    /// </summary>
    /// <remarks>
    /// Both at once, deliberately. A company with no way to sign in is not a
    /// company yet, and creating the two separately leaves a window where one
    /// exists without the other.
    /// </remarks>
    [HttpPost("companies")]
    [ProducesResponseType<ApiResponse<CompanyCreatedDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<CompanyCreatedDto>>> CreateCompany(
        CreateCompanyRequest request, CancellationToken ct)
    {
        var code = request.CompanyCode.Trim().ToUpperInvariant();

        if (!code.All(c => char.IsLetterOrDigit(c) || c == '-'))
        {
            return BadRequest(ApiResponse.Failure(
                "A company code may only contain letters, numbers and hyphens."));
        }

        if (await db.Workshops.AnyAsync(w => w.CompanyCode == code, ct))
            return BadRequest(ApiResponse.Failure($"Company code '{code}' is already taken."));

        var ownerEmail = request.OwnerEmail.Trim();

        // One account per email per company is the rule, so the same person can
        // own two workshops — but not two accounts in the one being created.
        if (await db.Users.AnyAsync(u => u.CompanyCode == code && u.Email == ownerEmail, ct))
            return BadRequest(ApiResponse.Failure("That email already has an account here."));

        var now = clock.GetUtcNow().UtcDateTime;

        // Generated unless the operator insisted on one. Either way it is a
        // handover credential, not the owner's password — see MustSetPassword.
        var oneTimePassword = string.IsNullOrWhiteSpace(request.OwnerPassword)
            ? TokenService.CreateOneTimePassword()
            : request.OwnerPassword;

        var modules = request.EnabledModules is { Length: > 0 }
            ? request.EnabledModules.Where(Vocabulary.Modules.Contains).ToArray()
            : Vocabulary.DefaultModules;

        var workshop = new Workshop
        {
            CompanyCode = code,
            Name = request.Name.Trim(),
            LegalName = request.LegalName?.Trim() ?? request.Name.Trim(),
            Phone = request.Phone?.Trim() ?? "",
            Email = request.Email?.Trim() ?? ownerEmail,
            Address = request.Address?.Trim() ?? "",
            EnabledModules = string.Join(',', modules),
            IsActive = true,
            IsListed = false,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Workshops.Add(workshop);

        // Unfiltered: user ids are unique across every company.
        var existingIds = await db.Users.Select(u => u.Id).ToListAsync(ct);

        var owner = new User
        {
            Id = Ids.Next(existingIds, "USR"),
            CompanyCode = code,
            Email = ownerEmail,
            FullName = request.OwnerName.Trim(),
            Phone = request.OwnerPhone?.Trim(),
            PasswordHash = passwordHasher.HashPassword(new User(), oneTimePassword),
            Role = "Owner",
            Workshop = workshop.Name,
            IsActive = true,
            CreatedAt = now,

            // The operator typed or read this password, so it is not a secret
            // between the owner and the system yet. It stops working the moment
            // they replace it, which the API makes them do before anything else.
            MustSetPassword = true,
        };

        db.Users.Add(owner);

        // Every company needs somewhere for its work to happen.
        db.Branches.Add(new Branch
        {
            Id = Ids.Next(await db.Branches.Select(b => b.Id).ToListAsync(ct), "BR"),
            CompanyCode = code,
            Name = "Main Branch",
            Address = workshop.Address,
            Phone = workshop.Phone,
            IsDefault = true,
            IsActive = true,
            CreatedAt = now,
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Superadmin created company {Code} with owner {Email}", code, ownerEmail);

        return Ok(ApiResponse<CompanyCreatedDto>.Ok(
            new CompanyCreatedDto
            {
                Company = new CompanyDto
                {
                    CompanyCode = code,
                    Name = workshop.Name,
                    LegalName = workshop.LegalName,
                    Phone = workshop.Phone,
                    Email = workshop.Email,
                    Address = workshop.Address,
                    // A company created a second ago has no logo. The console
                    // offers to add one on the handover screen that follows.
                    LogoUrl = null,
                    IsActive = true,
                    IsListed = false,
                    EnabledModules = modules,
                    UserCount = 1,
                    CustomerCount = 0,
                    JobCount = 0,
                    CreatedAt = now,
                    LastActiveAt = null,
                },
                OneTimePassword = oneTimePassword,
            },
            $"{workshop.Name} created. {ownerEmail} can sign in with company code {code}."));
    }

    /// <summary>Changes which modules a company has, or suspends it.</summary>
    [HttpPut("companies/{code}")]
    [ProducesResponseType<ApiResponse<CompanyDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<CompanyDto>>> UpdateCompany(
        string code, UpdateCompanyRequest request, CancellationToken ct)
    {
        var workshop = await db.Workshops.FirstOrDefaultAsync(
            w => w.CompanyCode == code.ToUpperInvariant(), ct);

        if (workshop is null)
            return NotFound(ApiResponse.Failure($"No company with code '{code}'."));

        var changes = new List<string>();

        if (request.EnabledModules is not null)
        {
            // Filtered against the known list: an unknown name would be stored
            // and silently gate nothing, which looks like the toggle is broken.
            var modules = request.EnabledModules.Where(Vocabulary.Modules.Contains).ToArray();
            workshop.EnabledModules = string.Join(',', modules);
            changes.Add($"{modules.Length} module(s)");
        }

        if (request.IsActive is { } active && active != workshop.IsActive)
        {
            workshop.IsActive = active;
            changes.Add(active ? "reactivated" : "suspended");
        }

        // Null means "leave it alone", so an edit form can send only the fields
        // it owns without having to resend everything else.
        if (request.Name is not null) workshop.Name = request.Name.Trim();
        if (request.LegalName is not null) workshop.LegalName = request.LegalName.Trim();
        if (request.Address is not null) workshop.Address = request.Address.Trim();
        if (request.Phone is not null) workshop.Phone = request.Phone.Trim();
        if (request.Email is not null) workshop.Email = request.Email.Trim();

        if (request.Name is not null || request.LegalName is not null ||
            request.Address is not null || request.Phone is not null || request.Email is not null)
        {
            changes.Add("details");
        }

        workshop.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<CompanyDto>.Ok(
            new CompanyDto
            {
                CompanyCode = workshop.CompanyCode,
                Name = workshop.Name,
                LegalName = workshop.LegalName,
                Phone = workshop.Phone,
                Email = workshop.Email,
                Address = workshop.Address,
                LogoUrl = PhotoStorage.PublicUrl(Request, workshop.LogoPath),
                IsActive = workshop.IsActive,
                IsListed = workshop.IsListed,
                EnabledModules = Split(workshop.EnabledModules),
                UserCount = 0,
                CustomerCount = 0,
                JobCount = 0,
                CreatedAt = workshop.CreatedAt,
                LastActiveAt = null,
            },
            changes.Count == 0
                ? "Nothing changed."
                : $"{workshop.Name}: {string.Join(", ", changes)}."));
    }

    /// <summary>
    /// Sets a company's logo on its behalf.
    /// </summary>
    /// <remarks>
    /// The same thing the workshop can do for itself on its settings screen, and
    /// it exists here because of when it is needed: the operator sets a company
    /// up before anyone at that workshop has ever signed in, and the logo is
    /// usually in the same email as the name and the PAN. Making them wait for
    /// the owner to do it means the first invoice goes out bare.
    ///
    /// The file rules are <see cref="WorkshopController.CheckLogoAsync"/>'s, not
    /// a second copy — an operator upload must not be able to store something a
    /// workshop upload would have refused.
    /// </remarks>
    [HttpPost("companies/{code}/logo")]
    [ProducesResponseType<ApiResponse<CompanyDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    [RequestSizeLimit(PhotoStorage.MaxLogoRequestBytes)]
    public async Task<ActionResult<ApiResponse<CompanyDto>>> UploadCompanyLogo(
        string code, IFormFile file, CancellationToken ct)
    {
        var workshop = await db.Workshops.FirstOrDefaultAsync(
            w => w.CompanyCode == code.ToUpperInvariant(), ct);

        if (workshop is null)
            return NotFound(ApiResponse.Failure($"No company with the code '{code}'."));

        var problem = await WorkshopController.CheckLogoAsync(file, ct);
        if (problem is not null) return BadRequest(ApiResponse.Failure(problem));

        var previous = workshop.LogoPath;

        workshop.LogoPath = await photos.SaveLogoAsync(file, workshop.CompanyCode, ct);
        workshop.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        if (previous is not null) photos.Delete(previous);

        logger.LogInformation("Superadmin set the logo for company {Code}", workshop.CompanyCode);

        return Ok(ApiResponse<CompanyDto>.Ok(
            Card(workshop), $"Logo set for {workshop.Name}."));
    }

    /// <summary>Removes a company's logo.</summary>
    [HttpDelete("companies/{code}/logo")]
    [ProducesResponseType<ApiResponse<CompanyDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<CompanyDto>>> DeleteCompanyLogo(
        string code, CancellationToken ct)
    {
        var workshop = await db.Workshops.FirstOrDefaultAsync(
            w => w.CompanyCode == code.ToUpperInvariant(), ct);

        if (workshop is null)
            return NotFound(ApiResponse.Failure($"No company with the code '{code}'."));

        var previous = workshop.LogoPath;

        workshop.LogoPath = null;
        workshop.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        if (previous is not null) photos.Delete(previous);

        return Ok(ApiResponse<CompanyDto>.Ok(
            Card(workshop), $"Logo removed from {workshop.Name}."));
    }

    /// <summary>
    /// One company as the console shows it, without the counts.
    /// </summary>
    /// <remarks>
    /// The counts need four grouped queries and the caller of this has just
    /// changed a logo, so they are left at zero rather than paid for. The list
    /// screen refetches after any mutation and fills them in.
    /// </remarks>
    private CompanyDto Card(Workshop w) => new()
    {
        CompanyCode = w.CompanyCode,
        Name = w.Name,
        LegalName = w.LegalName,
        Phone = w.Phone,
        Email = w.Email,
        Address = w.Address,
        LogoUrl = PhotoStorage.PublicUrl(Request, w.LogoPath),
        IsActive = w.IsActive,
        IsListed = w.IsListed,
        EnabledModules = Split(w.EnabledModules),
        UserCount = 0,
        CustomerCount = 0,
        JobCount = 0,
        CreatedAt = w.CreatedAt,
        LastActiveAt = null,
    };

    /// <summary>
    /// Deletes a company and everything belonging to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one irreversible action on the platform, so it is guarded twice: the
    /// company must already be suspended, and the caller has to type its code
    /// back. Requiring suspension first is not ceremony — it means somebody
    /// decided to stop this company before deciding to erase it, and gives
    /// whoever was using it a chance to notice.
    /// </para>
    /// <para>
    /// A real delete rather than a flag. "Suspended" already covers gone but
    /// recoverable; this is for a workshop that asked to be removed, and leaving
    /// their customers' names and phone numbers in the database afterwards would
    /// not be honouring that.
    /// </para>
    /// </remarks>
    [HttpDelete("companies/{code}")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse>> DeleteCompany(
        string code, DeleteCompanyRequest request, CancellationToken ct)
    {
        var companyCode = code.ToUpperInvariant();

        var workshop = await db.Workshops.FirstOrDefaultAsync(
            w => w.CompanyCode == companyCode, ct);

        if (workshop is null)
            return NotFound(ApiResponse.Failure($"No company with code '{code}'."));

        if (!string.Equals(
                request.ConfirmCompanyCode.Trim(), companyCode, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(ApiResponse.Failure(
                "The company code did not match. Type it exactly to confirm."));
        }

        if (workshop.IsActive)
        {
            return BadRequest(ApiResponse.Failure(
                "Suspend the company first. Deleting a live workshop should never be one click."));
        }

        var name = workshop.Name;
        var logoPath = workshop.LogoPath;

        // Children before parents. The order below is what the foreign keys
        // demand, not a preference — SQL Server refuses several of the cascade
        // paths into Customers, so these rows have to go by hand and in order.
        var jobIds = await db.JobCards.IgnoreQueryFilters()
            .Where(x => x.CompanyCode == companyCode).Select(x => x.Id).ToListAsync(ct);

        var bookingIds = await db.Bookings.IgnoreQueryFilters()
            .Where(x => x.CompanyCode == companyCode).Select(x => x.Id).ToListAsync(ct);

        var deliveryIds = await db.Deliveries.IgnoreQueryFilters()
            .Where(x => x.CompanyCode == companyCode).Select(x => x.Id).ToListAsync(ct);

        var userIds = await db.Users
            .Where(x => x.CompanyCode == companyCode).Select(x => x.Id).ToListAsync(ct);

        db.RemoveRange(await db.JobPhotos.Where(x => jobIds.Contains(x.JobCardId)).ToListAsync(ct));
        db.RemoveRange(await db.BookingServices.Where(x => bookingIds.Contains(x.BookingId)).ToListAsync(ct));
        db.RemoveRange(await db.DeliveryPoints.Where(x => deliveryIds.Contains(x.DeliveryId)).ToListAsync(ct));
        db.RemoveRange(await db.RefreshTokens.Where(x => userIds.Contains(x.UserId)).ToListAsync(ct));

        db.RemoveRange(await Owned(db.Payments, companyCode, ct));
        db.RemoveRange(await Owned(db.JobLines, companyCode, ct));
        db.RemoveRange(await Owned(db.Invoices, companyCode, ct));
        db.RemoveRange(await Owned(db.Deliveries, companyCode, ct));
        db.RemoveRange(await Owned(db.Bookings, companyCode, ct));
        db.RemoveRange(await Owned(db.JobCards, companyCode, ct));
        db.RemoveRange(await Owned(db.Vehicles, companyCode, ct));

        // The links and registrations point at customers, so they go first.
        db.RemoveRange(await db.UserWorkshopLinks
            .Where(x => x.CompanyCode == companyCode).ToListAsync(ct));
        db.RemoveRange(await db.CustomerRegistrations
            .Where(x => x.CompanyCode == companyCode).ToListAsync(ct));

        db.RemoveRange(await Owned(db.Customers, companyCode, ct));
        db.RemoveRange(await Owned(db.Services, companyCode, ct));
        db.RemoveRange(await Owned(db.Notifications, companyCode, ct));
        db.RemoveRange(await Owned(db.Activities, companyCode, ct));

        db.RemoveRange(await db.Branches.Where(x => x.CompanyCode == companyCode).ToListAsync(ct));
        db.RemoveRange(await db.Users.Where(x => x.CompanyCode == companyCode).ToListAsync(ct));

        // The access log outlives the company on purpose. It answers "who went
        // in there", and deleting the answer along with the subject would make
        // the trail useless exactly when somebody needs it.
        db.Workshops.Remove(workshop);

        await db.SaveChangesAsync(ct);

        // The one thing this company owned that is not a row. A workshop that
        // asked to be removed and whose logo is still being served from our
        // wwwroot has not been removed.
        if (logoPath is not null) photos.Delete(logoPath);

        logger.LogWarning(
            "Superadmin deleted company {Code} ({Name}) and all of its data", companyCode, name);

        return Ok(ApiResponse.Success($"{name} and all of its data have been deleted."));
    }

    /// <summary>
    /// Mints a token scoped to a company, and records that it happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The token carries that company and the Owner role, so every existing
    /// screen and every tenant filter behaves exactly as they do for the
    /// workshop's own staff. Nothing about the dashboard has to know this
    /// session began here.
    /// </para>
    /// <para>
    /// The log row is written before the token is returned. There is no path
    /// that hands one out without leaving the trail.
    /// </para>
    /// </remarks>
    [HttpPost("companies/{code}/impersonate")]
    [ProducesResponseType<ApiResponse<AuthResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<AuthResultDto>>> Impersonate(
        string code, ImpersonateRequest request, CancellationToken ct)
    {
        var companyCode = code.ToUpperInvariant();

        var workshop = await db.Workshops.AsNoTracking()
            .FirstOrDefaultAsync(w => w.CompanyCode == companyCode, ct);

        if (workshop is null)
            return NotFound(ApiResponse.Failure($"No company with code '{code}'."));

        var me = await db.Users.AsNoTracking().FirstOrDefaultAsync(
            u => u.Id == User.FindFirstValue(ClaimTypes.NameIdentifier), ct);

        if (me is null) return Unauthorized(ApiResponse.Failure("Please sign in again."));

        var now = clock.GetUtcNow().UtcDateTime;

        db.ImpersonationLogs.Add(new ImpersonationLog
        {
            UserId = me.Id,
            UserEmail = me.Email,
            CompanyCode = companyCode,
            At = now,
            Reason = request.Reason?.Trim() ?? "",
        });

        await db.SaveChangesAsync(ct);

        // A token for the superadmin *as* that company. Not a real user of the
        // workshop — deliberately, so nothing they do is attributed to a member
        // of staff who was not there.
        var session = new User
        {
            Id = me.Id,
            CompanyCode = companyCode,
            Email = me.Email,
            FullName = me.FullName,
            Role = "Owner",
            Workshop = workshop.Name,
        };

        var (accessToken, accessExpires) = tokens.CreateAccessToken(session);

        logger.LogWarning(
            "Superadmin {Email} is now acting as company {Code}", me.Email, companyCode);

        return Ok(ApiResponse<AuthResultDto>.Ok(
            new AuthResultDto
            {
                AccessToken = accessToken,
                AccessTokenExpiresAt = accessExpires,
                // No refresh token on purpose: an impersonated session should
                // expire on its own rather than be renewable for weeks.
                RefreshToken = "",
                RefreshTokenExpiresAt = accessExpires,
                User = new AuthUserDto
                {
                    Id = me.Id,
                    Email = me.Email,
                    Name = me.FullName,
                    Role = "Owner",
                    Workshop = workshop.Name,
                    CompanyCode = companyCode,
                },
            },
            $"Now acting as {workshop.Name}."));
    }

    /// <summary>Who has entered which company, most recent first.</summary>
    [HttpGet("impersonations")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<ImpersonationDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ImpersonationDto>>>> Impersonations(
        [FromQuery] string? companyCode, CancellationToken ct)
    {
        var rows = db.ImpersonationLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(companyCode))
            rows = rows.Where(l => l.CompanyCode == companyCode.ToUpperInvariant());

        var list = await rows
            .OrderByDescending(l => l.At)
            .Take(200)
            .Select(l => new ImpersonationDto
            {
                UserEmail = l.UserEmail,
                CompanyCode = l.CompanyCode,
                At = l.At,
                Reason = l.Reason,
            })
            .ToListAsync(ct);

        return Ok(ApiResponse<IReadOnlyList<ImpersonationDto>>.Ok(
            list, list.Count == 0 ? "No sessions recorded." : $"{list.Count} session(s)."));
    }

    /// <summary>
    /// Every row of one tenant-owned table belonging to a company.
    /// </summary>
    /// <remarks>
    /// The filter is off deliberately: the operator is inside no company, so the
    /// ambient filter would match nothing and the delete would quietly succeed
    /// having removed none of the data.
    /// </remarks>
    private static Task<List<T>> Owned<T>(DbSet<T> set, string companyCode, CancellationToken ct)
        where T : class, ITenantOwned =>
        set.IgnoreQueryFilters().Where(x => x.CompanyCode == companyCode).ToListAsync(ct);

    private static string[] Split(string modules) =>
        string.IsNullOrWhiteSpace(modules)
            ? []
            : modules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
