using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Services;

/// <summary>
/// Works out what one person sees down the left of the dashboard.
/// </summary>
/// <remarks>
/// <para>
/// Two gates, and both have to pass. The <b>module</b> gate is what the company
/// bought and is set by the platform operator; the <b>role</b> gate is what the
/// job needs and is set by the company's own owner. Keeping them apart is the
/// point — "we don't pay for deliveries" and "the front desk shouldn't see the
/// staff list" are different sentences said by different people, and a single
/// list of permissions would force one of them to overwrite the other.
/// </para>
/// <para>
/// Neither is a security boundary. A hidden row is a shorter menu, not a locked
/// door; every controller behind these routes still checks for itself. This
/// service is deliberately not consulted by any of them, so nobody can later
/// mistake it for authorisation.
/// </para>
/// </remarks>
public class MenuService(GarageFlowDbContext db)
{
    /// <summary>The whole catalogue, retired rows included, in display order.</summary>
    public Task<List<MenuItem>> CatalogueAsync(CancellationToken ct = default) =>
        db.MenuItems.AsNoTracking().OrderBy(m => m.SortOrder).ToListAsync(ct);

    /// <summary>
    /// The menu for one person at one company.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="role"/> is the company's own name for the role — "CEO",
    /// "Front desk" — and <paramref name="baseRole"/> is the product role it is
    /// backed by. Choices are recorded against the name, because that is what
    /// the person editing the screen picked; unchosen rows fall back to the
    /// defaults for the base role, because a brand-new "CEO" backed by Owner
    /// should start out looking like an owner rather than looking like nothing.
    /// </para>
    /// <para>
    /// A group whose children have all been hidden is dropped with them. An
    /// empty "Settings" that opens onto nothing is worse than no Settings at
    /// all — it reads as a bug rather than as a choice somebody made.
    /// </para>
    /// </remarks>
    public async Task<List<MenuItem>> ForAsync(
        string companyCode, string role, string? baseRole = null, CancellationToken ct = default)
    {
        var all = await db.MenuItems.AsNoTracking()
            .Where(m => m.IsActive)
            .OrderBy(m => m.SortOrder)
            .ToListAsync(ct);

        var modules = await ModulesOfAsync(companyCode, ct);
        var overrides = await OverridesAsync(companyCode, role, ct);
        var fallback = string.IsNullOrWhiteSpace(baseRole) ? role : baseRole;

        bool Visible(MenuItem m)
        {
            if (m.IsLocked) return true;
            if (m.Module is not null && !modules.Contains(m.Module)) return false;

            return overrides.TryGetValue(m.Key, out var chosen)
                ? chosen
                : MenuDefaults.CanView(fallback, m.Key);
        }

        var visible = all.Where(Visible).ToList();
        var keys = visible.Select(m => m.Key).ToHashSet();

        return visible
            // A child whose parent did not survive would render at top level,
            // which is not what hiding the parent meant.
            .Where(m => m.ParentKey is null || keys.Contains(m.ParentKey))
            .Where(m => !IsEmptyGroup(m, visible))
            .ToList();
    }

    /// <summary>
    /// This company's choices, as a role → menu key → visible map.
    /// </summary>
    /// <remarks>
    /// Every role and every row is present in the answer, filled in from
    /// <see cref="MenuDefaults"/> where nobody has chosen. A screen that had to
    /// tell "off" apart from "not set" would be showing the user a distinction
    /// they cannot act on.
    /// </remarks>
    /// <param name="companyCode">The company whose choices to read.</param>
    /// <param name="roles">
    /// This company's roles, as name → base role. Built-ins and the company's
    /// own alike; the matrix has a column for each.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Dictionary<string, Dictionary<string, bool>>> MatrixAsync(
        string companyCode,
        IReadOnlyDictionary<string, string> roles,
        CancellationToken ct = default)
    {
        var all = await db.MenuItems.AsNoTracking()
            .OrderBy(m => m.SortOrder)
            .ToListAsync(ct);

        var saved = await db.RoleMenus.AsNoTracking()
            .Where(r => r.CompanyCode == companyCode)
            .ToListAsync(ct);

        var matrix = new Dictionary<string, Dictionary<string, bool>>();

        foreach (var (role, baseRole) in roles)
        {
            var forRole = saved.Where(r => r.Role == role)
                .ToDictionary(r => r.MenuKey, r => r.CanView);

            matrix[role] = all.ToDictionary(
                m => m.Key,
                m => m.IsLocked
                    || (forRole.TryGetValue(m.Key, out var chosen)
                        ? chosen
                        : MenuDefaults.CanView(baseRole, m.Key)));
        }

        return matrix;
    }

    /// <summary>
    /// Carries a role's menu choices over to its new name.
    /// </summary>
    /// <remarks>
    /// Choices are keyed by name rather than by role id, which is what makes
    /// this necessary — and is still the right key: it is what the save request
    /// carries and what reads look up, so a lookup never has to resolve an id
    /// first. Renaming is rare; reading the menu happens on every page.
    /// </remarks>
    public async Task RenameAsync(
        string companyCode, string from, string to, CancellationToken ct = default)
    {
        var rows = await db.RoleMenus
            .Where(r => r.CompanyCode == companyCode && r.Role == from)
            .ToListAsync(ct);

        if (rows.Count == 0) return;

        foreach (var row in rows) row.Role = to;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Forgets every choice made about a role, when it is deleted.</summary>
    public async Task ForgetAsync(string companyCode, string role, CancellationToken ct = default)
    {
        var rows = await db.RoleMenus
            .Where(r => r.CompanyCode == companyCode && r.Role == role)
            .ToListAsync(ct);

        if (rows.Count == 0) return;

        db.RoleMenus.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Records one role's menu choices, replacing whatever was there.
    /// </summary>
    /// <remarks>
    /// Locked rows are dropped rather than rejected. The client does not send
    /// them and a hand-rolled request that does is trying to hide somebody's own
    /// account page from them, which is not a configuration this product offers.
    /// </remarks>
    public async Task SaveAsync(
        string companyCode, string role, Dictionary<string, bool> choices, CancellationToken ct = default)
    {
        var locked = await db.MenuItems.AsNoTracking()
            .Where(m => m.IsLocked)
            .Select(m => m.Key)
            .ToListAsync(ct);

        var known = await db.MenuItems.AsNoTracking().Select(m => m.Key).ToListAsync(ct);

        var existing = await db.RoleMenus
            .Where(r => r.CompanyCode == companyCode && r.Role == role)
            .ToListAsync(ct);

        db.RoleMenus.RemoveRange(existing);

        db.RoleMenus.AddRange(choices
            .Where(c => known.Contains(c.Key) && !locked.Contains(c.Key))
            .Select(c => new RoleMenu
            {
                CompanyCode = companyCode,
                Role = role,
                MenuKey = c.Key,
                CanView = c.Value,
            }));

        await db.SaveChangesAsync(ct);
    }

    private async Task<HashSet<string>> ModulesOfAsync(string companyCode, CancellationToken ct)
    {
        var enabled = await db.Workshops.AsNoTracking()
            .Where(w => w.CompanyCode == companyCode)
            .Select(w => w.EnabledModules)
            .FirstOrDefaultAsync(ct);

        // A workshop row that predates module config carries an empty string.
        // Read as "everything off" it would blank the menu of a working install,
        // so the default set stands in until an operator sets one explicitly.
        return string.IsNullOrWhiteSpace(enabled)
            ? [.. Vocabulary.DefaultModules]
            : [.. enabled.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private async Task<Dictionary<string, bool>> OverridesAsync(
        string companyCode, string role, CancellationToken ct) =>
        await db.RoleMenus.AsNoTracking()
            .Where(r => r.CompanyCode == companyCode && r.Role == role)
            .ToDictionaryAsync(r => r.MenuKey, r => r.CanView, ct);

    private static bool IsEmptyGroup(MenuItem item, List<MenuItem> visible) =>
        string.IsNullOrWhiteSpace(item.Route)
        && !visible.Any(other => other.ParentKey == item.Key);
}
