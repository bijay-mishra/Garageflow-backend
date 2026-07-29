namespace GarageFlow.Api.Domain;

/// <summary>A person who can sign in. Ids look like <c>USR-001</c>.</summary>
public class User
{
    public string Id { get; set; } = default!;

    /// <summary>
    /// Tenant the account belongs to. Part of the sign-in credentials, so the
    /// same email can exist under two different workshops.
    /// </summary>
    public string CompanyCode { get; set; } = default!;

    /// <summary>Doubles as the username — sign-in is company code + email + password.</summary>
    public string Email { get; set; } = default!;

    public string FullName { get; set; } = "";
    public string? Phone { get; set; }

    /// <summary>PBKDF2 hash from <see cref="Microsoft.AspNetCore.Identity.PasswordHasher{T}"/>. Never a plaintext password.</summary>
    public string PasswordHash { get; set; } = default!;

    /// <summary>One of <see cref="Vocabulary.UserRoles"/>.</summary>
    public string Role { get; set; } = "Owner";

    /// <summary>Workshop shown in the topbar.</summary>
    public string Workshop { get; set; } = "";

    // ── Mobile app links ─────────────────────────────────────────────────────
    // Staff accounts leave both of these null. The two mobile roles each need a
    // way back to the data they own, and the two links are deliberately
    // separate rather than one polymorphic column: a mechanic is identified by
    // the name written on job cards, a customer by a real foreign key.

    /// <summary>
    /// For a <c>Mechanic</c>: the name this person is assigned under on job
    /// cards, matching <see cref="JobCard.Mechanic"/>.
    /// </summary>
    /// <remarks>
    /// A name rather than a foreign key because job cards have always recorded
    /// their mechanic as free text, and existing rows have to keep working.
    /// Assignment stays a plain string on the job; this column is what lets an
    /// account claim those rows.
    /// </remarks>
    public string? MechanicName { get; set; }

    /// <summary>For a <c>Customer</c>: the customer record this login speaks for.</summary>
    public string? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>A disabled user keeps their history but cannot sign in.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    // ── Password reset ───────────────────────────────────────────────────────
    // Only the hash of the reset token is stored: the database is not a place
    // from which someone can mint a working reset link.

    public string? PasswordResetTokenHash { get; set; }
    public DateTime? PasswordResetExpiresAt { get; set; }

    public List<RefreshToken> RefreshTokens { get; set; } = [];
}

/// <summary>
/// A long-lived token that buys new access tokens.
/// </summary>
/// <remarks>
/// Access tokens are deliberately short-lived and cannot be revoked once
/// issued — this row is what makes logout mean something. Stored as a hash for
/// the same reason as the reset token, and rotated on every use so a stolen
/// token stops working as soon as the real client refreshes.
/// </remarks>
public class RefreshToken
{
    public int Id { get; set; }
    public string UserId { get; set; } = default!;
    public User? User { get; set; }

    public string TokenHash { get; set; } = default!;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Set when the token is used (rotation) or the user signs out.</summary>
    public DateTime? RevokedAt { get; set; }

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTime.UtcNow;
}
