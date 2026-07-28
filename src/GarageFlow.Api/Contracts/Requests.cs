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
}

public class UpdateCustomerRequest
{
    [StringLength(160, MinimumLength = 1)] public string? Name { get; set; }
    [StringLength(40)] public string? Phone { get; set; }
    [EmailAddress, StringLength(160)] public string? Email { get; set; }
    [StringLength(300)] public string? Address { get; set; }
    [StringLength(40)] public string? AvatarColor { get; set; }
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

    [AllowedValues("labour", "part")]
    public string Kind { get; set; } = "part";
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
