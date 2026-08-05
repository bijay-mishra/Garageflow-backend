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

    /// <summary>
    /// Profile photo, stored as a path relative to wwwroot. Null when they have
    /// not set one — the app draws their initials instead, which is why there is
    /// no default image to fall back to.
    /// </summary>
    public string? PhotoPath { get; set; }

    /// <summary>PBKDF2 hash from <see cref="Microsoft.AspNetCore.Identity.PasswordHasher{T}"/>. Never a plaintext password.</summary>
    public string PasswordHash { get; set; } = default!;

    /// <summary>
    /// True while the password on this account is one somebody else chose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set whenever a password is typed by anyone other than the account holder:
    /// an operator creating a company, an owner adding staff, a manager
    /// resetting a forgotten one. Until it is cleared the account can sign in
    /// and do exactly one thing — choose its own password.
    /// </para>
    /// <para>
    /// The point is that the person who handed the credentials over stops
    /// knowing them the moment they are used. Without this, every account a
    /// superadmin ever created is an account a superadmin can still sign into,
    /// and no amount of care about the rest of the system makes up for that.
    /// </para>
    /// <para>
    /// Enforced on the token rather than in the client — see
    /// <c>MustSetPasswordFilter</c>. A screen the user can navigate away from is
    /// not a control.
    /// </para>
    /// </remarks>
    public bool MustSetPassword { get; set; }

    /// <summary>
    /// One of <see cref="Vocabulary.UserRoles"/>. What the server authorises as.
    /// </summary>
    /// <remarks>
    /// This is the role claim in the token and the name every
    /// <c>[Authorize(Roles = …)]</c> checks. It stays one of the product's own
    /// roles even when the workshop calls this person something else — see
    /// <see cref="CompanyRoleName"/>.
    /// </remarks>
    public string Role { get; set; } = "Owner";

    /// <summary>
    /// The company's own name for this role — "CEO", "Front desk" — or null to
    /// use <see cref="Role"/> as-is.
    /// </summary>
    /// <remarks>
    /// Decides which menu this person gets and what the staff list calls them.
    /// Deliberately separate from <see cref="Role"/>: one is a label a company
    /// can invent on a Tuesday, the other is a permission the server enforces,
    /// and collapsing them would make renaming a job title a security change.
    /// See <see cref="CompanyRole"/>.
    /// </remarks>
    public string? CompanyRoleName { get; set; }

    /// <summary>Workshop shown in the topbar.</summary>
    public string Workshop { get; set; } = "";

    // ── Workspace cursor ─────────────────────────────────────────────────────
    // Which branch and accounting year this session is looking at. Cursors, in
    // the same sense as CustomerId below: the token carries them, every request
    // is answered against them, and changing one mints a new token rather than
    // being passed as a query parameter the client could forge or forget.
    //
    // Stored on the user so the choice survives signing out — coming back to
    // last year's books because that is where you were is right; coming back to
    // a colleague's branch because it happened to be the default is not.

    /// <summary>
    /// The branch this session is scoped to, or null to use the default.
    /// </summary>
    /// <remarks>
    /// Nothing is partitioned by branch yet — see <see cref="Branch"/> for why
    /// that is deliberate. This is the selection, honoured by the token and the
    /// session, ready for the day the data follows.
    /// </remarks>
    public string? BranchId { get; set; }

    /// <summary>
    /// The accounting year in view, e.g. <c>2082/83</c>. Null means the current
    /// one, resolved per request rather than written down — otherwise every
    /// account would be pinned to whichever year it was created in.
    /// </summary>
    public string? FiscalYear { get; set; }

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

    /// <summary>
    /// For a <c>Customer</c>: the customer record this login currently speaks
    /// for — the garage they last selected.
    /// </summary>
    /// <remarks>
    /// Still a single value, and still what every scoped endpoint reads, but it
    /// is now a *cursor* rather than the whole truth. The full set of garages a
    /// person belongs to lives in <see cref="WorkshopLinks"/>; selecting one
    /// moves this and issues a fresh token. Keeping the cursor here is what let
    /// the marketplace arrive without rewriting a single customer endpoint.
    /// </remarks>
    public string? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>
    /// Every garage this account is a customer of. Empty for staff.
    /// </summary>
    /// <remarks>
    /// A customer signs up belonging to nobody, browses the directory, and joins
    /// as many garages as they like. Each link carries its own customer record,
    /// so the two garages never see each other's data.
    /// </remarks>
    public List<UserWorkshopLink> WorkshopLinks { get; set; } = [];

    /// <summary>A disabled user keeps their history but cannot sign in.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether this person wants their phone to buzz.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gates <em>delivery</em>, not the record. The in-app notification list is
    /// this account's history of what happened to its jobs and bills, and
    /// silencing your phone should not erase that — so the rows are still
    /// written and the feed still fills; only the push is suppressed.
    /// </para>
    /// <para>
    /// Stored on the server rather than in the phone's own preferences because
    /// the decision has to be honoured at <em>send</em> time. A purely local
    /// switch still lets the server push to a device that has been told to stay
    /// quiet, and relies on the app being installed to keep the promise.
    /// </para>
    /// </remarks>
    public bool NotificationsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    // ── Password reset ───────────────────────────────────────────────────────
    // A six-digit code, emailed. Only its hash is stored: the database is not a
    // place from which someone can read a working reset code.
    //
    // Six digits is a million guesses, which is minutes for a script — so unlike
    // the 256-bit values elsewhere in this file, this one is never looked up on
    // its own. The account is found by company code and email first and the code
    // compared against that single row, which turns a million guesses into a
    // million guesses *per account you already know about*. The attempt counter
    // below then burns it after five.

    public string? PasswordResetCodeHash { get; set; }
    public DateTime? PasswordResetExpiresAt { get; set; }

    /// <summary>Wrong guesses against the live code. The fifth burns it.</summary>
    public int PasswordResetAttempts { get; set; }

    /// <summary>
    /// When the last code went out.
    /// </summary>
    /// <remarks>
    /// Throttles resends. Without it, an endpoint that needs no credentials and
    /// sends mail to any address on file is a way to bury somebody's inbox using
    /// nothing but their email address.
    /// </remarks>
    public DateTime? PasswordResetSentAt { get; set; }

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
