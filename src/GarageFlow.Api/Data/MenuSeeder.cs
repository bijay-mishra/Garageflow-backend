using GarageFlow.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Data;

/// <summary>
/// The menu catalogue, as the product ships it.
/// </summary>
/// <remarks>
/// <para>
/// Seeded from code rather than typed into a table, because every row here
/// names a React route that has to exist in the bundle. Adding a menu entry is
/// therefore a code change in both projects — which is honest, since adding a
/// screen was always going to be.
/// </para>
/// <para>
/// It runs on every start and is additive: a new release's new rows appear, and
/// anything an operator has renamed or reordered is left alone. The alternative
/// — reset the table to match the code — would silently undo their work on
/// every deploy.
/// </para>
/// </remarks>
public static class MenuSeeder
{
    private record Seed(
        string Key,
        string Label,
        string LabelNe,
        string Route,
        string Icon,
        int SortOrder,
        string? Module = null,
        string? ParentKey = null,
        bool IsLocked = false);

    // Order matches the sidebar top to bottom. The gaps in SortOrder are room to
    // insert a screen later without renumbering everything below it.
    private static readonly Seed[] Catalogue =
    [
        new("home", "Home", "गृहपृष्ठ", "/", "home", 10, IsLocked: true),

        new("customers", "Customers", "ग्राहक", "/customers", "users", 20),
        new("vehicles", "Vehicles", "सवारी साधन", "/vehicles", "truck", 30),
        new("job-cards", "Job Cards", "जब कार्ड", "/job-cards", "clipboard", 40),

        // The price list above the record of work done: you set up what you sell
        // before you can look back at having sold it.
        new("services", "Services", "सेवाहरू", "/services", "sparkles", 50, Module: "services"),
        new("service-history", "Service History", "सेवा इतिहास", "/service-history", "wrench", 60,
            Module: "serviceHistory"),

        new("billing", "Billing", "बिलिङ", "/billing", "banknotes", 70, Module: "billing"),

        // After billing: a handover is the last step of a job, and the fee it can
        // add has already landed on the bill by the time anyone opens this.
        new("deliveries", "Deliveries", "डेलिभरी", "/deliveries", "map", 80, Module: "deliveries"),

        new("reports", "Reports", "रिपोर्ट", "/reports", "chart", 90, Module: "reports"),

        // Customer questions waiting on a person here. Not gated on a module:
        // a workshop that can be asked a question can always answer it, and a
        // hidden inbox is an unanswered customer.
        new("support", "Customer chat", "ग्राहक च्याट", "/support", "chat", 95),

        new("settings", "Settings", "सेटिङ", "", "cog", 100),
        new("workshop", "Workshop", "वर्कशप", "/workshop", "storefront", 110, ParentKey: "settings"),
        new("staff", "Staff", "कर्मचारी", "/staff", "userGroup", 120, Module: "staff", ParentKey: "settings"),
        new("role-menus", "Role setup", "भूमिका सेटअप", "/role-menus", "listBullet", 130,
            ParentKey: "settings"),
        new("configuration", "Configuration", "कन्फिगरेसन", "/configuration", "adjustments", 135,
            ParentKey: "settings"),

        // Never hidden: this is your own password and your own email address.
        new("account", "My Account", "मेरो खाता", "/account", "userCircle", 140,
            ParentKey: "settings", IsLocked: true),
    ];

    /// <summary>
    /// Rows this release renames, as key → the label the last one shipped.
    /// </summary>
    /// <remarks>
    /// The seed is additive, so a row whose wording changes in
    /// <see cref="Catalogue"/> keeps the old wording on every database that
    /// already has it. Naming the old label here lets the rename land — and
    /// only where nobody has since typed their own, which is the whole reason
    /// the seed is additive in the first place.
    /// </remarks>
    private static readonly Dictionary<string, string> Renamed = new()
    {
        ["role-menus"] = "Menu access",
    };

    public static async Task SeedAsync(GarageFlowDbContext db, CancellationToken ct = default)
    {
        await RenameAsync(db, ct);

        var existing = await db.MenuItems.Select(m => m.Key).ToListAsync(ct);

        var missing = Catalogue.Where(s => !existing.Contains(s.Key)).ToList();

        if (missing.Count == 0) return;

        db.MenuItems.AddRange(missing.Select(s => new MenuItem
        {
            Key = s.Key,
            Label = s.Label,
            LabelNe = s.LabelNe,
            Route = s.Route,
            Icon = s.Icon,
            ParentKey = s.ParentKey,
            SortOrder = s.SortOrder,
            Module = s.Module,
            IsLocked = s.IsLocked,
            IsActive = true,
        }));

        await db.SaveChangesAsync(ct);
    }

    private static async Task RenameAsync(GarageFlowDbContext db, CancellationToken ct)
    {
        var keys = Renamed.Keys.ToList();

        var rows = await db.MenuItems.Where(m => keys.Contains(m.Key)).ToListAsync(ct);

        var changed = false;

        foreach (var row in rows)
        {
            var seed = Catalogue.FirstOrDefault(s => s.Key == row.Key);

            // Only where the row still says what the last release said. An
            // operator who has renamed it themselves has made a decision, and
            // a deploy is not the place to overrule it.
            if (seed is null || row.Label != Renamed[row.Key]) continue;

            row.Label = seed.Label;
            row.LabelNe = seed.LabelNe;
            changed = true;
        }

        if (changed) await db.SaveChangesAsync(ct);
    }
}
