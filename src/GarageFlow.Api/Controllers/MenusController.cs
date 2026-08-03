using GarageFlow.Api.Contracts;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using GarageFlow.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Controllers;

/// <summary>
/// The dashboard's menu, and who at this company sees what.
/// </summary>
/// <remarks>
/// <para>
/// The menu used to be a hardcoded array compiled into the client, so every
/// workshop got the same one and changing it meant shipping a bundle. It is
/// now assembled per person from two independent decisions: which modules the
/// company was given, and which rows its owner has granted each role.
/// </para>
/// <para>
/// None of this is authorisation, and it is worth being blunt about that: an
/// advisor whose Reports row is hidden can still type /reports, and the reports
/// endpoint is what decides whether they get an answer. This controller makes
/// menus shorter and more honest, nothing more.
/// </para>
/// </remarks>
[Authorize]
[ApiController]
[Route("api/menus")]
[Produces("application/json")]
public class MenusController(
    GarageFlowDbContext db,
    MenuService menus,
    RoleService roles,
    CurrentUserService currentUser,
    TenantContext tenant,
    TimeProvider clock) : ControllerBase
{
    /// <summary>The signed-in user's own menu, in display order.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<IReadOnlyList<MenuItemDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<MenuItemDto>>>> Mine(
        CancellationToken ct)
    {
        var user = await currentUser.GetAsync(User, ct);

        if (user is null) return Unauthorized(ApiResponse.Failure("Please sign in again."));

        // The company from the token, the base role from the token too — during
        // an impersonated session the stored row belongs to the operator and
        // says neither. The role claim is what the API itself authorises
        // against, so reading it here keeps the menu describing the same session.
        var companyCode = tenant.CompanyCode ?? "";
        var baseRole = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? user.Role;

        if (string.IsNullOrWhiteSpace(companyCode))
        {
            // A superadmin, or a customer who has joined no garage. Neither has
            // a workshop menu; the console draws its own and the app draws its own.
            return Ok(ApiResponse<IReadOnlyList<MenuItemDto>>.Ok([], "No workshop menu."));
        }

        // The company's own name for the role, which is what menu choices are
        // recorded against. An impersonated session has no stored row to read
        // one from and falls back to the base role, which is the right answer:
        // the operator is looking at the company, not standing in for a person.
        var role = user.CompanyCode == companyCode ? RoleService.MenuRoleOf(user) : baseRole;

        var items = await menus.ForAsync(companyCode, role, baseRole, ct);

        return Ok(ApiResponse<IReadOnlyList<MenuItemDto>>.Ok(
            items.Select(ToDto).ToList(), $"{items.Count} menu item(s)."));
    }

    /// <summary>Every role and every menu row, for the role setup screen.</summary>
    [HttpGet("access")]
    [Authorize(Roles = "Owner,Manager")]
    [ProducesResponseType<ApiResponse<MenuMatrixDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<MenuMatrixDto>>> Access(CancellationToken ct)
    {
        var companyCode = tenant.CompanyCode ?? "";

        if (string.IsNullOrWhiteSpace(companyCode)) return NoCompany();

        var matrix = await MatrixOf(companyCode, ct);

        return Ok(ApiResponse<MenuMatrixDto>.Ok(matrix, $"{matrix.Roles.Count} role(s)."));
    }

    /// <summary>Adds a role.</summary>
    [HttpPost("roles")]
    [Authorize(Roles = "Owner")]
    [ProducesResponseType<ApiResponse<MenuMatrixDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<MenuMatrixDto>>> CreateRole(
        SaveRoleRequest request, CancellationToken ct)
    {
        var companyCode = tenant.CompanyCode ?? "";

        if (string.IsNullOrWhiteSpace(companyCode)) return NoCompany();

        var name = request.Name.Trim();

        if (Invalid(request, name) is { } problem) return BadRequest(ApiResponse.Failure(problem));

        var existing = await roles.ForCompanyAsync(companyCode, ct);

        if (existing.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            return BadRequest(ApiResponse.Failure($"There is already a role called {name}."));

        db.CompanyRoles.Add(new CompanyRole
        {
            CompanyCode = companyCode,
            Name = name,
            BaseRole = request.BaseRole,
            Description = request.Description.Trim(),
            IsBuiltIn = false,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        });

        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<MenuMatrixDto>.Ok(await MatrixOf(companyCode, ct), $"{name} added."));
    }

    /// <summary>Renames a role, or changes what it is backed by.</summary>
    [HttpPut("roles/{id:int}")]
    [Authorize(Roles = "Owner")]
    [ProducesResponseType<ApiResponse<MenuMatrixDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<MenuMatrixDto>>> UpdateRole(
        int id, SaveRoleRequest request, CancellationToken ct)
    {
        var companyCode = tenant.CompanyCode ?? "";

        if (string.IsNullOrWhiteSpace(companyCode)) return NoCompany();

        var all = await roles.ForCompanyAsync(companyCode, ct);
        var role = all.FirstOrDefault(r => r.Id == id);

        if (role is null) return NotFound(ApiResponse.Failure("That role is not in the list."));

        var name = request.Name.Trim();

        if (Invalid(request, name) is { } problem) return BadRequest(ApiResponse.Failure(problem));

        if (role.IsBuiltIn && name != role.Name)
        {
            return BadRequest(ApiResponse.Failure(
                $"{role.Name} is a built-in role and cannot be renamed. Add a role of your own instead."));
        }

        if (all.Any(r => r.Id != id && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            return BadRequest(ApiResponse.Failure($"There is already a role called {name}."));

        var headcount = await roles.HeadcountAsync(companyCode, ct);
        var staff = headcount.GetValueOrDefault(role.Name);

        // Re-backing a role rewrites what everybody in it may do. Refused while
        // anyone is in it rather than confirmed, because the person clicking has
        // no way to see from here which accounts they are about to change.
        if (request.BaseRole != role.BaseRole && staff > 0)
        {
            return BadRequest(ApiResponse.Failure(
                $"{staff} account(s) are in {role.Name}. Move them out before changing what it is based on."));
        }

        // The name is the identity, so a rename has to move everything pointing
        // at it: the menu choices, and the accounts in the role. Missing either
        // fails silently — the role keeps its row in the table and the people in
        // it quietly drop back to their base role's default menu.
        if (name != role.Name) await RenameAsync(companyCode, role.Name, name, ct);

        role.Name = name;
        role.Description = request.Description.Trim();
        if (staff == 0) role.BaseRole = request.BaseRole;

        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<MenuMatrixDto>.Ok(await MatrixOf(companyCode, ct), $"{name} updated."));
    }

    /// <summary>Removes a role.</summary>
    /// <remarks>
    /// Refused for the built-ins and for any role with people in it. A deleted
    /// role would leave those accounts pointing at nothing, and the failure
    /// would show up as a blank sidebar at their next sign-in rather than here.
    /// </remarks>
    [HttpDelete("roles/{id:int}")]
    [Authorize(Roles = "Owner")]
    [ProducesResponseType<ApiResponse<MenuMatrixDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<MenuMatrixDto>>> DeleteRole(
        int id, CancellationToken ct)
    {
        var companyCode = tenant.CompanyCode ?? "";

        if (string.IsNullOrWhiteSpace(companyCode)) return NoCompany();

        var all = await roles.ForCompanyAsync(companyCode, ct);
        var role = all.FirstOrDefault(r => r.Id == id);

        if (role is null) return NotFound(ApiResponse.Failure("That role is not in the list."));

        if (role.IsBuiltIn)
        {
            return BadRequest(ApiResponse.Failure(
                $"{role.Name} is a built-in role and cannot be removed."));
        }

        var headcount = await roles.HeadcountAsync(companyCode, ct);
        var staff = headcount.GetValueOrDefault(role.Name);

        if (staff > 0)
        {
            return BadRequest(ApiResponse.Failure(
                $"{staff} account(s) are in {role.Name}. Move them to another role first."));
        }

        await menus.ForgetAsync(companyCode, role.Name, ct);

        db.CompanyRoles.Remove(role);
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<MenuMatrixDto>.Ok(
            await MatrixOf(companyCode, ct), $"{role.Name} removed."));
    }

    /// <summary>
    /// Records one role's menu.
    /// </summary>
    /// <remarks>
    /// Owner only. A manager can read the screen — knowing what the front desk
    /// sees is part of running the floor — but granting yourself a menu row is
    /// the sort of change that should have exactly one person's name on it.
    /// </remarks>
    [HttpPut("access")]
    [Authorize(Roles = "Owner")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse>> SaveAccess(
        SaveRoleMenusRequest request, CancellationToken ct)
    {
        var companyCode = tenant.CompanyCode ?? "";

        if (string.IsNullOrWhiteSpace(companyCode)) return NoCompany();

        var all = await roles.ForCompanyAsync(companyCode, ct);

        if (all.All(r => r.Name != request.Role))
            return BadRequest(ApiResponse.Failure($"'{request.Role}' is not a role at this workshop."));

        await menus.SaveAsync(companyCode, request.Role, request.Access, ct);

        return Ok(ApiResponse.Success($"Menu saved for {request.Role}."));
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    /// <summary>Moves everything that points at a role name to the new one.</summary>
    private async Task RenameAsync(
        string companyCode, string from, string to, CancellationToken ct)
    {
        await menus.RenameAsync(companyCode, from, to, ct);

        var accounts = await db.Users
            .Where(u => u.CompanyCode == companyCode && u.CompanyRoleName == from)
            .ToListAsync(ct);

        if (accounts.Count == 0) return;

        foreach (var account in accounts) account.CompanyRoleName = to;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The whole screen in one object.
    /// </summary>
    /// <remarks>
    /// Every write returns it too. A role's menu count and headcount both move
    /// when a role is added or deleted, and returning the new state beats the
    /// client guessing at it and then refetching to find out it guessed wrong.
    /// </remarks>
    private async Task<MenuMatrixDto> MatrixOf(string companyCode, CancellationToken ct)
    {
        var catalogue = await menus.CatalogueAsync(ct);
        var live = catalogue.Where(m => m.IsActive).ToList();

        var companyRoles = await roles.ForCompanyAsync(companyCode, ct);
        var headcount = await roles.HeadcountAsync(companyCode, ct);

        var access = await menus.MatrixAsync(
            companyCode, companyRoles.ToDictionary(r => r.Name, r => r.BaseRole), ct);

        return new MenuMatrixDto
        {
            Items = live.Select(ToDto).ToList(),
            Roles = companyRoles.Select(r => new CompanyRoleDto
            {
                Id = r.Id,
                Name = r.Name,
                BaseRole = r.BaseRole,
                Description = r.Description,
                IsBuiltIn = r.IsBuiltIn,
                StaffCount = headcount.GetValueOrDefault(r.Name),
                // Counted off the live catalogue, so a row the platform has
                // retired does not inflate a number nobody can see the source of.
                MenuCount = live.Count(m => access[r.Name].GetValueOrDefault(m.Key)),
            }).ToList(),
            Access = access,
        };
    }

    private ActionResult NoCompany() =>
        BadRequest(ApiResponse.Failure("Your account is not attached to a workshop."));

    private static string? Invalid(SaveRoleRequest request, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A role needs a name.";

        return Vocabulary.MenuRoles.Contains(request.BaseRole)
            ? null
            : $"'{request.BaseRole}' is not a role this can be based on.";
    }

    internal static MenuItemDto ToDto(MenuItem m) => new()
    {
        Key = m.Key,
        Label = m.Label,
        // Never empty: a half-translated menu with blanks in it is worse than
        // an English word somebody recognises.
        LabelNe = string.IsNullOrWhiteSpace(m.LabelNe) ? m.Label : m.LabelNe,
        Route = m.Route,
        Icon = m.Icon,
        ParentKey = m.ParentKey,
        SortOrder = m.SortOrder,
        Module = m.Module,
        IsLocked = m.IsLocked,
        IsActive = m.IsActive,
    };
}
