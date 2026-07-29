namespace GarageFlow.Api.Domain;

/// <summary>
/// A customer claiming an account against a record the workshop already holds.
/// </summary>
/// <remarks>
/// There is deliberately no open sign-up. A person proves they are the customer
/// on file by receiving a code at the phone or email already recorded against
/// that customer — which means a stranger cannot create an account, and a real
/// customer's vehicles and history are there the moment they first sign in.
///
/// Only the hash of the code is stored, for the same reason as the password
/// reset token: the database must not be a place from which someone can read a
/// working code.
/// </remarks>
public class CustomerRegistration
{
    public int Id { get; set; }

    /// <summary>The customer record being claimed.</summary>
    public string CustomerId { get; set; } = default!;
    public Customer? Customer { get; set; }

    /// <summary>The tenant, so a code cannot be replayed against another workshop.</summary>
    public string CompanyCode { get; set; } = default!;

    /// <summary>
    /// The phone or email the code was sent to, normalised. Kept in the clear —
    /// it is already on the customer record, and matching it on the second leg
    /// is what stops a code being redeemed against a different contact.
    /// </summary>
    public string Contact { get; set; } = default!;

    /// <summary>SHA-256 of the six-digit code. Never the code itself.</summary>
    public string CodeHash { get; set; } = default!;

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Wrong codes tried. A six-digit code is a million guesses; without a cap
    /// that is an afternoon's work for a script.
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>Set the moment it is redeemed, so a code works exactly once.</summary>
    public DateTime? ConsumedAt { get; set; }
}

/// <summary>
/// Getting a finished vehicle back to its owner. Ids look like <c>DEL-001</c>.
/// </summary>
/// <remarks>
/// Created when a job reaches Completed, in <c>AwaitingChoice</c> — the customer
/// has been told the car is ready and has not yet said how they want it. It is a
/// separate record rather than fields on the job card because it has its own
/// life: its own status, its own fee, its own driver, and a trail of positions
/// that has nothing to do with the repair.
/// </remarks>
public class Delivery
{
    public string Id { get; set; } = default!;

    public string JobCardId { get; set; } = default!;
    public JobCard? JobCard { get; set; }

    public string CustomerId { get; set; } = default!;
    public Customer? Customer { get; set; }

    /// <summary>One of <see cref="Vocabulary.DeliveryMethods"/>.</summary>
    public string Method { get; set; } = "Pickup";

    /// <summary>One of <see cref="Vocabulary.DeliveryStatuses"/>.</summary>
    public string Status { get; set; } = "AwaitingChoice";

    // ── Where it is going ────────────────────────────────────────────────────

    /// <summary>
    /// Snapshotted from the customer when they chose, not read live. A customer
    /// who moves house next year must not change where last year's delivery went.
    /// </summary>
    public string Address { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>
    /// Straight-line distance from the workshop, in km, at the moment of quoting.
    /// </summary>
    /// <remarks>
    /// Great-circle, not road distance — road routing needs a paid routing API,
    /// and for a fee that is a rounding error either way it is not worth the
    /// dependency. It under-reads against real driving distance, which errs in
    /// the customer's favour.
    /// </remarks>
    public double? DistanceKm { get; set; }

    /// <summary>What was charged. Zero for a pickup, or for a delivery waived by the threshold.</summary>
    public decimal Fee { get; set; }

    // ── Who is taking it ─────────────────────────────────────────────────────

    /// <summary>Free text, matching <see cref="JobCard.Mechanic"/> — see <see cref="User.MechanicName"/>.</summary>
    public string Driver { get; set; } = "";

    /// <summary>Last position reported by the driver, and when.</summary>
    public double? DriverLatitude { get; set; }
    public double? DriverLongitude { get; set; }
    public DateTime? DriverAt { get; set; }

    public List<DeliveryPoint> Trail { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    /// <summary>When the customer said how they wanted it.</summary>
    public DateTime? ChosenAt { get; set; }

    /// <summary>When the driver set off.</summary>
    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>True while a driver is on the road with it.</summary>
    public bool IsLive => Status == "OutForDelivery";

    /// <summary>True when there is a position worth putting on a map.</summary>
    public bool HasDriverPosition => DriverLatitude is not null && DriverLongitude is not null;
}

/// <summary>
/// One position reported by a driver on the way.
/// </summary>
/// <remarks>
/// Kept as a trail rather than only a latest position so the dashboard can draw
/// the route taken, and so "where was the car at 3pm" has an answer. Pruned when
/// the delivery closes — the shape of the trip is worth keeping, every 15-second
/// sample of it is not.
/// </remarks>
public class DeliveryPoint
{
    public int Id { get; set; }

    public string DeliveryId { get; set; } = default!;
    public Delivery? Delivery { get; set; }

    public double Latitude { get; set; }
    public double Longitude { get; set; }

    /// <summary>Metres of GPS uncertainty the phone reported, when it did.</summary>
    public double? AccuracyMetres { get; set; }

    public DateTime At { get; set; }
}
