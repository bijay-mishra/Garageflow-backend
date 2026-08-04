namespace GarageFlow.Api.Contracts;

// ── Response DTOs ────────────────────────────────────────────────────────────
// Every shape here matches an interface in the dashboard's src/types/index.ts.
// ASP.NET Core serialises with camelCase by default, so `VehicleCount` arrives
// as `vehicleCount` — no attributes needed. Change one of these and the
// matching TypeScript interface has to change with it.
//
// These are written as init-only properties rather than positional records on
// purpose: EF Core can only resolve a member back through an object-initializer
// projection, and that is what lets the list endpoints sort and filter on
// computed columns (`totalSpent`, `status`) inside SQL instead of in memory.

/// <summary>
/// A customer as the list and detail views consume it. <see cref="VehicleCount"/>
/// and <see cref="TotalSpent"/> are computed per request, never stored.
/// </summary>
public record CustomerDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Phone { get; init; }
    public required string Email { get; init; }
    public required string Address { get; init; }

    /// <summary>
    /// Map pin, or null if nobody has placed one. The address is what you post
    /// a bill to; this is what a driver navigates to.
    /// </summary>
    public required double? Latitude { get; init; }
    public required double? Longitude { get; init; }

    /// <summary>Number of vehicles on file.</summary>
    public required int VehicleCount { get; init; }

    /// <summary>Lifetime amount billed to this customer, tax included.</summary>
    public required decimal TotalSpent { get; init; }

    public required DateOnly CreatedAt { get; init; }

    /// <summary>Tailwind class for the list avatar, e.g. <c>bg-brand-500</c>.</summary>
    public required string AvatarColor { get; init; }
}

/// <summary>A vehicle plus its owner's name.</summary>
public record VehicleDto
{
    public required string Id { get; init; }
    public required string CustomerId { get; init; }
    public required string CustomerName { get; init; }
    public required string Make { get; init; }
    public required string Model { get; init; }
    public required int Year { get; init; }
    public required string Plate { get; init; }
    public required string Vin { get; init; }

    /// <summary>Body class — one of Bike, Car, Van, Bus, Truck, Tractor.</summary>
    public required string Type { get; init; }

    /// <summary>One of Petrol, Diesel, Electric, Hybrid, CNG.</summary>
    public required string Fuel { get; init; }

    /// <summary>Last recorded odometer reading, in km.</summary>
    public required int Odometer { get; init; }
    public required string Color { get; init; }

    /// <summary>Completion date of the most recent finished job, or null if never serviced.</summary>
    public required DateOnly? LastServiceDate { get; init; }
}

/// <summary>A labour, parts or service line on a job card.</summary>
public record JobLineDto
{
    public required string Description { get; init; }

    /// <summary>Hours for labour, units for parts and services.</summary>
    public required decimal Qty { get; init; }
    public required decimal UnitPrice { get; init; }

    /// <summary><c>labour</c>, <c>part</c> or <c>service</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// The price-list entry this line came from, or null when it was typed in.
    /// The client uses it only to mark the row as catalogue-priced.
    /// </summary>
    public required string? ServiceId { get; init; }
}

// ── Service catalogue ────────────────────────────────────────────────────────

/// <summary>
/// One item on the workshop's menu, as the Services screen and both apps list it.
/// </summary>
public record ServiceDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>Washing, Detailing, Maintenance, Repair, Inspection, Convenience or Other.</summary>
    public required string Category { get; init; }

    public required decimal Price { get; init; }

    /// <summary>Rough bay time in minutes; 0 when the shop does not quote one.</summary>
    public required int DurationMinutes { get; init; }

    public required bool IsActive { get; init; }

    /// <summary>False for services the shop adds itself and customers cannot order.</summary>
    public required bool IsBookable { get; init; }

    /// <summary>
    /// The stored column: vehicle types this is offered for, comma separated.
    /// Hidden from the JSON — clients read <see cref="AppliesTo"/> instead.
    /// </summary>
    /// <remarks>
    /// Carried on the DTO because the projection has to stay an expression tree
    /// for EF to translate it, and <c>string.Split</c> has no SQL equivalent.
    /// Splitting it here costs nothing: a price list is tens of rows, not
    /// thousands.
    ///
    /// Not <c>required</c>, unlike every other member: System.Text.Json refuses
    /// to configure a type that marks a property required and then ignores it,
    /// and the failure is a 500 on serialisation rather than a compile error.
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public string VehicleTypes { get; init; } = "";

    /// <summary>Vehicle types this is offered for. Empty means every vehicle.</summary>
    public IReadOnlyList<string> AppliesTo => Domain.Service.Split(VehicleTypes);

    /// <summary>How many job cards have carried this service. Retire, don't delete.</summary>
    public required int TimesUsed { get; init; }
}

/// <summary>
/// A job card with the vehicle and customer fields the list views need
/// denormalised onto it.
/// </summary>
public record JobCardDto
{
    public required string Id { get; init; }
    public required string VehicleId { get; init; }
    public required string VehiclePlate { get; init; }

    /// <summary>Reads "Toyota Corolla 2019".</summary>
    public required string VehicleLabel { get; init; }

    public required string CustomerId { get; init; }
    public required string CustomerName { get; init; }
    public required string Complaint { get; init; }

    /// <summary>Open, In Progress, Awaiting Parts, Completed, Delivered or Cancelled.</summary>
    public required string Status { get; init; }

    /// <summary>Low, Normal, High or Urgent.</summary>
    public required string Priority { get; init; }

    public required string Mechanic { get; init; }
    public required int Odometer { get; init; }
    public required DateOnly CreatedAt { get; init; }
    public required DateOnly PromisedAt { get; init; }

    /// <summary>Stamped when the job reaches Completed or Delivered.</summary>
    public required DateOnly? CompletedAt { get; init; }

    public required IReadOnlyList<JobLineDto> Lines { get; init; }

    /// <summary>Sum of all lines. Only ever computed by the server.</summary>
    public required decimal Total { get; init; }
}

/// <summary>
/// A bill. <see cref="Tax"/>, <see cref="Total"/> and <see cref="Status"/> are
/// derived from the subtotal, rate and amount paid on every read.
/// </summary>
public record InvoiceDto
{
    public required string Id { get; init; }
    public required string JobCardId { get; init; }
    public required string CustomerId { get; init; }
    public required string CustomerName { get; init; }
    public required string VehiclePlate { get; init; }
    public required DateOnly IssuedAt { get; init; }
    public required decimal Subtotal { get; init; }

    /// <summary>Fractional VAT rate, e.g. 0.13.</summary>
    public required decimal TaxRate { get; init; }

    public required decimal Tax { get; init; }
    public required decimal Total { get; init; }
    public required decimal Paid { get; init; }

    /// <summary>Outstanding balance. Projected so the list can sort on it in SQL.</summary>
    public required decimal Due { get; init; }

    /// <summary>Paid, Partial or Unpaid.</summary>
    public required string Status { get; init; }

    /// <summary>Method of the most recent payment; null until something is paid.</summary>
    public required string? Method { get; init; }
}

/// <summary>
/// Billing totals across every invoice, for the cards above the invoice table.
/// Computed in SQL so the page never has to download all invoices to add them up.
/// </summary>
public record InvoiceSummaryDto
{
    /// <summary>Sum of invoice totals, tax included.</summary>
    public required decimal Billed { get; init; }

    /// <summary>Sum of amounts actually received.</summary>
    public required decimal Collected { get; init; }

    /// <summary>Billed minus collected.</summary>
    public required decimal Outstanding { get; init; }
}

/// <summary>One receipt against an invoice — the audit trail behind `paid`.</summary>
public record PaymentDto
{
    public required int Id { get; init; }
    public required decimal Amount { get; init; }

    /// <summary>Cash, Card, eSewa, Khalti or Bank Transfer.</summary>
    public required string Method { get; init; }

    /// <summary>
    /// <c>cash</c>, <c>online</c> or <c>bank</c> — how the money moved, as
    /// opposed to whose brand carried it. What the end-of-day count is against.
    /// </summary>
    public required string Channel { get; init; }

    /// <summary>Pending, Completed, Failed or Cancelled. Only Completed is money.</summary>
    public required string Status { get; init; }

    /// <summary>
    /// Our side of the reference: the id sent to the gateway for an online
    /// payment, or the slip number staff typed in for a bank transfer.
    /// </summary>
    public required string? Reference { get; init; }

    /// <summary>The gateway's own transaction id. Null for anything recorded by hand.</summary>
    public required string? ProviderRef { get; init; }

    /// <summary>Why an attempt did not settle, when it did not.</summary>
    public required string? FailureReason { get; init; }

    public required DateTime At { get; init; }
}

/// <summary>Where the customer has to be sent to pay, and how.</summary>
public record PaymentStartDto
{
    /// <summary>Our reference for this attempt — quote it when verifying.</summary>
    public required string Reference { get; init; }

    public required string Provider { get; init; }
    public required decimal Amount { get; init; }

    /// <summary>
    /// <c>redirect</c> — open <see cref="Url"/>.
    /// <c>form-post</c> — the gateway needs an HTML form POST, so open
    /// <see cref="Url"/> in a browser and let the page we serve submit it.
    /// </summary>
    public required string Method { get; init; }

    public required string Url { get; init; }

    /// <summary>Form fields for <c>form-post</c>; empty for a plain redirect.</summary>
    public required IReadOnlyDictionary<string, string> Fields { get; init; }
}

/// <summary>What the workshop looks like to the app and the settings screen.</summary>
public record WorkshopDto
{
    public required string Name { get; init; }
    public required string LegalName { get; init; }
    public required string Address { get; init; }
    public required string Phone { get; init; }
    public required string Email { get; init; }

    /// <summary>PAN, printed on every bill.</summary>
    public required string TaxNumber { get; init; }

    /// <summary>
    /// Absolute URL of the workshop's logo, or null if it has not set one.
    /// </summary>
    /// <remarks>
    /// Absolute, because the three clients that read it are all on other
    /// origins. Null rather than a placeholder image: what to show instead is
    /// the client's decision, and the printed invoice's answer (nothing, so the
    /// name sits where the mark would have) differs from the sidebar's.
    /// </remarks>
    public required string? LogoUrl { get; init; }

    /// <summary>Map pin, or null until somebody drops one.</summary>
    public required double? Latitude { get; init; }
    public required double? Longitude { get; init; }

    public required string OpeningHours { get; init; }
    public required string InvoiceFooter { get; init; }

    /// <summary>Shown on the garage's card in the customer app's directory.</summary>
    public required string About { get; init; }

    /// <summary>Whether customers can find and join this garage. Off by default.</summary>
    public required bool IsListed { get; init; }

    // ── Home delivery ────────────────────────────────────────────────────────

    /// <summary>
    /// Whether delivery can actually be offered — the flag *and* a workshop pin,
    /// since the fee is priced from the distance between two points.
    /// </summary>
    /// <summary>Bank details for a customer paying by transfer. Blank when unset.</summary>
    public required string BankName { get; init; }
    public required string BankAccountName { get; init; }
    public required string BankAccountNumber { get; init; }
    public required string BankBranch { get; init; }

    /// <summary>True when there is enough on file to actually pay into.</summary>
    public required bool CanBankTransfer { get; init; }

    public required bool CanDeliver { get; init; }

    public required bool DeliveryEnabled { get; init; }
    public required decimal DeliveryBaseFee { get; init; }
    public required decimal DeliveryPerKm { get; init; }

    /// <summary>Bills at or above this are delivered free. Zero disables the waiver.</summary>
    public required decimal DeliveryFreeAbove { get; init; }

    /// <summary>Furthest the shop will go, in km. Zero means no limit.</summary>
    public required double DeliveryMaxKm { get; init; }

    /// <summary>
    /// Gateways with usable credentials right now. The app draws a button per
    /// entry, so a workshop with no Khalti key never sees a Khalti button that
    /// dead-ends.
    /// </summary>
    public required IReadOnlyList<string> OnlineProviders { get; init; }
}

/// <summary>How much came in through each channel — the end-of-day question.</summary>
public record CollectionsByChannelDto
{
    public required decimal Cash { get; init; }
    public required decimal Online { get; init; }
    public required decimal Bank { get; init; }
    public required decimal Total { get; init; }

    /// <summary>Attempts still open. Not money, and deliberately kept apart from it.</summary>
    public required int PendingCount { get; init; }
}

// ── Printable invoice ────────────────────────────────────────────────────────

/// <summary>
/// Everything a printed bill needs, composed server-side in one request.
/// </summary>
/// <remarks>
/// Assembled here rather than stitched together in the browser from four calls
/// (invoice, payments, job card, customer) for two reasons. A print window that
/// fires four requests can render half a document if one of them is slow, and —
/// more importantly — an invoice deliberately has no foreign key to its job
/// card, so the job it was raised for can be gone. This endpoint handles that
/// case once, on the server, instead of leaving every client to discover it.
///
/// The money on this record is the invoice's own snapshot. Nothing is
/// recomputed from the job card, so reprinting last year's bill prints last
/// year's numbers even if the job was edited since.
/// </remarks>
public record InvoicePrintDto
{
    public required InvoiceDto Invoice { get; init; }

    /// <summary>Payments against it, oldest first — the bill shows how it was settled.</summary>
    public required IReadOnlyList<PaymentDto> Payments { get; init; }

    /// <summary>Address and contact, read live. Not snapshotted: a bill reprinted
    /// after the customer moved should carry the address that reaches them.</summary>
    public required string CustomerAddress { get; init; }
    public required string CustomerPhone { get; init; }
    public required string CustomerEmail { get; init; }

    /// <summary>"Toyota Corolla 2019", or empty if the vehicle has since gone.</summary>
    public required string VehicleLabel { get; init; }
    public required int Odometer { get; init; }

    /// <summary>What the work was. Empty when the job card no longer exists.</summary>
    public required string Complaint { get; init; }
    public required string Mechanic { get; init; }
    public required DateOnly? CompletedAt { get; init; }

    /// <summary>
    /// The itemised work. Empty when the job card has been deleted — the invoice
    /// still prints, with its totals, and <see cref="HasJobCard"/> says why there
    /// is no breakdown.
    /// </summary>
    public required IReadOnlyList<JobLineDto> Lines { get; init; }

    /// <summary>False when the job behind this bill has been deleted.</summary>
    public required bool HasJobCard { get; init; }
}

// ── Handover ─────────────────────────────────────────────────────────────────

/// <summary>Getting a finished vehicle back to its owner.</summary>
public record DeliveryDto
{
    public required string Id { get; init; }
    public required string JobCardId { get; init; }
    public required string CustomerId { get; init; }
    public required string CustomerName { get; init; }
    public required string CustomerPhone { get; init; }
    public required string VehiclePlate { get; init; }
    public required string VehicleLabel { get; init; }

    /// <summary>Pickup or HomeDelivery.</summary>
    public required string Method { get; init; }

    /// <summary>AwaitingChoice, Scheduled, OutForDelivery, Delivered or Cancelled.</summary>
    public required string Status { get; init; }

    public required string Address { get; init; }

    /// <summary>Destination pin. Null for a collection.</summary>
    public required double? Latitude { get; init; }
    public required double? Longitude { get; init; }

    /// <summary>Straight-line km from the workshop, as quoted.</summary>
    public required double? DistanceKm { get; init; }

    /// <summary>What was charged. Fixed when the customer chose, never recomputed.</summary>
    public required decimal Fee { get; init; }

    public required string Driver { get; init; }

    /// <summary>Where the driver was when they last reported. Null until they set off.</summary>
    public required double? DriverLatitude { get; init; }
    public required double? DriverLongitude { get; init; }
    public required DateTime? DriverAt { get; init; }

    public required DateTime CreatedAt { get; init; }
    public required DateTime? ChosenAt { get; init; }
    public required DateTime? StartedAt { get; init; }
    public required DateTime? CompletedAt { get; init; }
}

/// <summary>One point on a driver's route.</summary>
public record DeliveryPointDto(double Latitude, double Longitude, DateTime At);

/// <summary>
/// A live delivery, with the trail so far.
/// </summary>
/// <remarks>
/// Separate from <see cref="DeliveryDto"/> because the trail is only wanted by
/// whoever is watching the map — putting it on every row of a delivery list
/// would be tens of kilobytes nobody reads.
/// </remarks>
public record DeliveryTrackDto
{
    public required DeliveryDto Delivery { get; init; }

    /// <summary>The workshop's pin — where the journey started from.</summary>
    public required double? OriginLatitude { get; init; }
    public required double? OriginLongitude { get; init; }

    public required IReadOnlyList<DeliveryPointDto> Trail { get; init; }

    /// <summary>
    /// Seconds since the driver's phone last reported. Null before they set off.
    /// </summary>
    /// <remarks>
    /// The client shows this rather than assuming the marker is current: tracking
    /// only runs while the driver has the app open, so a stale position is normal
    /// and saying "3 minutes ago" is honest where a moving dot would not be.
    /// </remarks>
    public required int? SecondsSinceUpdate { get; init; }
}

/// <summary>What home delivery would cost, or why it is not on offer.</summary>
public record DeliveryQuoteDto
{
    public required bool Available { get; init; }
    public required double DistanceKm { get; init; }
    public required decimal Fee { get; init; }

    /// <summary>Set when <see cref="Available"/> is false — shown to the customer verbatim.</summary>
    public required string? Reason { get; init; }
}

/// <summary>An entry in the dashboard's recent-activity feed.</summary>
public record ActivityDto
{
    public required string Id { get; init; }
    public required DateTime At { get; init; }
    public required string Text { get; init; }

    /// <summary>One of job, invoice, customer, vehicle.</summary>
    public required string Kind { get; init; }
}

// List endpoints return ApiResponse<PagedList<T>> — see Contracts/ApiResponse.cs.

// ── Dashboard aggregate ──────────────────────────────────────────────────────

/// <summary>How many job cards currently sit in one status.</summary>
public record JobStatusCount(string Status, int Count);

/// <summary>One month on the revenue trend chart. <c>Label</c> is a short month name.</summary>
public record RevenuePoint(string Label, decimal Revenue, int Jobs);

/// <summary>
/// Everything the dashboard home page needs, in one round trip.
/// <c>RevenueDeltaPct</c> compares this month against the previous one.
/// </summary>
public record DashboardSummaryDto(
    decimal RevenueToday,
    decimal RevenueThisMonth,
    decimal RevenueDeltaPct,
    int OpenJobs,
    int CompletedThisMonth,
    int VehiclesInShop,
    int ActiveCustomers,
    decimal UnpaidTotal,
    IReadOnlyList<JobStatusCount> JobStatusBreakdown,
    IReadOnlyList<RevenuePoint> RevenueTrend,
    IReadOnlyList<ActivityDto> RecentActivity);
