namespace GarageFlow.Api.Domain;

/// <summary>A workshop customer. Ids look like <c>CUS-001</c>.</summary>
public class Customer
{
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Address { get; set; } = "";
    public DateOnly CreatedAt { get; set; }

    /// <summary>Tailwind class used for the list avatar, e.g. <c>bg-brand-500</c>.</summary>
    public string AvatarColor { get; set; } = "bg-brand-500";

    public List<Vehicle> Vehicles { get; set; } = [];
    public List<Invoice> Invoices { get; set; } = [];
}

/// <summary>A vehicle belonging to a customer. Ids look like <c>VEH-001</c>.</summary>
public class Vehicle
{
    public string Id { get; set; } = default!;
    public string CustomerId { get; set; } = default!;
    public Customer? Customer { get; set; }

    public string Make { get; set; } = "";
    public string Model { get; set; } = "";
    public int Year { get; set; }
    public string Plate { get; set; } = "";
    public string Vin { get; set; } = "";

    /// <summary>One of <see cref="Vocabulary.FuelTypes"/>.</summary>
    public string Fuel { get; set; } = "Petrol";

    /// <summary>Last recorded odometer reading, in km.</summary>
    public int Odometer { get; set; }
    public string Color { get; set; } = "";

    public List<JobCard> JobCards { get; set; } = [];

    /// <summary>Human label used across the UI, e.g. "Toyota Corolla 2019".</summary>
    public string Label => $"{Make} {Model} {Year}".Trim();
}

/// <summary>A repair order. Ids look like <c>JOB-1042</c>.</summary>
public class JobCard
{
    public string Id { get; set; } = default!;
    public string VehicleId { get; set; } = default!;
    public Vehicle? Vehicle { get; set; }

    public string Complaint { get; set; } = "";

    /// <summary>One of <see cref="Vocabulary.JobStatuses"/>.</summary>
    public string Status { get; set; } = "Open";

    /// <summary>One of <see cref="Vocabulary.JobPriorities"/>.</summary>
    public string Priority { get; set; } = "Normal";

    public string Mechanic { get; set; } = "";
    public int Odometer { get; set; }

    public DateOnly CreatedAt { get; set; }
    public DateOnly PromisedAt { get; set; }

    /// <summary>Stamped automatically when the job moves to Completed or Delivered.</summary>
    public DateOnly? CompletedAt { get; set; }

    public List<JobLine> Lines { get; set; } = [];

    /// <summary>Sum of every line (qty × unit price). Never stored — always derived.</summary>
    public decimal Total => Lines.Sum(l => l.Qty * l.UnitPrice);
}

/// <summary>A single labour or parts line on a job card.</summary>
public class JobLine
{
    public int Id { get; set; }
    public string JobCardId { get; set; } = default!;
    public JobCard? JobCard { get; set; }

    public string Description { get; set; } = "";

    /// <summary>Quantity — hours for labour, units for parts.</summary>
    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }

    /// <summary>One of <see cref="Vocabulary.JobLineKinds"/>.</summary>
    public string Kind { get; set; } = "part";

    /// <summary>Preserves the order the lines were entered in.</summary>
    public int SortOrder { get; set; }
}

/// <summary>A bill raised against a job card. Ids look like <c>INV-2091</c>.</summary>
public class Invoice
{
    public string Id { get; set; } = default!;
    public string JobCardId { get; set; } = default!;
    public string CustomerId { get; set; } = default!;
    public Customer? Customer { get; set; }

    // Snapshots: an invoice is a financial record, so it keeps the name and plate
    // as they were at issue time rather than following later edits.
    public string CustomerName { get; set; } = "";
    public string VehiclePlate { get; set; } = "";

    public DateOnly IssuedAt { get; set; }
    public decimal Subtotal { get; set; }

    /// <summary>Fractional rate, e.g. 0.13 for 13% VAT.</summary>
    public decimal TaxRate { get; set; }

    public decimal Paid { get; set; }

    /// <summary>Method of the most recent payment; null until something is paid.</summary>
    public string? Method { get; set; }

    public List<Payment> Payments { get; set; } = [];

    public decimal Tax => Math.Round(Subtotal * TaxRate, 2, MidpointRounding.AwayFromZero);
    public decimal Total => Subtotal + Tax;

    /// <summary>Derived from how much of <see cref="Total"/> has been settled.</summary>
    public string Status => Paid <= 0 ? "Unpaid" : Paid >= Total ? "Paid" : "Partial";
}

/// <summary>One receipt against an invoice — the audit trail behind <c>Invoice.Paid</c>.</summary>
public class Payment
{
    public int Id { get; set; }
    public string InvoiceId { get; set; } = default!;
    public Invoice? Invoice { get; set; }

    public decimal Amount { get; set; }

    /// <summary>One of <see cref="Vocabulary.PaymentMethods"/>.</summary>
    public string Method { get; set; } = "Cash";
    public DateTime At { get; set; }
}

/// <summary>An entry in the dashboard's recent-activity feed.</summary>
public class Activity
{
    public string Id { get; set; } = default!;
    public DateTime At { get; set; }
    public string Text { get; set; } = "";

    /// <summary>One of <see cref="Vocabulary.ActivityKinds"/>.</summary>
    public string Kind { get; set; } = "job";
}
