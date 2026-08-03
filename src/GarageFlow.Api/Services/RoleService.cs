using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Services;

/// <summary>
/// The roles one company has named, and the accounts sitting in them.
/// </summary>
/// <remarks>
/// See <see cref="CompanyRole"/> for why a role has both a name and a base
/// role. This service owns the rule that the two stay consistent: a role always
/// resolves to one of the product's own, and a role that people are assigned to
/// cannot quietly change what those people are allowed to do.
/// </remarks>
public class RoleService(GarageFlowDbContext db, TimeProvider clock)
{
    /// <summary>
    /// This company's roles, built-ins first, seeding them if it has none.
    /// </summary>
    /// <remarks>
    /// Seeded on read rather than at sign-up so companies that existed before
    /// this screen did get their four rows the first time anybody opens it. The
    /// alternative — a migration that back-fills every tenant — does the same
    /// work at a worse time, and silently does nothing for a company created by
    /// an older build afterwards.
    /// </remarks>
    public async Task<List<CompanyRole>> ForCompanyAsync(
        string companyCode, CancellationToken ct = default)
    {
        var roles = await Owned(companyCode).ToListAsync(ct);

        if (roles.Count == 0)
        {
            var now = clock.GetUtcNow().UtcDateTime;

            db.CompanyRoles.AddRange(CompanyRoleDefaults.BuiltIn.Select(s => new CompanyRole
            {
                CompanyCode = companyCode,
                Name = s.Name,
                BaseRole = s.Name,
                Description = s.Description,
                IsBuiltIn = true,
                CreatedAt = now,
            }));

            await db.SaveChangesAsync(ct);

            roles = await Owned(companyCode).ToListAsync(ct);
        }

        // Built-ins in product order, then the company's own alphabetically.
        // Not by created date: a roles table people add to over years should
        // stay findable, and "whenever we happened to add it" is not an order
        // anybody can look something up in.
        var order = CompanyRoleDefaults.BuiltIn.Select(s => s.Name).ToList();

        return [.. roles
            .OrderByDescending(r => r.IsBuiltIn)
            .ThenBy(r => r.IsBuiltIn ? order.IndexOf(r.Name) : int.MaxValue)
            .ThenBy(r => r.Name)];
    }

    /// <summary>How many accounts sit in each role name.</summary>
    /// <remarks>
    /// Keyed by the company's role name, with accounts that have none counted
    /// against the base role they carry — that is the role they are effectively
    /// in, and leaving them out would let a role look empty and deletable when
    /// deleting it would strand somebody.
    /// </remarks>
    public async Task<Dictionary<string, int>> HeadcountAsync(
        string companyCode, CancellationToken ct = default)
    {
        var users = await db.Users.AsNoTracking()
            .Where(u => u.CompanyCode == companyCode && u.IsActive)
            .Select(u => new { u.Role, u.CompanyRoleName })
            .ToListAsync(ct);

        return users
            .GroupBy(u => string.IsNullOrWhiteSpace(u.CompanyRoleName) ? u.Role : u.CompanyRoleName!)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// The role a user's menu should be looked up under.
    /// </summary>
    /// <remarks>
    /// Falls back to the base role, which is what every account had before this
    /// existed and what an impersonated session carries.
    /// </remarks>
    public static string MenuRoleOf(User user) =>
        string.IsNullOrWhiteSpace(user.CompanyRoleName) ? user.Role : user.CompanyRoleName!;

    /// <summary>
    /// The base role a name resolves to, or null if this company has no such role.
    /// </summary>
    public async Task<string?> BaseRoleOfAsync(
        string companyCode, string roleName, CancellationToken ct = default)
    {
        // A built-in name is its own base even before the seed has run — the
        // very first sign-in at a new company happens before anyone has opened
        // the roles screen.
        if (CompanyRoleDefaults.BuiltIn.Any(s => s.Name == roleName)) return roleName;

        return await Owned(companyCode)
            .Where(r => r.Name == roleName)
            .Select(r => r.BaseRole)
            .FirstOrDefaultAsync(ct);
    }

    private IQueryable<CompanyRole> Owned(string companyCode) =>
        db.CompanyRoles.IgnoreQueryFilters().Where(r => r.CompanyCode == companyCode);
}
