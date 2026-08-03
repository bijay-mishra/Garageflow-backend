using System.ComponentModel.DataAnnotations;
using GarageFlow.Api.Domain;

namespace GarageFlow.Api.Contracts;

// ── Request DTOs ─────────────────────────────────────────────────────────────
// Create requests mirror the `New*` types in src/types/index.ts: the server owns
// every derived field (ids, totals, denormalised names) so the client cannot
// send one.
//
// Update requests are PATCH-shaped even though the frontend sends them over PUT
// — the dashboard posts `Partial<T>`, so every property is nullable and only the
// ones actually present are applied. The trade-off: a property cannot be *set*
// back to null through an update, which only affects server-managed fields
// (completedAt, lastServiceDate) that you would not clear by hand anyway.

// ── Customers ────────────────────────────────────────────────────────────────

public class CreateCustomerRequest
{
    [Required, StringLength(160, MinimumLength = 1)]
    public string Name { get; set; } = "";

    [StringLength(40)]
    public string Phone { get; set; } = "";

    [EmailAddress, StringLength(160)]
    public string Email { get; set; } = "";

    [StringLength(300)]
    public string Address { get; set; } = "";

    /// <summary>
    /// Map pin. Optional — most customers will never have one, and a blank pin
    /// is not a validation failure.
    /// </summary>
    /// <remarks>
    /// Ranges are checked because a swapped lat/lng pair is the single most
    /// common way this goes wrong, and 85.32 degrees of latitude is the Arctic
    /// rather than Kathmandu. The check catches the obviously impossible; it
    /// cannot catch a pin dropped in the wrong street.
    /// </remarks>
    [Range(-90, 90, ErrorMessage = "Latitude must be between -90 and 90.")]
    public double? Latitude { get; set; }

    [Range(-180, 180, ErrorMessage = "Longitude must be between -180 and 180.")]
    public double? Longitude { get; set; }
}

public class UpdateCustomerRequest
{
    [StringLength(160, MinimumLength = 1)] public string? Name { get; set; }
    [StringLength(40)] public string? Phone { get; set; }
    [EmailAddress, StringLength(160)] public string? Email { get; set; }
    [StringLength(300)] public string? Address { get; set; }
    [StringLength(40)] public string? AvatarColor { get; set; }

    [Range(-90, 90, ErrorMessage = "Latitude must be between -90 and 90.")]
    public double? Latitude { get; set; }

    [Range(-180, 180, ErrorMessage = "Longitude must be between -180 and 180.")]
    public double? Longitude { get; set; }

    /// <summary>
    /// Send true to remove the pin. Needed because a partial update reads an
    /// absent property as "leave it alone", so there is otherwise no way to
    /// express "this pin was wrong, take it off".
    /// </summary>
    public bool? ClearLocation { get; set; }
}

// ── Vehicles ─────────────────────────────────────────────────────────────────

public class CreateVehicleRequest
{
    [Required, StringLength(20)]
    public string CustomerId { get; set; } = "";

    [Required, StringLength(80)] public string Make { get; set; } = "";
    [Required, StringLength(80)] public string Model { get; set; } = "";

    [Range(1900, 2200)] public int Year { get; set; }

    [Required, StringLength(40)] public string Plate { get; set; } = "";
    [StringLength(40)] public string Vin { get; set; } = "";

    [AllowedValues("Bike", "Car", "Van", "Bus", "Truck", "Tractor")]
    public string Type { get; set; } = "Car";

    [AllowedValues("Petrol", "Diesel", "Electric", "Hybrid", "CNG")]
    public string Fuel { get; set; } = "Petrol";

    /// <summary>Odometer reading in km.</summary>
    [Range(0, 10_000_000)] public int Odometer { get; set; }

    [StringLength(40)] public string Color { get; set; } = "";
}

public class UpdateVehicleRequest
{
    [StringLength(20)] public string? CustomerId { get; set; }
    [StringLength(80)] public string? Make { get; set; }
    [StringLength(80)] public string? Model { get; set; }
    [Range(1900, 2200)] public int? Year { get; set; }
    [StringLength(40)] public string? Plate { get; set; }
    [StringLength(40)] public string? Vin { get; set; }

    // null has to be listed explicitly: AllowedValues rejects it otherwise, and
    // an omitted property is exactly what a partial update looks like.
    [AllowedValues(null, "Bike", "Car", "Van", "Bus", "Truck", "Tractor")]
    public string? Type { get; set; }

    [AllowedValues(null, "Petrol", "Diesel", "Electric", "Hybrid", "CNG")]
    public string? Fuel { get; set; }

    [Range(0, 10_000_000)] public int? Odometer { get; set; }
    [StringLength(40)] public string? Color { get; set; }
}

// ── Job cards ────────────────────────────────────────────────────────────────

public class JobLineRequest
{
    [Required, StringLength(300)] public string Description { get; set; } = "";
    [Range(0, 100_000)] public decimal Qty { get; set; }
    [Range(0, 100_000_000)] public decimal UnitPrice { get; set; }

    [AllowedValues("labour", "part", "service")]
    public string Kind { get; set; } = "part";

    /// <summary>
    /// Set when this line came from the price list. Optional, and never trusted
    /// for the price — the description and amount on this request are what get
    /// billed, so an advisor can still discount a wash on the day.
    /// </summary>
    [StringLength(20)]
    public string? ServiceId { get; set; }
}

/// <summary>
/// Appends catalogue services to an existing job card.
/// </summary>
/// <remarks>
/// Separate from the full update because it is a different action with a
/// different risk. Updating a job card replaces the whole line set, so a client
/// that sends a stale copy silently wipes work someone else added; this only
/// ever adds, which is what "the car also needs a wash" means.
/// </remarks>
public class AddJobServicesRequest
{
    /// <summary>Catalogue ids. Anything already on the job is skipped, not duplicated.</summary>
    [Required, MinLength(1, ErrorMessage = "Choose at least one service.")]
    public List<string> ServiceIds { get; set; } = [];
}

public class CreateJobCardRequest
{
    [Required, StringLength(20)] public string VehicleId { get; set; } = "";
    [StringLength(1000)] public string Complaint { get; set; } = "";

    [AllowedValues("Open", "In Progress", "Awaiting Parts", "Completed", "Delivered", "Cancelled")]
    public string Status { get; set; } = "Open";

    [AllowedValues("Low", "Normal", "High", "Urgent")]
    public string Priority { get; set; } = "Normal";

    [StringLength(120)] public string Mechanic { get; set; } = "";
    [Range(0, 10_000_000)] public int Odometer { get; set; }

    /// <summary>Date the car is promised back to the customer.</summary>
    public DateOnly PromisedAt { get; set; }

    public List<JobLineRequest> Lines { get; set; } = [];
}

public class UpdateJobCardRequest
{
    [StringLength(20)] public string? VehicleId { get; set; }
    [StringLength(1000)] public string? Complaint { get; set; }

    // null has to be listed explicitly: AllowedValues rejects it otherwise, and
    // an omitted property is exactly what a partial update looks like.
    [AllowedValues(null, "Open", "In Progress", "Awaiting Parts", "Completed", "Delivered", "Cancelled")]
    public string? Status { get; set; }

    [AllowedValues(null, "Low", "Normal", "High", "Urgent")]
    public string? Priority { get; set; }

    [StringLength(120)] public string? Mechanic { get; set; }
    [Range(0, 10_000_000)] public int? Odometer { get; set; }
    public DateOnly? PromisedAt { get; set; }

    /// <summary>When supplied, replaces the whole line set.</summary>
    public List<JobLineRequest>? Lines { get; set; }
}

// ── Service catalogue ────────────────────────────────────────────────────────

public class CreateServiceRequest
{
    [Required, StringLength(160, MinimumLength = 1)]
    public string Name { get; set; } = "";

    [StringLength(500)] public string Description { get; set; } = "";

    [AllowedValues("Washing", "Detailing", "Maintenance", "Repair", "Inspection", "Convenience", "Other")]
    public string Category { get; set; } = "Other";

    [Range(0, 100_000_000)] public decimal Price { get; set; }

    /// <summary>Rough bay time in minutes. 0 when the shop does not quote one.</summary>
    [Range(0, 10_000)] public int DurationMinutes { get; set; }

    /// <summary>
    /// Vehicle types this is offered for. Leave empty for every vehicle — which
    /// is right for an AC regas and wrong for a wash, since washing a bus is not
    /// the job that washing a scooter is.
    /// </summary>
    public List<string> AppliesTo { get; set; } = [];

    public bool IsActive { get; set; } = true;

    /// <summary>False keeps it off the customer app; the shop can still add it.</summary>
    public bool IsBookable { get; set; } = true;
}

public class UpdateServiceRequest
{
    [StringLength(160, MinimumLength = 1)] public string? Name { get; set; }
    [StringLength(500)] public string? Description { get; set; }

    [AllowedValues(null, "Washing", "Detailing", "Maintenance", "Repair", "Inspection", "Convenience", "Other")]
    public string? Category { get; set; }

    [Range(0, 100_000_000)] public decimal? Price { get; set; }
    [Range(0, 10_000)] public int? DurationMinutes { get; set; }

    /// <summary>Send an empty list to clear the restriction; omit to leave it alone.</summary>
    public List<string>? AppliesTo { get; set; }

    public bool? IsActive { get; set; }
    public bool? IsBookable { get; set; }
}

// ── Invoices ─────────────────────────────────────────────────────────────────

public class CreateInvoiceRequest
{
    [Required, StringLength(20)] public string JobCardId { get; set; } = "";
    [Required, StringLength(20)] public string CustomerId { get; set; } = "";

    /// <summary>Ignored — the server snapshots the customer's current name.</summary>
    public string? CustomerName { get; set; }

    /// <summary>Ignored — the server snapshots the plate from the job card.</summary>
    public string? VehiclePlate { get; set; }

    public DateOnly IssuedAt { get; set; }

    [Range(0, 100_000_000)] public decimal Subtotal { get; set; }

    /// <summary>Fractional VAT rate, e.g. 0.13.</summary>
    [Range(0, 1)] public decimal TaxRate { get; set; }

    /// <summary>Amount settled at issue time; 0 for an open bill.</summary>
    [Range(0, 100_000_000)] public decimal Paid { get; set; }

    [AllowedValues(null, "Cash", "Card", "eSewa", "Khalti", "Bank Transfer")]
    public string? Method { get; set; }
}

public class UpdateInvoiceRequest
{
    public DateOnly? IssuedAt { get; set; }
    [Range(0, 100_000_000)] public decimal? Subtotal { get; set; }
    [Range(0, 1)] public decimal? TaxRate { get; set; }
    [Range(0, 100_000_000)] public decimal? Paid { get; set; }

    [AllowedValues(null, "Cash", "Card", "eSewa", "Khalti", "Bank Transfer")]
    public string? Method { get; set; }
}

public class RecordPaymentRequest
{
    [Range(0.01, 100_000_000)] public decimal Amount { get; set; }

    [AllowedValues("Cash", "Card", "eSewa", "Khalti", "Bank Transfer")]
    public string Method { get; set; } = "Cash";

    /// <summary>
    /// The bank slip number, the wallet's transaction id, whatever the customer
    /// showed at the counter. Optional, and the only thing that makes a manually
    /// recorded transfer reconcilable later.
    /// </summary>
    [StringLength(120)]
    public string? Reference { get; set; }
}

// ── Online payment ───────────────────────────────────────────────────────────

public class StartPaymentRequest
{
    [Required, StringLength(20)]
    public string InvoiceId { get; set; } = "";

    /// <summary>
    /// Which gateway. Only wallets appear here — cash, card and bank transfer
    /// are recorded by staff after the fact and have no online flow.
    /// </summary>
    [AllowedValues("eSewa", "Khalti")]
    public string Provider { get; set; } = "eSewa";
}

/// <summary>
/// Asks the server to confirm an attempt with the gateway.
/// </summary>
/// <remarks>
/// Used by the app when the customer returns from the wallet. Harmless to call
/// repeatedly — a settled payment answers the same way every time.
/// </remarks>
public class VerifyPaymentRequest
{
    [Required, StringLength(64)]
    public string Reference { get; set; } = "";

    /// <summary>Whatever the gateway handed back, if the client caught it.</summary>
    [StringLength(4000)]
    public string? Data { get; set; }
}

// ── Handover ─────────────────────────────────────────────────────────────────

public class ChooseDeliveryRequest
{
    [AllowedValues("Pickup", "HomeDelivery")]
    public string Method { get; set; } = "Pickup";
}

public class StartDeliveryRequest
{
    /// <summary>
    /// Who is driving. Staff may name anyone; a mechanic naming themselves is
    /// ignored in favour of their own account, so a driver cannot log a trip
    /// under somebody else's name.
    /// </summary>
    [StringLength(120)]
    public string? Driver { get; set; }
}

public class DeliveryPingRequest
{
    [Range(-90, 90)] public double Latitude { get; set; }
    [Range(-180, 180)] public double Longitude { get; set; }

    /// <summary>Metres of GPS uncertainty the phone reported. Optional.</summary>
    [Range(0, 100_000)] public double? AccuracyMetres { get; set; }
}

// ── Workshop ─────────────────────────────────────────────────────────────────

public class UpdateWorkshopRequest
{
    // Bank details, for a customer paying by transfer. Each is optional and
    // absent means "leave it alone", matching every other field here.
    [StringLength(120)] public string? BankName { get; set; }
    [StringLength(160)] public string? BankAccountName { get; set; }
    [StringLength(60)] public string? BankAccountNumber { get; set; }
    [StringLength(120)] public string? BankBranch { get; set; }

    [StringLength(160)] public string? Name { get; set; }
    [StringLength(200)] public string? LegalName { get; set; }
    [StringLength(300)] public string? Address { get; set; }
    [StringLength(40)] public string? Phone { get; set; }
    [EmailAddress, StringLength(160)] public string? Email { get; set; }
    [StringLength(40)] public string? TaxNumber { get; set; }
    [StringLength(200)] public string? OpeningHours { get; set; }
    [StringLength(500)] public string? InvoiceFooter { get; set; }

    /// <summary>Shown on the garage's card in the public directory.</summary>
    [StringLength(600)] public string? About { get; set; }

    /// <summary>
    /// Whether customers browsing the app can find and join this garage.
    /// Off by default — being listed is a choice the workshop makes.
    /// </summary>
    public bool? IsListed { get; set; }

    [Range(-90, 90, ErrorMessage = "Latitude must be between -90 and 90.")]
    public double? Latitude { get; set; }

    [Range(-180, 180, ErrorMessage = "Longitude must be between -180 and 180.")]
    public double? Longitude { get; set; }

    /// <summary>Send true to remove the pin — see UpdateCustomerRequest for why.</summary>
    public bool? ClearLocation { get; set; }

    // ── Home delivery pricing ────────────────────────────────────────────────

    public bool? DeliveryEnabled { get; set; }

    [Range(0, 100_000)] public decimal? DeliveryBaseFee { get; set; }
    [Range(0, 10_000)] public decimal? DeliveryPerKm { get; set; }

    /// <summary>Bills at or above this are delivered free. Zero disables the waiver.</summary>
    [Range(0, 100_000_000)] public decimal? DeliveryFreeAbove { get; set; }

    /// <summary>Furthest the shop will go. Zero means no limit.</summary>
    [Range(0, 500)] public double? DeliveryMaxKm { get; set; }
}

// ── Query parameters ─────────────────────────────────────────────────────────

/// <summary>
/// Paging, sorting and search parameters accepted by every list endpoint.
/// </summary>
/// <remarks>
/// Paging is <c>skip</c>/<c>take</c>. Omit <c>take</c> and the endpoint returns
/// every matching row — which is what the dashboard, reports and global search
/// need. <c>count</c> in the response is always the full total, so the client
/// never has to ask twice to size its pager.
///
/// <c>page</c>/<c>pageSize</c> are accepted as an alternative and converted to
/// skip/take; supplying both lets skip/take win.
/// </remarks>
public class TableQuery
{
    /// <summary>Rows to skip. Defaults to 0.</summary>
    [Range(0, int.MaxValue)] public int? Skip { get; set; }

    /// <summary>Rows to return. Omit for all matching rows.</summary>
    [Range(1, 1000)] public int? Take { get; set; }

    /// <summary>1-based page number — an alternative to <see cref="Skip"/>.</summary>
    [Range(1, int.MaxValue)] public int? Page { get; set; }

    /// <summary>Page size — an alternative to <see cref="Take"/>.</summary>
    [Range(1, 1000)] public int? PageSize { get; set; }

    /// <summary>Property name to sort by, camelCase or PascalCase.</summary>
    public string? SortBy { get; set; }

    [AllowedValues(null, "asc", "desc")]
    public string? SortDir { get; set; }

    /// <summary>Free-text filter; each endpoint documents the fields it covers.</summary>
    public string? Search { get; set; }

    public bool Descending => string.Equals(SortDir, "desc", StringComparison.OrdinalIgnoreCase);

    /// <summary>Effective rows to skip, whichever style the caller used.</summary>
    public int EffectiveSkip =>
        Skip ?? (Page is { } page && PageSize is { } size ? (page - 1) * size : 0);

    /// <summary>Effective page size, or null for "everything".</summary>
    public int? EffectiveTake => Take ?? PageSize;
}

/// <summary>A customer reporting that they have transferred the money.</summary>
public class DeclareBankTransferRequest
{
    [Required, StringLength(20)]
    public string InvoiceId { get; set; } = "";

    /// <summary>
    /// The bank's own reference, if the customer has it to hand. Optional —
    /// most people will not, and demanding it would stop them telling us at all.
    /// </summary>
    [StringLength(80)]
    public string? Reference { get; set; }
}
