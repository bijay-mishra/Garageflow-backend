using GarageFlow.Api.Contracts;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using GarageFlow.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Controllers;

/// <summary>
/// The branch and accounting year the session is looking at, and what else it
/// could be looking at.
/// </summary>
/// <remarks>
/// Read-only. Changing the selection is <c>POST /api/auth/select-workspace</c>,
/// and it lives there rather than here because the answer is a new token pair —
/// that belongs with the rest of the session, not on a settings endpoint.
///
/// This replaces a hardcoded list in the dashboard's <c>src/data/seed.ts</c>.
/// The branches in that file were an array of four names compiled into the
/// bundle, so every deployment of the product claimed to have a Pokhara branch.
/// </remarks>
[Authorize]
[ApiController]
[Route("api/workspace")]
[Produces("application/json")]
public class WorkspaceController(
    GarageFlowDbContext db,
    WorkspaceService workspace,
    CurrentUserService currentUser,
    TenantContext tenant,
    TimeProvider clock) : ControllerBase
{
    /// <summary>
    /// Which parts of the product this company has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dashboard used to keep this in localStorage, which meant the answer
    /// travelled with the browser rather than with the company: clearing site
    /// data restored every module, and a workshop that had never paid for
    /// deliveries could turn them on from the settings screen.
    /// </para>
    /// <para>
    /// This endpoint is the menu, not the lock. Hiding a route stops honest
    /// mistakes; the controllers behind each module still answer for themselves.
    /// A list that says "deliveries" is not permission to read anything.
    /// </para>
    /// </remarks>
    [HttpGet("modules")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<string>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<string>>>> Modules(CancellationToken ct)
    {
        // From the token, not from the caller's stored User row. During an
        // impersonated session the two disagree: the id in the token is the
        // operator's, whose own row carries no company, so a database lookup
        // answers for GarageFlow rather than for the workshop being viewed. The
        // token's company is what every tenant-filtered query on this request is
        // already scoped to, so reading the same value is what keeps the menu
        // and the data describing the same company.
        var companyCode = tenant.CompanyCode;

        if (companyCode is null)
            return Unauthorized(ApiResponse.Failure("Please sign in again."));

        var enabled = await db.Workshops.AsNoTracking()
            .Where(w => w.CompanyCode == companyCode)
            .Select(w => w.EnabledModules)
            .FirstOrDefaultAsync(ct);

        // A workshop row that predates module config has an empty string. Read
        // as "everything off" that would blank the menu of a working install, so
        // the default set stands in until an operator sets it explicitly.
        var modules = string.IsNullOrWhiteSpace(enabled)
            ? Vocabulary.DefaultModules
            : enabled.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(Vocabulary.Modules.Contains)
                .ToArray();

        return Ok(ApiResponse<IReadOnlyList<string>>.Ok(modules, $"{modules.Length} module(s)."));
    }

    /// <summary>Branches, accounting years, and which of each is selected.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<WorkspaceDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<WorkspaceDto>>> Get(CancellationToken ct)
    {
        var current = await workspace.CurrentAsync(User, ct);
        var currentYear = FiscalCalendar.Current(clock);

        var dto = new WorkspaceDto
        {
            Branches = current.Branches.Select(ToDto).ToList(),
            FiscalYears = FiscalCalendar.All(clock)
                .Select(y => new FiscalYearDto
                {
                    Code = y.Code,
                    Start = y.Start,
                    End = y.End,
                    IsCurrent = y.Code == currentYear.Code,
                })
                // Newest first: the year you want is nearly always the latest.
                .Reverse()
                .ToList(),
            BranchId = current.Branch?.Id,
            BranchName = current.Branch?.Name,
            FiscalYear = current.FiscalYear.Code,
            FiscalYearStart = current.FiscalYear.Start,
            FiscalYearEnd = current.FiscalYear.End,
            IsCurrentYear = current.FiscalYear.Code == currentYear.Code,
        };

        return Ok(ApiResponse<WorkspaceDto>.Ok(dto, "Workspace loaded."));
    }

    /// <summary>Branches for the caller's workshop.</summary>
    /// <remarks>
    /// Separate from the bundle above so a form that only needs the list — a
    /// branch picker on a create screen, later — does not also pull every
    /// fiscal year.
    /// </remarks>
    [HttpGet("branches")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<BranchDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<BranchDto>>>> Branches(CancellationToken ct)
    {
        var user = await currentUser.GetAsync(User, ct);
        var branches = await workspace.BranchesAsync(user?.CompanyCode, ct);

        return Ok(ApiResponse<IReadOnlyList<BranchDto>>.Ok(
            branches.Select(ToDto).ToList(),
            branches.Count == 0 ? "No branches set up." : $"{branches.Count} branch(es)."));
    }

    private static BranchDto ToDto(Branch b) => new()
    {
        Id = b.Id,
        Name = b.Name,
        Address = b.Address,
        Phone = b.Phone,
        IsDefault = b.IsDefault,
    };
}
