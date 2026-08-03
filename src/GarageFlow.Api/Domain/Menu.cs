namespace GarageFlow.Api.Domain;

// ── The menu ─────────────────────────────────────────────────────────────────
// What each person sees down the left of the dashboard.
//
// It used to be a hardcoded array in the client's src/lib/navigation.ts, which
// meant every workshop got the same menu and the only way to change one was to
// ship a new bundle. Two different questions were being answered by that one
// array, and neither well:
//
//   1. What did this company buy?   → the module gate, set by the operator
//   2. What does this job need?     → the role gate, set by the company's owner
//
// They stack. A mechanic at a company without deliveries does not see
// deliveries because of (1); the owner at the same company does. An advisor
// there does not see Staff accounts because of (2), even though the company has
// it. Both have to pass.
//
// Neither is a security boundary and neither is written as one. Hiding a row
// stops honest mistakes and keeps the menu short; the controllers behind each
// route still answer for themselves.

/// <summary>
/// One row in the dashboard's menu, as the platform defines it.
/// </summary>
/// <remarks>
/// <para>
/// Platform-wide rather than per-company: a menu entry points at a React route
/// that has to exist in the bundle, so companies choose from this list rather
/// than writing their own. <see cref="Key"/> and <see cref="Route"/> are seeded
/// from code and never editable through the API for that reason — a row naming
/// a route nobody wrote is a 404 with a friendly label on it.
/// </para>
/// <para>
/// What an operator <i>can</i> change is the wording, the order, which module
/// gates it, and whether it exists at all. That covers every reason anyone has
/// actually wanted to touch the menu without letting them break it.
/// </para>
/// </remarks>
public class MenuItem
{
    public int Id { get; set; }

    /// <summary>Stable identifier. Never changes, never shown to anyone.</summary>
    public string Key { get; set; } = default!;

    public string Label { get; set; } = "";

    /// <summary>The Nepali label. Falls back to <see cref="Label"/> when blank.</summary>
    public string LabelNe { get; set; } = "";

    /// <summary>Where it goes. Empty on a group that only holds children.</summary>
    public string Route { get; set; } = "";

    /// <summary>An icon name the client maps to a component. Unknown names fall back.</summary>
    public string Icon { get; set; } = "";

    /// <summary>The <see cref="Key"/> of the group this sits under, or null for top level.</summary>
    public string? ParentKey { get; set; }

    public int SortOrder { get; set; }

    /// <summary>
    /// The module a company must have for this to appear. Null means always.
    /// </summary>
    /// <remarks>
    /// Customers, vehicles and job cards carry none on purpose — they are the
    /// product rather than an add-on, and a workshop with those hidden has
    /// nothing left to sign in for.
    /// </remarks>
    public string? Module { get; set; }

    /// <summary>
    /// False retires the row platform-wide.
    /// </summary>
    /// <remarks>
    /// For a screen that has been withdrawn. Kept as a flag rather than deleted
    /// so the per-role choices companies made about it survive it coming back.
    /// </remarks>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// True for rows whose visibility is not the company's to decide.
    /// </summary>
    /// <remarks>
    /// The dashboard home and a person's own account. Hiding either produces a
    /// user who cannot navigate or cannot change their own password, which is
    /// never what anyone meant to configure.
    /// </remarks>
    public bool IsLocked { get; set; }
}

/// <summary>
/// One company's decision about whether a role sees a menu row.
/// </summary>
/// <remarks>
/// Rows exist only where somebody has made a choice. A company that has never
/// opened the screen has none at all, and falls back to
/// <see cref="MenuDefaults"/> — which is why a new workshop has a working menu
/// on day one rather than an empty rail.
/// </remarks>
public class RoleMenu : ITenantOwned
{
    public int Id { get; set; }

    public string CompanyCode { get; set; } = default!;

    /// <summary>Owner, Manager, Advisor or Mechanic.</summary>
    public string Role { get; set; } = default!;

    public string MenuKey { get; set; } = default!;

    public bool CanView { get; set; }
}

/// <summary>
/// What each role sees before anybody configures anything.
/// </summary>
/// <remarks>
/// Written as "who is refused" rather than "who is allowed". The allow-list
/// version has to be revisited every time a screen is added, and the failure is
/// silent — the new screen simply never appears for anyone and nobody knows
/// why. This way a new screen is visible by default and the exceptions stay
/// short enough to read.
/// </remarks>
public static class MenuDefaults
{
    /// <summary>Menu keys each role does not get until somebody says otherwise.</summary>
    private static readonly Dictionary<string, string[]> HiddenFor = new()
    {
        // The owner sees the whole product. There is nobody above them to
        // withhold anything, and a workshop where the owner cannot reach the
        // books is not a workshop anyone would run.
        ["Owner"] = [],

        // A manager runs the floor. Everything except the bank details and who
        // gets paid what, which stay with the owner.
        ["Manager"] = ["staff"],

        // Front desk: books cars in, takes payment, answers the phone. Not the
        // reports and not the staff list.
        //
        // Menu access is hidden for a harder reason than taste: the endpoint
        // behind it answers only Owner and Manager, so a visible row would send
        // them to a screen that loads nothing. A menu entry has to be one this
        // person can actually use, or it is just a trap with a label.
        ["Advisor"] = ["staff", "reports", "workshop", "role-menus", "configuration"],

        // A mechanic uses the phone app. If they open the dashboard at all it is
        // to look at the work.
        ["Mechanic"] =
        [
            "staff", "reports", "workshop", "billing", "services", "deliveries", "role-menus",
            "configuration",
        ],
    };

    public static bool CanView(string role, string menuKey) =>
        !HiddenFor.TryGetValue(role, out var hidden) || !hidden.Contains(menuKey);
}
