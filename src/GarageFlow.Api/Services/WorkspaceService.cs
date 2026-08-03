using System.Security.Claims;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Services;

/// <summary>
/// Which branch and accounting year the current request is answered against.
/// </summary>
/// <remarks>
/// Resolved from the database rather than trusted from the token, for the same
/// reason <see cref="CurrentUserService"/> re-reads the user: a branch can be
/// closed and a selection can stop being valid while a token minted beforehand
/// keeps asserting it. The token is what makes the choice *travel*; this is what
/// decides whether it still holds.
///
/// Cached per request — every list endpoint asks, and a workshop with four
/// branches should not pay four queries for one answer.
/// </remarks>
public class WorkspaceService(
    GarageFlowDbContext db,
    CurrentUserService currentUser,
    TimeProvider clock)
{
    private Workspace? _cached;

    /// <summary>The active workspace, falling back where a selection cannot be honoured.</summary>
    public async Task<Workspace> CurrentAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;

        var user = await currentUser.GetAsync(principal, ct);

        // Unresolvable year → the current one, not an error. A shop whose
        // selection has aged out of the offered range should see this year's
        // books rather than a failed screen.
        var fiscalYear = FiscalCalendar.Find(user?.FiscalYear, clock)
                         ?? FiscalCalendar.Current(clock);

        var branches = await BranchesAsync(user?.CompanyCode, ct);

        var branch = branches.FirstOrDefault(b => b.Id == user?.BranchId)
                     ?? branches.FirstOrDefault(b => b.IsDefault)
                     ?? branches.FirstOrDefault();

        return _cached = new Workspace(branch, fiscalYear, branches);
    }

    /// <summary>
    /// The accounting year to filter a list by, or null to filter by nothing.
    /// </summary>
    /// <remarks>
    /// Null for anyone who is not staff, and that is the whole point of this
    /// method existing separately from <see cref="CurrentAsync"/>. Several
    /// endpoints — bookings, handovers — are read by both the dashboard and the
    /// customer app. A fiscal year is an accounting boundary the *business*
    /// keeps; a car owner looking at their own history has no idea what year
    /// their last service falls in and would simply find it missing.
    ///
    /// So the window applies to the workshop's own screens and never to a
    /// customer's.
    /// </remarks>
    public async Task<FiscalYear?> StaffYearAsync(
        ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var user = await currentUser.GetAsync(principal, ct);

        // Mechanics are excluded alongside customers, deliberately. A mechanic's
        // list is the work in front of them, and a job opened before mid-July
        // that is still on the ramp in August is exactly the job they most need
        // to see. Only the roles that read the books get the boundary.
        if (user is null || !Vocabulary.StaffRoles.Contains(user.Role)) return null;

        return (await CurrentAsync(principal, ct)).FiscalYear;
    }

    /// <summary>Selectable branches for a tenant, in display order.</summary>
    public async Task<IReadOnlyList<Branch>> BranchesAsync(
        string? companyCode, CancellationToken ct = default)
    {
        var code = companyCode ?? DbSeeder.DemoCompanyCode;

        return await db.Branches.AsNoTracking()
            .Where(b => b.CompanyCode == code && b.IsActive)
            .OrderByDescending(b => b.IsDefault)
            .ThenBy(b => b.Name)
            .ToListAsync(ct);
    }
}

/// <summary>
/// The resolved workspace for one request.
/// </summary>
/// <param name="Branch">
/// The selected branch, or null for a tenant with none on record. Null is
/// ordinary, not broken — a single-site workshop never needs one.
/// </param>
/// <param name="FiscalYear">Always set: there is always an accounting year.</param>
/// <param name="Branches">Everything selectable, so the caller need not re-query.</param>
public record Workspace(
    Branch? Branch,
    FiscalYear FiscalYear,
    IReadOnlyList<Branch> Branches);
