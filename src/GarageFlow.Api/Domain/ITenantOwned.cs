namespace GarageFlow.Api.Domain;

/// <summary>
/// A row that belongs to one company.
/// </summary>
/// <remarks>
/// Implementing this is what puts an entity behind the global query filter in
/// <see cref="Data.GarageFlowDbContext"/> and gets its company stamped on save.
/// Both happen by reflection over this interface, so adding a new tenant-scoped
/// table is one interface and a migration — there is no list to remember to
/// update, which is the point.
///
/// Not everything is tenant-owned. <c>Workshop</c> and <c>Branch</c> carry a
/// company code but are read *across* companies by the garage directory and the
/// superadmin, so they stay outside the filter and are scoped by hand.
/// </remarks>
public interface ITenantOwned
{
    /// <summary>The company that owns this row. Never null once saved.</summary>
    string CompanyCode { get; set; }
}
