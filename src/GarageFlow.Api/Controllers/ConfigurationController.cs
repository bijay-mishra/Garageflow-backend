using GarageFlow.Api.Contracts;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using GarageFlow.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Controllers;

/// <summary>
/// The lists a workshop keeps about itself: accounting years and locations.
/// </summary>
/// <remarks>
/// <para>
/// One controller serving two callers. A workshop's owner reaches it for their
/// own company; a platform operator reaches it with <c>?companyCode=</c> to work
/// on somebody's behalf. Same endpoints, same validation, same messages — a
/// second "admin" copy of these rules is how the two drift until one of them
/// permits something the other refuses.
/// </para>
/// <para>
/// The company is resolved once, in <see cref="ScopeAsync"/>, and every query
/// below is written against the answer. An operator's requests carry
/// <c>IgnoreQueryFilters</c> because their token belongs to no company, so the
/// ambient tenant filter would match nothing and every list would come back
/// empty rather than refused.
/// </para>
/// </remarks>
[Authorize(Roles = "Owner,Manager,SuperAdmin")]
[ApiController]
[Route("api/configuration")]
[Produces("application/json")]
public class ConfigurationController(
    GarageFlowDbContext db,
    TenantContext tenant,
    TimeProvider clock) : ControllerBase
{
    // ── Fiscal years ─────────────────────────────────────────────────────────

    /// <summary>Every accounting year this company keeps, newest first.</summary>
    [HttpGet("fiscal-years")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<FiscalYearRecordDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<FiscalYearRecordDto>>>> FiscalYears(
        [FromQuery] string? companyCode, CancellationToken ct)
    {
        var scope = await ScopeAsync(companyCode, ct);
        if (scope is null) return NoCompany();

        await EnsureSeededAsync(scope, ct);

        var years = await Years(scope).OrderByDescending(y => y.Start).ToListAsync(ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        // Counted per year so the delete confirmation can say what it would
        // orphan. Two queries rather than one per year — the list is short, but
        // a query inside a loop is a habit worth not forming.
        var invoices = await db.Invoices.IgnoreQueryFilters()
            .Where(i => i.CompanyCode == scope)
            .Select(i => i.IssuedAt)
            .ToListAsync(ct);

        var jobs = await db.JobCards.IgnoreQueryFilters()
            .Where(j => j.CompanyCode == scope)
            .Select(j => j.CreatedAt)
            .ToListAsync(ct);

        var list = years.Select(y => new FiscalYearRecordDto
        {
            Id = y.Id,
            Code = y.Code,
            Start = y.Start,
            End = y.End,
            IsClosed = y.IsClosed,
            IsCurrent = today >= y.Start && today <= y.End,
            InvoiceCount = invoices.Count(d => d >= y.Start && d <= y.End),
            JobCount = jobs.Count(d => d >= y.Start && d <= y.End),
        }).ToList();

        return Ok(ApiResponse<IReadOnlyList<FiscalYearRecordDto>>.Ok(
            list, $"{list.Count} fiscal year(s)."));
    }

    /// <summary>Adds an accounting year.</summary>
    [HttpPost("fiscal-years")]
    [ProducesResponseType<ApiResponse<FiscalYearRecordDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<FiscalYearRecordDto>>> CreateFiscalYear(
        [FromQuery] string? companyCode, SaveFiscalYearRequest request, CancellationToken ct)
    {
        var scope = await ScopeAsync(companyCode, ct);
        if (scope is null) return NoCompany();

        var code = request.Code.Trim();

        if (await Years(scope).AnyAsync(y => y.Code == code, ct))
            return BadRequest(ApiResponse.Failure($"{code} is already in the list."));

        if (Invalid(request) is { } problem) return BadRequest(ApiResponse.Failure(problem));

        var year = new FiscalYearRecord
        {
            CompanyCode = scope,
            Code = code,
            Start = request.Start,
            End = request.End,
            IsClosed = request.IsClosed,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };

        db.FiscalYearRecords.Add(year);
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<FiscalYearRecordDto>.Ok(ToDto(year), $"{code} added."));
    }

    /// <summary>Changes an accounting year's window or closes it.</summary>
    [HttpPut("fiscal-years/{id:int}")]
    [ProducesResponseType<ApiResponse<FiscalYearRecordDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<FiscalYearRecordDto>>> UpdateFiscalYear(
        int id, [FromQuery] string? companyCode, SaveFiscalYearRequest request, CancellationToken ct)
    {
        var scope = await ScopeAsync(companyCode, ct);
        if (scope is null) return NoCompany();

        var year = await Years(scope).FirstOrDefaultAsync(y => y.Id == id, ct);

        if (year is null) return NotFound(ApiResponse.Failure("That fiscal year is not in the list."));

        if (Invalid(request) is { } problem) return BadRequest(ApiResponse.Failure(problem));

        var code = request.Code.Trim();

        if (await Years(scope).AnyAsync(y => y.Code == code && y.Id != id, ct))
            return BadRequest(ApiResponse.Failure($"{code} is already in the list."));

        year.Code = code;
        year.Start = request.Start;
        year.End = request.End;
        year.IsClosed = request.IsClosed;

        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<FiscalYearRecordDto>.Ok(ToDto(year), $"{code} updated."));
    }

    /// <summary>
    /// Removes an accounting year.
    /// </summary>
    /// <remarks>
    /// Refused while anything is filed under it. Deleting the year would not
    /// delete the bills — it would leave them in a window nothing describes,
    /// which is a worse state than an unwanted row in a list.
    /// </remarks>
    [HttpDelete("fiscal-years/{id:int}")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse>> DeleteFiscalYear(
        int id, [FromQuery] string? companyCode, CancellationToken ct)
    {
        var scope = await ScopeAsync(companyCode, ct);
        if (scope is null) return NoCompany();

        var year = await Years(scope).FirstOrDefaultAsync(y => y.Id == id, ct);

        if (year is null) return NotFound(ApiResponse.Failure("That fiscal year is not in the list."));

        var invoices = await db.Invoices.IgnoreQueryFilters()
            .CountAsync(i => i.CompanyCode == scope && i.IssuedAt >= year.Start && i.IssuedAt <= year.End, ct);

        if (invoices > 0)
        {
            return BadRequest(ApiResponse.Failure(
                $"{invoices} bill(s) are filed under {year.Code}. Close the year instead of removing it."));
        }

        db.FiscalYearRecords.Remove(year);
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse.Success($"{year.Code} removed."));
    }

    // ── Branches ─────────────────────────────────────────────────────────────

    /// <summary>Every location, the closed ones included.</summary>
    [HttpGet("branches")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<BranchDetailDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<BranchDetailDto>>>> Branches(
        [FromQuery] string? companyCode, CancellationToken ct)
    {
        var scope = await ScopeAsync(companyCode, ct);
        if (scope is null) return NoCompany();

        await EnsureBranchAsync(scope, ct);

        var list = await db.Branches
            .Where(b => b.CompanyCode == scope)
            .OrderByDescending(b => b.IsDefault)
            .ThenBy(b => b.Name)
            .Select(b => ToDto(b))
            .ToListAsync(ct);

        return Ok(ApiResponse<IReadOnlyList<BranchDetailDto>>.Ok(
            list, $"{list.Count} branch(es)."));
    }

    /// <summary>Adds a location.</summary>
    [HttpPost("branches")]
    [ProducesResponseType<ApiResponse<BranchDetailDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<BranchDetailDto>>> CreateBranch(
        [FromQuery] string? companyCode, SaveBranchRequest request, CancellationToken ct)
    {
        var scope = await ScopeAsync(companyCode, ct);
        if (scope is null) return NoCompany();

        // Unfiltered: branch ids are unique across every company, so the
        // generator has to see them all or it will hand out one already taken.
        var ids = await db.Branches.IgnoreQueryFilters().Select(b => b.Id).ToListAsync(ct);

        var existing = await db.Branches.Where(b => b.CompanyCode == scope).ToListAsync(ct);

        var branch = new Branch
        {
            Id = Ids.Next(ids, "BR"),
            CompanyCode = scope,
            Name = request.Name.Trim(),
            Address = request.Address.Trim(),
            Phone = request.Phone.Trim(),
            // The first branch is the default whatever was asked for: a company
            // with no default has no branch to open a session on.
            IsDefault = request.IsDefault || existing.Count == 0,
            IsActive = request.IsActive,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };

        if (branch.IsDefault) foreach (var other in existing) other.IsDefault = false;

        db.Branches.Add(branch);
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<BranchDetailDto>.Ok(ToDto(branch), $"{branch.Name} added."));
    }

    /// <summary>Changes a location.</summary>
    [HttpPut("branches/{id}")]
    [ProducesResponseType<ApiResponse<BranchDetailDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<BranchDetailDto>>> UpdateBranch(
        string id, [FromQuery] string? companyCode, SaveBranchRequest request, CancellationToken ct)
    {
        var scope = await ScopeAsync(companyCode, ct);
        if (scope is null) return NoCompany();

        var all = await db.Branches.Where(b => b.CompanyCode == scope).ToListAsync(ct);
        var branch = all.FirstOrDefault(b => b.Id == id);

        if (branch is null) return NotFound(ApiResponse.Failure($"No branch '{id}'."));

        branch.Name = request.Name.Trim();
        branch.Address = request.Address.Trim();
        branch.Phone = request.Phone.Trim();

        if (request.IsDefault && !branch.IsDefault)
        {
            foreach (var other in all) other.IsDefault = false;
            branch.IsDefault = true;
        }

        // The default cannot be closed. Sessions fall back to it, so closing it
        // would leave everyone pointing at a branch they are not allowed to use.
        branch.IsActive = branch.IsDefault || request.IsActive;

        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<BranchDetailDto>.Ok(ToDto(branch), $"{branch.Name} updated."));
    }

    /// <summary>Removes a location.</summary>
    /// <remarks>
    /// The default is refused, and so is the last one. A company always has
    /// somewhere for its work to happen — "no branches" is not a state any
    /// screen in the product knows how to draw.
    /// </remarks>
    [HttpDelete("branches/{id}")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse>> DeleteBranch(
        string id, [FromQuery] string? companyCode, CancellationToken ct)
    {
        var scope = await ScopeAsync(companyCode, ct);
        if (scope is null) return NoCompany();

        var all = await db.Branches.Where(b => b.CompanyCode == scope).ToListAsync(ct);
        var branch = all.FirstOrDefault(b => b.Id == id);

        if (branch is null) return NotFound(ApiResponse.Failure($"No branch '{id}'."));

        if (all.Count == 1)
            return BadRequest(ApiResponse.Failure("A company needs at least one branch."));

        if (branch.IsDefault)
        {
            return BadRequest(ApiResponse.Failure(
                "Make another branch the default first — sessions fall back to this one."));
        }

        db.Branches.Remove(branch);
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse.Success($"{branch.Name} removed."));
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Which company this request is about, or null if there is no answer.
    /// </summary>
    /// <remarks>
    /// A workshop's own staff get their token's company and the query parameter
    /// is ignored — accepting it would let a manager read another company by
    /// guessing a code, which is exactly the boundary the rest of this codebase
    /// spends its effort defending. Only an operator, who belongs to no company,
    /// may name one.
    /// </remarks>
    private async Task<string?> ScopeAsync(string? companyCode, CancellationToken ct)
    {
        var isOperator = User.IsInRole(Vocabulary.SuperAdminRole);

        if (!isOperator)
        {
            var own = tenant.CompanyCode;
            return string.IsNullOrWhiteSpace(own) ? null : own;
        }

        if (string.IsNullOrWhiteSpace(companyCode)) return null;

        var code = companyCode.Trim().ToUpperInvariant();

        return await db.Workshops.AsNoTracking().AnyAsync(w => w.CompanyCode == code, ct)
            ? code
            : null;
    }

    private ActionResult NoCompany() =>
        BadRequest(ApiResponse.Failure(
            User.IsInRole(Vocabulary.SuperAdminRole)
                ? "Choose a company first."
                : "Your account is not attached to a workshop."));

    /// <summary>This company's years, with the ambient filter out of the way.</summary>
    private IQueryable<FiscalYearRecord> Years(string companyCode) =>
        db.FiscalYearRecords.IgnoreQueryFilters().Where(y => y.CompanyCode == companyCode);

    /// <summary>
    /// Fills an empty list from the published national calendar.
    /// </summary>
    /// <remarks>
    /// So a company that has never opened this screen sees the right years
    /// rather than a blank page and a New button. The dates are the published
    /// ones — a workshop should be correcting a calendar, not typing one.
    /// </remarks>
    private async Task EnsureSeededAsync(string companyCode, CancellationToken ct)
    {
        if (await Years(companyCode).AnyAsync(ct)) return;

        var now = clock.GetUtcNow().UtcDateTime;

        db.FiscalYearRecords.AddRange(FiscalCalendar.All(clock).Select(y => new FiscalYearRecord
        {
            CompanyCode = companyCode,
            Code = y.Code,
            Start = y.Start,
            End = y.End,
            IsClosed = false,
            CreatedAt = now,
        }));

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Gives a company with no locations its first one.
    /// </summary>
    /// <remarks>
    /// Delete refuses to take the last branch away, so "none" is a state this
    /// controller will not create — but it is one that exists, in companies
    /// signed up before the console started seeding a branch with them. Healing
    /// it on read rather than in a migration keeps the fix in the same place as
    /// the rule it is upholding, and the alternative is a branch picker with
    /// nothing in it and a Delete button that refuses to explain itself.
    /// </remarks>
    private async Task EnsureBranchAsync(string companyCode, CancellationToken ct)
    {
        if (await db.Branches.AnyAsync(b => b.CompanyCode == companyCode, ct)) return;

        // Unfiltered: branch ids are unique across every company.
        var ids = await db.Branches.IgnoreQueryFilters().Select(b => b.Id).ToListAsync(ct);

        var workshop = await db.Workshops.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(w => w.CompanyCode == companyCode, ct);

        db.Branches.Add(new Branch
        {
            Id = Ids.Next(ids, "BR"),
            CompanyCode = companyCode,
            Name = "Main Branch",
            Address = workshop?.Address ?? "",
            Phone = workshop?.Phone ?? "",
            IsDefault = true,
            IsActive = true,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        });

        await db.SaveChangesAsync(ct);
    }

    private static string? Invalid(SaveFiscalYearRequest request)
    {
        if (request.End <= request.Start) return "The year has to end after it starts.";

        var days = request.End.DayNumber - request.Start.DayNumber;

        // Loose on purpose. A fiscal year is roughly 365 days and this is not the
        // place to insist on exactly which — but a two-day or three-year "year"
        // is a typo, and catching it here beats discovering it in a report.
        return days is < 300 or > 400
            ? "A fiscal year should be about twelve months long."
            : null;
    }

    private static FiscalYearRecordDto ToDto(FiscalYearRecord y) => new()
    {
        Id = y.Id,
        Code = y.Code,
        Start = y.Start,
        End = y.End,
        IsClosed = y.IsClosed,
        IsCurrent = false,
        InvoiceCount = 0,
        JobCount = 0,
    };

    private static BranchDetailDto ToDto(Branch b) => new()
    {
        Id = b.Id,
        Name = b.Name,
        Address = b.Address,
        Phone = b.Phone,
        IsDefault = b.IsDefault,
        IsActive = b.IsActive,
        CreatedAt = b.CreatedAt,
    };
}
