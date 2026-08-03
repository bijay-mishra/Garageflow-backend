namespace GarageFlow.Api.Domain;

// ── Roles a company names for itself ─────────────────────────────────────────
// A workshop's org chart is not the product's. One calls the person who runs
// the floor a Manager, the next calls them Admin, a third has a CEO who wants
// the books but not the staff list. Forcing all of them onto four fixed names
// meant either lying on the staff screen or asking for a code change.
//
// So a company names its own roles — and every one of them is *backed by* one
// of the four the product ships. That backing is the load-bearing part:
//
//   Name       what the workshop calls them        "CEO"
//   BaseRole   what the server authorises as       "Owner"
//
// Every [Authorize(Roles = "Owner,Manager")] in this codebase checks the base
// role, and the token carries the base role in its role claim. A role the
// server has never heard of would be refused by every endpoint, so a custom
// role that did not resolve to a known one would be a role that can sign in and
// do nothing — a setting whose only effect is a support ticket.
//
// What the custom role adds on top is its own menu. Two people both authorised
// as Owner can see different sidebars, which is the whole reason anybody wanted
// this.

/// <summary>
/// A role as one company names it.
/// </summary>
/// <remarks>
/// The four built-ins exist as rows too, seeded per company, rather than being
/// special-cased in code. It means the roles table is the one answer to "what
/// roles are there here", the menu editor has nothing to branch on, and a
/// company that renames nothing still has a complete list to look at.
/// </remarks>
public class CompanyRole : ITenantOwned
{
    public int Id { get; set; }

    public string CompanyCode { get; set; } = default!;

    /// <summary>What this workshop calls it — "CEO", "Front desk", "Manager".</summary>
    public string Name { get; set; } = default!;

    /// <summary>
    /// Which of <see cref="Vocabulary.MenuRoles"/> the server authorises this as.
    /// </summary>
    /// <remarks>
    /// Not editable once staff are assigned, and the screen says why: changing
    /// it silently rewrites what those people are allowed to do, which is not a
    /// dropdown's worth of consequence.
    /// </remarks>
    public string BaseRole { get; set; } = default!;

    /// <summary>A short line for the roles table. Optional.</summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// True for the four the product seeds.
    /// </summary>
    /// <remarks>
    /// They cannot be deleted or renamed. Every existing account, every default
    /// in <see cref="MenuDefaults"/> and every authorisation attribute is
    /// written against these names; a company that renamed Owner to something
    /// else would have accounts pointing at a role that no longer exists.
    /// Their menus are still theirs to edit, which is the part anyone wanted.
    /// </remarks>
    public bool IsBuiltIn { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// The four roles every company starts with.
/// </summary>
public static class CompanyRoleDefaults
{
    public record Seed(string Name, string Description);

    /// <summary>Keyed by base role, in the order the roles table lists them.</summary>
    public static readonly Seed[] BuiltIn =
    [
        new("Owner", "Runs the business. Sees everything, including the money."),
        new("Manager", "Runs the floor. Everything except pay and bank details."),
        new("Advisor", "Front desk — books cars in, takes payment, answers the phone."),
        new("Mechanic", "Does the work. Uses the phone app."),
    ];
}
