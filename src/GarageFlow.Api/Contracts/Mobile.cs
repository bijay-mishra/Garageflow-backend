using System.ComponentModel.DataAnnotations;

namespace GarageFlow.Api.Contracts;

// ── Mobile DTOs ──────────────────────────────────────────────────────────────
// The shapes the Flutter app binds to. Kept apart from the dashboard's
// contracts because the two clients want different slices: a mechanic needs the
// vehicle and the complaint, never the money; a customer needs the status and
// the total, never the mechanic's name.

// ── Job photos ───────────────────────────────────────────────────────────────

public record JobPhotoDto
{
    public required int Id { get; init; }
    public required string JobCardId { get; init; }

    /// <summary>Absolute URL, ready for the app's image widget.</summary>
    public required string Url { get; init; }

    public required string FileName { get; init; }
    public required long SizeBytes { get; init; }

    /// <summary>before, during, after, damage or part.</summary>
    public required string Kind { get; init; }

    public required string Caption { get; init; }
    public required string UploadedBy { get; init; }
    public required DateTime UploadedAt { get; init; }
}

public class UploadJobPhotoRequest
{
    [AllowedValues("before", "during", "after", "damage", "part")]
    public string Kind { get; set; } = "during";

    [StringLength(300)]
    public string Caption { get; set; } = "";
}

// ── Mechanic app ─────────────────────────────────────────────────────────────

/// <summary>
/// A job as the mechanic app lists it. Money is deliberately absent — a
/// mechanic is told what to do, not what it bills for.
/// </summary>
public record MechanicJobDto
{
    public required string Id { get; init; }
    public required string VehicleId { get; init; }
    public required string VehiclePlate { get; init; }
    public required string VehicleLabel { get; init; }

    /// <summary>Body class — lets the app lead with the services that suit it.</summary>
    public required string VehicleType { get; init; }

    public required string CustomerName { get; init; }

    /// <summary>So the mechanic can ring the owner about an unexpected repair.</summary>
    public required string CustomerPhone { get; init; }

    /// <summary>Written address — what you read out on the phone.</summary>
    public required string CustomerAddress { get; init; }

    /// <summary>
    /// The customer's map pin, or null if nobody set one. Present for pickup
    /// and drop: "Baneshwor, near the temple" is a real address and a useless
    /// destination.
    /// </summary>
    public required double? CustomerLatitude { get; init; }
    public required double? CustomerLongitude { get; init; }

    public required string Complaint { get; init; }
    public required string Status { get; init; }
    public required string Priority { get; init; }
    public required int Odometer { get; init; }
    public required DateOnly CreatedAt { get; init; }
    public required DateOnly PromisedAt { get; init; }
    public required DateOnly? CompletedAt { get; init; }

    /// <summary>True once <c>PromisedAt</c> is behind us and the work is not done.</summary>
    public required bool IsOverdue { get; init; }

    public required IReadOnlyList<JobLineDto> Lines { get; init; }
    public required int PhotoCount { get; init; }
}

public class UpdateJobStatusRequest
{
    [AllowedValues("Open", "In Progress", "Awaiting Parts", "Completed", "Delivered", "Cancelled")]
    public string Status { get; set; } = "In Progress";

    /// <summary>
    /// Reading at the moment the status changed. Optional — omit and the job's
    /// existing odometer stands.
    /// </summary>
    [Range(0, 10_000_000)]
    public int? Odometer { get; set; }

    /// <summary>Appended to the job's complaint as a work note.</summary>
    [StringLength(500)]
    public string? Note { get; set; }
}

/// <summary>The tiles at the top of the mechanic's job list.</summary>
public record MechanicSummaryDto(
    int AssignedTotal,
    int InProgress,
    int AwaitingParts,
    int CompletedToday,
    int Overdue);

// ── Customer app ─────────────────────────────────────────────────────────────

/// <summary>An extra the customer ticked when booking, at the price they saw.</summary>
public record BookedServiceDto
{
    public required string ServiceId { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }

    /// <summary>What it was quoted at when the booking was made, not today's price.</summary>
    public required decimal Price { get; init; }
}

public record BookingDto
{
    public required string Id { get; init; }
    public required string CustomerId { get; init; }
    public required string CustomerName { get; init; }
    public required string VehicleId { get; init; }
    public required string VehiclePlate { get; init; }
    public required string VehicleLabel { get; init; }
    public required string Complaint { get; init; }
    public required DateOnly PreferredDate { get; init; }
    public required string PreferredTime { get; init; }

    /// <summary>Extras asked for on top of the complaint — a wash, a polish, a pickup.</summary>
    public required IReadOnlyList<BookedServiceDto> Services { get; init; }

    /// <summary>
    /// Sum of the quoted extras. The complaint itself is deliberately unpriced:
    /// nobody can quote "a knocking noise" before they have looked at it.
    /// </summary>
    public required decimal EstimatedTotal { get; init; }

    /// <summary>Requested, Confirmed, Rejected, Converted or Cancelled.</summary>
    public required string Status { get; init; }

    public required string? StaffNote { get; init; }

    /// <summary>Set once the workshop turned this into real work.</summary>
    public required string? JobCardId { get; init; }

    public required DateTime CreatedAt { get; init; }
    public required DateTime? RespondedAt { get; init; }
}

public class CreateBookingRequest
{
    [Required, StringLength(20)]
    public string VehicleId { get; set; } = "";

    [Required, StringLength(1000, MinimumLength = 1)]
    public string Complaint { get; set; } = "";

    public DateOnly PreferredDate { get; set; }

    [StringLength(60)]
    public string PreferredTime { get; set; } = "";

    /// <summary>
    /// Optional extras from the price list — "and give it a wash while it is in".
    /// Each is checked against the vehicle's type and the shop's bookable flag,
    /// then quoted at today's price and held there.
    /// </summary>
    [MaxLength(20, ErrorMessage = "That is more extras than one visit can hold.")]
    public List<string> ServiceIds { get; set; } = [];
}

/// <summary>Staff answering a booking request.</summary>
public class RespondToBookingRequest
{
    [AllowedValues("Confirmed", "Rejected")]
    public string Status { get; set; } = "Confirmed";

    [StringLength(500)]
    public string? StaffNote { get; set; }
}

/// <summary>
/// A job as the customer app shows it: what is happening to their car and what
/// it will cost, with no mention of who is holding the spanner.
/// </summary>
public record CustomerJobDto
{
    public required string Id { get; init; }
    public required string VehicleId { get; init; }
    public required string VehiclePlate { get; init; }
    public required string VehicleLabel { get; init; }
    public required string Complaint { get; init; }
    public required string Status { get; init; }
    public required DateOnly CreatedAt { get; init; }
    public required DateOnly PromisedAt { get; init; }
    public required DateOnly? CompletedAt { get; init; }
    public required decimal Total { get; init; }
    public required IReadOnlyList<JobLineDto> Lines { get; init; }
    public required IReadOnlyList<JobPhotoDto> Photos { get; init; }

    /// <summary>
    /// 0–100, derived from the status. Gives the app a progress bar without
    /// each client inventing its own mapping.
    /// </summary>
    public required int ProgressPct { get; init; }
}

// ── Notifications ────────────────────────────────────────────────────────────

public record NotificationDto
{
    public required int Id { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }

    /// <summary>job, booking, invoice or system — drives the icon.</summary>
    public required string Kind { get; init; }

    /// <summary>Id of the job, booking or invoice this is about, for deep links.</summary>
    public required string? EntityId { get; init; }

    public required DateTime CreatedAt { get; init; }
    public required DateTime? ReadAt { get; init; }
    public required bool IsRead { get; init; }
}

/// <summary>The feed plus the badge count, so the app needs one call.</summary>
public record NotificationFeedDto(int UnreadCount, IReadOnlyList<NotificationDto> Items);

// ── Account management (dashboard-side) ──────────────────────────────────────

/// <summary>A user account as the dashboard's Users screen lists it.</summary>
public record UserDto
{
    public required string Id { get; init; }
    public required string Email { get; init; }
    public required string Name { get; init; }
    public required string Role { get; init; }
    public required string? Phone { get; init; }
    public required bool IsActive { get; init; }

    /// <summary>Set for Mechanic accounts — the name they are assigned under.</summary>
    public required string? MechanicName { get; init; }

    /// <summary>Set for Customer accounts — the customer they speak for.</summary>
    public required string? CustomerId { get; init; }
    public required string? CustomerName { get; init; }

    public required DateTime CreatedAt { get; init; }
    public required DateTime? LastLoginAt { get; init; }
}

public class CreateUserRequest
{
    [Required, EmailAddress, StringLength(160)]
    public string Email { get; set; } = "";

    [Required, StringLength(160, MinimumLength = 1)]
    public string Name { get; set; } = "";

    [Required, StringLength(200, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters.")]
    public string Password { get; set; } = "";

    [AllowedValues("Owner", "Manager", "Advisor", "Mechanic", "Customer")]
    public string Role { get; set; } = "Mechanic";

    [StringLength(40)]
    public string? Phone { get; set; }

    /// <summary>Required when <see cref="Role"/> is Mechanic.</summary>
    [StringLength(120)]
    public string? MechanicName { get; set; }

    /// <summary>Required when <see cref="Role"/> is Customer.</summary>
    [StringLength(20)]
    public string? CustomerId { get; set; }
}

public class UpdateUserRequest
{
    [StringLength(160, MinimumLength = 1)] public string? Name { get; set; }
    [StringLength(40)] public string? Phone { get; set; }

    [AllowedValues(null, "Owner", "Manager", "Advisor", "Mechanic", "Customer")]
    public string? Role { get; set; }

    [StringLength(120)] public string? MechanicName { get; set; }
    [StringLength(20)] public string? CustomerId { get; set; }
    public bool? IsActive { get; set; }

    /// <summary>Supply to reset the password without the current one — staff action.</summary>
    [StringLength(200, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters.")]
    public string? Password { get; set; }
}
