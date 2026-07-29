namespace GarageFlow.Api.Domain;

// ── Entities the mobile apps add ─────────────────────────────────────────────
// The dashboard never needed these: a mechanic photographing a job, a customer
// requesting one, and the feed that tells either of them something changed.

/// <summary>
/// A photo a mechanic took against a job card.
/// </summary>
/// <remarks>
/// Only the relative path is stored. The bytes live under
/// <c>wwwroot/uploads/{jobCardId}/</c> and are served as static files, so the
/// database stays small and a photo costs one file read rather than a query.
/// Moving to blob storage later means changing <see cref="Path"/> to a URL and
/// nothing else.
/// </remarks>
public class JobPhoto
{
    public int Id { get; set; }

    public string JobCardId { get; set; } = default!;
    public JobCard? JobCard { get; set; }

    /// <summary>Path relative to wwwroot, e.g. <c>uploads/JOB-1042/a1b2.jpg</c>.</summary>
    public string Path { get; set; } = default!;

    /// <summary>The name the file arrived with — shown when a download is offered.</summary>
    public string FileName { get; set; } = "";

    public long SizeBytes { get; set; }
    public string ContentType { get; set; } = "";

    /// <summary>One of <see cref="Vocabulary.JobPhotoKinds"/>.</summary>
    public string Kind { get; set; } = "during";

    /// <summary>Optional note the mechanic typed with the photo.</summary>
    public string Caption { get; set; } = "";

    /// <summary>Name of the mechanic who uploaded it — free text, as on the job card.</summary>
    public string UploadedBy { get; set; } = "";

    public DateTime UploadedAt { get; set; }
}

/// <summary>
/// A service request raised by a customer from the app. Ids look like
/// <c>BKG-001</c>.
/// </summary>
/// <remarks>
/// Deliberately not a job card. A booking is a customer's *ask* — it carries no
/// mechanic, no parts and no money, and the workshop may well decline it. It
/// becomes a job card only when someone accepts it, at which point
/// <see cref="JobCardId"/> is filled in and the two are linked for good.
/// </remarks>
public class Booking
{
    public string Id { get; set; } = default!;

    public string CustomerId { get; set; } = default!;
    public Customer? Customer { get; set; }

    public string VehicleId { get; set; } = default!;
    public Vehicle? Vehicle { get; set; }

    /// <summary>What the customer says is wrong.</summary>
    public string Complaint { get; set; } = "";

    /// <summary>The day the customer would like to bring the vehicle in.</summary>
    public DateOnly PreferredDate { get; set; }

    /// <summary>Free text — "morning", "after 4pm". Not a real time slot yet.</summary>
    public string PreferredTime { get; set; } = "";

    /// <summary>One of <see cref="Vocabulary.BookingStatuses"/>.</summary>
    public string Status { get; set; } = "Requested";

    /// <summary>Why the workshop rejected it, shown to the customer.</summary>
    public string? StaffNote { get; set; }

    /// <summary>Set once the booking has been turned into a real job card.</summary>
    public string? JobCardId { get; set; }
    public JobCard? JobCard { get; set; }

    /// <summary>
    /// Extras the customer ticked when booking — a wash, a polish, a pickup.
    /// Each becomes a priced line on the job card when the booking is converted.
    /// </summary>
    public List<BookingService> Services { get; set; } = [];

    public DateTime CreatedAt { get; set; }
    public DateTime? RespondedAt { get; set; }

    /// <summary>What the extras were quoted at. The complaint itself is unpriced.</summary>
    public decimal EstimatedTotal => Services.Sum(s => s.QuotedPrice);
}

/// <summary>
/// One entry in a user's in-app notification feed.
/// </summary>
/// <remarks>
/// Rows are per-user rather than broadcast: the feed is small, and a read flag
/// on a shared row would have to be a join table anyway. Written by
/// <c>NotificationService</c> whenever something happens that the owner of the
/// row would want to know about.
/// </remarks>
public class Notification
{
    public int Id { get; set; }

    /// <summary>Recipient. Always a real user — nothing is broadcast.</summary>
    public string UserId { get; set; } = default!;
    public User? User { get; set; }

    public string Title { get; set; } = "";
    public string Body { get; set; } = "";

    /// <summary>One of <see cref="Vocabulary.NotificationKinds"/>. Drives the icon.</summary>
    public string Kind { get; set; } = "system";

    /// <summary>
    /// Id of the thing this is about — a job card, booking or invoice — so the
    /// app can deep-link straight to it. Null for general messages.
    /// </summary>
    public string? EntityId { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Null until the user opens it.</summary>
    public DateTime? ReadAt { get; set; }
}
