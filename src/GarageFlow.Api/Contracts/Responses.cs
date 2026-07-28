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

/// <summary>A labour or parts line on a job card.</summary>
public record JobLineDto
{
    public required string Description { get; init; }

    /// <summary>Hours for labour, units for parts.</summary>
    public required decimal Qty { get; init; }
    public required decimal UnitPrice { get; init; }

    /// <summary>Either <c>labour</c> or <c>part</c>.</summary>
    public required string Kind { get; init; }
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
    public required DateTime At { get; init; }
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
