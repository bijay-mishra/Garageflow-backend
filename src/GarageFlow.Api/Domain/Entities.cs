namespace GarageFlow.Api.Domain;

/// <summary>A workshop customer. Ids look like <c>CUS-001</c>.</summary>
public class Customer
{
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Address { get; set; } = "";

    /// <summary>
    /// Where the customer is, as dropped on a map. Null until somebody places a
    /// pin — which is most customers, and has to stay fine.
    /// </summary>
    /// <remarks>
    /// Kept alongside <see cref="Address"/> rather than replacing it. The two
    /// answer different questions: the address is what you write on an invoice
    /// and read out on the phone, the pin is what a pickup-and-drop driver
    /// actually navigates to. In Kathmandu especially, "Baneshwor, near the
    /// temple" is a real address and a useless destination.
    ///
    /// Stored as plain doubles, not a spatial type. Nothing here asks a
    /// geographic question — no "customers within 5km" — and a
    /// <c>geography</c> column would tie the schema to SQL Server for a feature
    /// that is two numbers and a marker.
    /// </remarks>
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    public DateOnly CreatedAt { get; set; }

    /// <summary>Tailwind class used for the list avatar, e.g. <c>bg-brand-500</c>.</summary>
    public string AvatarColor { get; set; } = "bg-brand-500";

    /// <summary>True when there is a pin to show on a map.</summary>
    public bool HasLocation => Latitude is not null && Longitude is not null;

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

    /// <summary>One of <see cref="Vocabulary.VehicleTypes"/>.</summary>
    public string Type { get; set; } = "Car";

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

    /// <summary>Photos the mechanic attached from the mobile app.</summary>
    public List<JobPhoto> Photos { get; set; } = [];

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

    /// <summary>
    /// The catalogue entry this line came from, when it came from one. Null for
    /// anything typed in by hand, which is most parts and labour.
    /// </summary>
    /// <remarks>
    /// A link, not a lookup. <see cref="Description"/> and
    /// <see cref="UnitPrice"/> are copied at the moment the line is added and
    /// never re-read, so re-pricing a wash tomorrow leaves today's job alone.
    /// The id is kept only so the shop can ask what a given service has earned.
    /// </remarks>
    public string? ServiceId { get; set; }
    public Service? Service { get; set; }

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
/// <remarks>
/// A row here used to mean "money arrived". With online payment it can also mean
/// "money was asked for and we are waiting" — see <see cref="Status"/>. Only a
/// Completed payment counts towards <c>Invoice.Paid</c>, which is what stops a
/// customer who opened the eSewa page and walked away from appearing to have
/// settled their bill.
/// </remarks>
public class Payment
{
    public int Id { get; set; }
    public string InvoiceId { get; set; } = default!;
    public Invoice? Invoice { get; set; }

    public decimal Amount { get; set; }

    /// <summary>One of <see cref="Vocabulary.PaymentMethods"/>.</summary>
    public string Method { get; set; } = "Cash";

    /// <summary>
    /// How the money moved — one of <see cref="Vocabulary.PaymentChannels"/>.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="Method"/> today, but stored rather than computed
    /// on read. The question the shop actually asks at the end of the month is
    /// "how much came in as cash?", and a method list that grows a new wallet
    /// every year should not silently change the answer for last year.
    /// </remarks>
    public string Channel { get; set; } = "cash";

    /// <summary>
    /// One of <see cref="Vocabulary.PaymentStatuses"/>. Manual payments are
    /// Completed the moment they are recorded; a gateway payment starts Pending.
    /// </summary>
    public string Status { get; set; } = "Completed";

    /// <summary>
    /// Our own reference, sent to the gateway so its record and ours can be
    /// matched later. Unique per attempt, not per invoice — a customer whose
    /// first attempt failed gets a fresh one.
    /// </summary>
    public string? Reference { get; set; }

    /// <summary>
    /// The gateway's own identifier for the transaction, kept for reconciliation
    /// and for arguing with their support desk. Null until it confirms.
    /// </summary>
    public string? ProviderRef { get; set; }

    /// <summary>Why a gateway payment failed, shown to whoever tried it.</summary>
    public string? FailureReason { get; set; }

    /// <summary>When the attempt started. Equal to <see cref="At"/> for cash.</summary>
    public DateTime InitiatedAt { get; set; }

    /// <summary>
    /// When the money was confirmed. For a Pending payment this is the moment it
    /// was started, and it only means anything once the status moves.
    /// </summary>
    public DateTime At { get; set; }

    /// <summary>Only a completed payment is money.</summary>
    public bool IsSettled => Status == "Completed";
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
