namespace GarageFlow.Api.Domain;

/// <summary>
/// A priced item on the workshop's menu — a body wash, an interior detail, an
/// AC regas, a pickup and drop. Ids look like <c>SVC-001</c>.
/// </summary>
/// <remarks>
/// This is a price list, not work. Nothing here belongs to a customer or a
/// vehicle; it is what the shop *offers*. Choosing one puts a
/// <see cref="JobLine"/> on a job card at <see cref="Price"/>, and from that
/// point the line stands on its own — editing the catalogue afterwards never
/// rewrites a job that has already been quoted.
///
/// A washing charge was always possible as a hand-typed labour line. What was
/// missing is the list: without one, every advisor types "wash" at whatever
/// price they remember, and nobody can answer "what do we charge for a wash?"
/// </remarks>
public class Service : ITenantOwned
{
    /// <summary>The company that owns this row.</summary>
    /// <remarks>
    /// Set automatically on save from the request's token, and enforced by a
    /// global query filter — no controller reads or writes it directly.
    /// </remarks>
    public string CompanyCode { get; set; } = default!;
    public string Id { get; set; } = default!;

    public string Name { get; set; } = default!;
    public string Description { get; set; } = "";

    /// <summary>One of <see cref="Vocabulary.ServiceCategories"/>.</summary>
    public string Category { get; set; } = "Other";

    /// <summary>Standard price. What lands on the job card unless it is edited.</summary>
    public decimal Price { get; set; }

    /// <summary>Rough bay time, in minutes. 0 when it is not worth quoting one.</summary>
    public int DurationMinutes { get; set; }

    /// <summary>
    /// Vehicle types this is offered for, comma separated — <c>"Bike,Car"</c>.
    /// Empty means every vehicle.
    /// </summary>
    /// <remarks>
    /// Stored as one string rather than a join table on purpose: it is a short
    /// closed list, it is only ever read whole, and keeping it a column is what
    /// lets the filter run as a plain LIKE in SQL. A bus wash and a scooter wash
    /// are different jobs at different prices, so the shop wants two rows here —
    /// not one row that has to be haggled over at the counter.
    /// </remarks>
    public string VehicleTypes { get; set; } = "";

    /// <summary>
    /// Retired services stop being offered but stay on the jobs that used them,
    /// so last year's invoices still read correctly.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether a customer may pick this in the app. Some things — a courtesy
    /// wash, an internal inspection — are the shop's to add, not the customer's
    /// to order.
    /// </summary>
    public bool IsBookable { get; set; } = true;

    public DateOnly CreatedAt { get; set; }

    /// <summary>
    /// Every job line ever raised from this service. Exists so the Services
    /// screen can count usage in SQL and offer "retire" rather than "delete" on
    /// anything that has been sold.
    /// </summary>
    public List<JobLine> JobLines { get; set; } = [];

    /// <summary>The vehicle types as a list; empty means all of them.</summary>
    public string[] AppliesTo => Split(VehicleTypes);

    /// <summary>True when this service is offered for <paramref name="vehicleType"/>.</summary>
    public bool AppliesToType(string vehicleType)
    {
        var types = AppliesTo;
        return types.Length == 0 || types.Contains(vehicleType, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Shared with the DTO so both sides split the column identically.</summary>
    public static string[] Split(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The inverse, for writes. Order and casing are the caller's.</summary>
    public static string Join(IEnumerable<string>? types) =>
        types is null ? "" : string.Join(',', types.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()));
}

/// <summary>
/// A service a customer asked for when they made a booking.
/// </summary>
/// <remarks>
/// <see cref="QuotedPrice"/> is a snapshot, not a lookup. The customer saw a
/// number in the app before they tapped Request, and that is the number the
/// workshop is held to when the booking becomes a job card — even if the price
/// list moved in between.
/// </remarks>
public class BookingService
{
    public int Id { get; set; }

    public string BookingId { get; set; } = default!;
    public Booking? Booking { get; set; }

    public string ServiceId { get; set; } = default!;
    public Service? Service { get; set; }

    /// <summary>The price shown to the customer at the moment they asked.</summary>
    public decimal QuotedPrice { get; set; }
}
