using System.ComponentModel.DataAnnotations;

namespace GarageFlow.Api.Contracts;

// ── Menus ────────────────────────────────────────────────────────────────────
// What the dashboard draws down its left side, and who decides.

/// <summary>One row of the menu, as a client should draw it.</summary>
public record MenuItemDto
{
    public required string Key { get; init; }
    public required string Label { get; init; }

    /// <summary>The Nepali label. Never empty — falls back to the English one.</summary>
    public required string LabelNe { get; init; }

    /// <summary>Empty on a group that only holds children.</summary>
    public required string Route { get; init; }

    /// <summary>An icon name to map to a component. Unknown names should fall back.</summary>
    public required string Icon { get; init; }

    public required string? ParentKey { get; init; }
    public required int SortOrder { get; init; }

    /// <summary>The module gating this row, or null if it is always available.</summary>
    public required string? Module { get; init; }

    /// <summary>True for rows a company cannot hide — home, and your own account.</summary>
    public required bool IsLocked { get; init; }

    /// <summary>False for a row the platform has retired. Only the console sees these.</summary>
    public required bool IsActive { get; init; }
}

/// <summary>One role, as this company named it.</summary>
public record CompanyRoleDto
{
    public required int Id { get; init; }

    /// <summary>What the workshop calls it. The identity everything points at.</summary>
    public required string Name { get; init; }

    /// <summary>Which product role the server authorises this as.</summary>
    public required string BaseRole { get; init; }

    public required string Description { get; init; }

    /// <summary>True for the four the product ships. Cannot be renamed or deleted.</summary>
    public required bool IsBuiltIn { get; init; }

    /// <summary>Active accounts in this role. Deleting one with people in it is refused.</summary>
    public required int StaffCount { get; init; }

    /// <summary>How many menu rows this role currently sees.</summary>
    public required int MenuCount { get; init; }
}

/// <summary>The role setup screen: every role, every menu row, and who sees what.</summary>
public record MenuMatrixDto
{
    public required IReadOnlyList<MenuItemDto> Items { get; init; }

    /// <summary>This company's roles, in the order to show them.</summary>
    public required IReadOnlyList<CompanyRoleDto> Roles { get; init; }

    /// <summary>
    /// role → menu key → visible.
    /// </summary>
    /// <remarks>
    /// Fully populated: defaults are resolved server-side so the screen never has
    /// to tell "switched off" apart from "never chosen", which is a distinction
    /// nobody looking at it could act on.
    /// </remarks>
    public required Dictionary<string, Dictionary<string, bool>> Access { get; init; }
}

/// <summary>Creates or renames a role.</summary>
public class SaveRoleRequest
{
    [Required]
    [StringLength(40, MinimumLength = 2)]
    public string Name { get; set; } = "";

    /// <summary>
    /// One of the product's own roles. What the server authorises this role as.
    /// </summary>
    /// <remarks>
    /// Ignored when editing a role that already has people in it — changing it
    /// would rewrite what those accounts are allowed to do, which is not
    /// something a rename should be able to smuggle in.
    /// </remarks>
    [Required]
    public string BaseRole { get; set; } = "";

    [StringLength(200)]
    public string Description { get; set; } = "";
}

/// <summary>Records one role's menu choices.</summary>
public class SaveRoleMenusRequest
{
    [Required]
    public string Role { get; set; } = "";

    /// <summary>
    /// menu key → visible. Rows left out fall back to the product default rather
    /// than being hidden, so a partial request cannot empty somebody's menu.
    /// </summary>
    public Dictionary<string, bool> Access { get; set; } = [];
}

/// <summary>Changes a menu row for every company on the platform.</summary>
public class UpdateMenuItemRequest
{
    // The key and the route are absent on purpose. A row names a screen that has
    // to exist in the client bundle, so letting either be edited here would
    // produce a menu entry leading to a 404 with a friendly label on it.

    [StringLength(80)] public string? Label { get; set; }
    [StringLength(80)] public string? LabelNe { get; set; }
    [StringLength(60)] public string? Icon { get; set; }

    public int? SortOrder { get; set; }

    /// <summary>The gating module. Send an empty string to make it always visible.</summary>
    [StringLength(40)] public string? Module { get; set; }

    public bool? IsActive { get; set; }
}
