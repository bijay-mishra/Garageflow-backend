using System.ComponentModel.DataAnnotations;

// ── Customer self-registration ───────────────────────────────────────────────
// Two legs. The first proves the person is a customer the workshop already
// holds, by sending a code to the contact already on that record; the second
// redeems it. There is no open sign-up: a stranger cannot create an account, and
// a real customer's vehicles and history are there on first sign-in.

namespace GarageFlow.Api.Contracts;

/// <summary>
/// Open sign-up, for customers only.
/// </summary>
/// <remarks>
/// No company code, because a person downloading the app has never heard of one.
/// The account starts belonging to no garage at all; they browse the directory
/// and join whichever they like, as many as they like.
///
/// Staff accounts are still created by a workshop on the dashboard. There is no
/// path here to a Mechanic, Advisor, Manager or Owner role, and that is the
/// point: a stranger must not be able to mint a login that sees job data.
/// </remarks>
public class SignUpRequest
{
    [Required, StringLength(160, MinimumLength = 2)]
    public string Name { get; set; } = "";

    [Required, EmailAddress, StringLength(160)]
    public string Email { get; set; } = "";

    [StringLength(40)]
    public string Phone { get; set; } = "";

    [Required, StringLength(200, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters.")]
    public string Password { get; set; } = "";
}

/// <summary>A Google ID token, from the app.</summary>
/// <remarks>
/// The *ID* token, not the access token. Only the ID token is a signed
/// assertion about who the person is; an access token is a bearer key for
/// calling Google APIs and proves nothing to us.
/// </remarks>
public class GoogleSignInRequest
{
    [Required]
    public string IdToken { get; set; } = "";
}

/// <summary>Swaps the current token for one scoped to a garage.</summary>
public class SelectWorkshopRequest
{
    [Required, StringLength(40)]
    public string CompanyCode { get; set; } = "";
}

/// <summary>
/// Changes the branch or accounting year the session is looking at.
/// </summary>
/// <remarks>
/// Both optional and applied independently, so the topbar's two dropdowns each
/// send only what they changed. Sending neither is a no-op that still reissues
/// the token, which is harmless and simpler than a special case.
/// </remarks>
public class SelectWorkspaceRequest
{
    /// <summary>A branch id, or <c>""</c> to clear the selection.</summary>
    [StringLength(20)]
    public string? BranchId { get; set; }

    /// <summary>A fiscal year code such as <c>2082/83</c>.</summary>
    [StringLength(20)]
    public string? FiscalYear { get; set; }
}

/// <summary>A branch, as the topbar lists it.</summary>
public record BranchDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Address { get; init; }
    public required string Phone { get; init; }
    public required bool IsDefault { get; init; }
}

/// <summary>One selectable accounting year.</summary>
/// <remarks>
/// The Gregorian window is sent because the client shows it — a user picking
/// "2082/83" should be able to see it means Jul 2025 to Jul 2026 without knowing
/// the conversion.
/// </remarks>
public record FiscalYearDto
{
    public required string Code { get; init; }
    public required DateOnly Start { get; init; }
    public required DateOnly End { get; init; }
    public required bool IsCurrent { get; init; }
}

/// <summary>What the topbar's two dropdowns need, and what is selected now.</summary>
public record WorkspaceDto
{
    public required IReadOnlyList<BranchDto> Branches { get; init; }
    public required IReadOnlyList<FiscalYearDto> FiscalYears { get; init; }

    /// <summary>Null for a tenant with no branches on record.</summary>
    public required string? BranchId { get; init; }
    public required string? BranchName { get; init; }

    public required string FiscalYear { get; init; }
    public required DateOnly FiscalYearStart { get; init; }
    public required DateOnly FiscalYearEnd { get; init; }

    /// <summary>
    /// True when the selected year is the one today falls in — the client uses
    /// it to mark that viewing a closed year is deliberate.
    /// </summary>
    public required bool IsCurrentYear { get; init; }
}

/// <summary>A garage as the customer app lists it in the directory.</summary>
public record WorkshopCardDto
{
    public required string CompanyCode { get; init; }
    public required string Name { get; init; }
    public required string Address { get; init; }
    public required string Phone { get; init; }
    public required string About { get; init; }
    public required string OpeningHours { get; init; }

    /// <summary>
    /// The garage's logo, or null if it has not uploaded one.
    /// </summary>
    /// <remarks>
    /// A directory is a list of strangers, and a mark is what makes one of them
    /// the place the customer has actually driven past. The app falls back to
    /// the first letter of the name rather than a generic icon, so a garage
    /// without a logo still looks like itself.
    /// </remarks>
    public required string? LogoUrl { get; init; }

    public required double? Latitude { get; init; }
    public required double? Longitude { get; init; }

    /// <summary>How many services it publishes — a rough sign of life.</summary>
    public required int ServiceCount { get; init; }

    /// <summary>True when the signed-in customer has already joined it.</summary>
    public required bool IsJoined { get; init; }

    /// <summary>True when this is the garage the app currently opens on.</summary>
    public required bool IsPrimary { get; init; }

    /// <summary>
    /// Straight-line km from the coordinates the caller supplied, if they did.
    /// Null when either side has no pin.
    /// </summary>
    public required double? DistanceKm { get; init; }
}

public class StartRegistrationRequest
{
    [Required, StringLength(40)]
    public string CompanyCode { get; set; } = "";

    /// <summary>
    /// The phone or email the workshop has on file. Either is accepted, because
    /// a customer will not know which one the shop wrote down.
    /// </summary>
    [Required, StringLength(160, MinimumLength = 4)]
    public string Contact { get; set; } = "";
}

public class CompleteRegistrationRequest
{
    [Required, StringLength(40)]
    public string CompanyCode { get; set; } = "";

    [Required, StringLength(160, MinimumLength = 4)]
    public string Contact { get; set; } = "";

    [Required, StringLength(6, MinimumLength = 6, ErrorMessage = "The code is six digits.")]
    public string Code { get; set; } = "";

    /// <summary>
    /// The email this account will sign in with. Defaults to the contact when
    /// that was an email, so most people never see this field.
    /// </summary>
    [EmailAddress, StringLength(160)]
    public string? Email { get; set; }

    [Required, StringLength(200, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters.")]
    public string Password { get; set; } = "";
}

/// <summary>What the app needs after asking for a code.</summary>
public record RegistrationStartedDto
{
    /// <summary>Where the code went, masked — "r••••@gmail.com".</summary>
    public required string SentTo { get; init; }

    public required int ExpiresInMinutes { get; init; }
}

// ── Auth requests ────────────────────────────────────────────────────────────

public class LoginRequest
{
    /// <summary>
    /// Tenant code, e.g. <c>DEMO</c>. Case-insensitive.
    /// </summary>
    /// <remarks>
    /// Required for staff and mechanics — it is part of their credentials, and
    /// the same email may exist under two workshops.
    ///
    /// Omitted by customers. A person who downloaded the app has no idea what a
    /// company code is, and their account belongs above any single garage, so
    /// they sign in with email and password alone. The server falls back to a
    /// customer-only lookup when this is blank.
    /// </remarks>
    [StringLength(40)]
    public string CompanyCode { get; set; } = "";

    [Required, EmailAddress, StringLength(160)]
    public string Email { get; set; } = "";

    [Required, StringLength(200, MinimumLength = 1)]
    public string Password { get; set; } = "";
}

public class RefreshTokenRequest
{
    [Required]
    public string RefreshToken { get; set; } = "";
}

public class ForgotPasswordRequest
{
    /// <summary>
    /// Required for staff, omitted by customers — mirroring <see cref="LoginRequest"/>.
    /// A customer has no company code to give, so demanding one here made the
    /// reset link unreachable for every customer account.
    /// </summary>
    [StringLength(40)]
    public string CompanyCode { get; set; } = "";

    [Required, EmailAddress, StringLength(160)]
    public string Email { get; set; } = "";
}

/// <summary>Checks a reset code without spending it.</summary>
/// <remarks>
/// Its own step so the client can move to the password screen on a good code
/// rather than making somebody type a new password twice because they fat
/// -fingered a digit six fields ago.
/// </remarks>
public class VerifyResetCodeRequest
{
    [StringLength(40)]
    public string CompanyCode { get; set; } = "";

    [Required, EmailAddress, StringLength(160)]
    public string Email { get; set; } = "";

    [Required, StringLength(6, MinimumLength = 6, ErrorMessage = "The code is six digits.")]
    public string Code { get; set; } = "";
}

public class ResetPasswordRequest
{
    /// <summary>
    /// Who is resetting. Sent again rather than carried in the code, because the
    /// code is six digits and must never be the thing that identifies an account
    /// — see <c>User.PasswordResetCodeHash</c>.
    /// </summary>
    [StringLength(40)]
    public string CompanyCode { get; set; } = "";

    [Required, EmailAddress, StringLength(160)]
    public string Email { get; set; } = "";

    /// <summary>The code from the email. Single use.</summary>
    [Required, StringLength(6, MinimumLength = 6, ErrorMessage = "The code is six digits.")]
    public string Code { get; set; } = "";

    [Required, StringLength(200, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters.")]
    public string NewPassword { get; set; } = "";
}

/// <summary>What the client needs after asking for a reset code.</summary>
public record PasswordResetStartedDto
{
    /// <summary>
    /// Where the code went, masked — "ra••••@gmail.com".
    /// </summary>
    /// <remarks>
    /// Masked from what the caller *typed*, not from the account. This endpoint
    /// answers identically whether or not an account matched, and echoing the
    /// mailbox it really went to would break that in the one case that matters.
    /// </remarks>
    public required string SentTo { get; init; }

    public required int ExpiresInMinutes { get; init; }
}

public class UpdateProfileRequest
{
    [Required, StringLength(160, MinimumLength = 1)]
    public string Name { get; set; } = "";

    /// <summary>Changing this changes what you sign in with.</summary>
    [Required, EmailAddress, StringLength(160)]
    public string Email { get; set; } = "";

    [StringLength(40)]
    public string? Phone { get; set; }
}

public class ChangePasswordRequest
{
    [Required]
    public string CurrentPassword { get; set; } = "";

    [Required, StringLength(200, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters.")]
    public string NewPassword { get; set; } = "";
}

/// <summary>
/// Replaces the one-time password an account was handed.
/// </summary>
/// <remarks>
/// No current password, unlike <see cref="ChangePasswordRequest"/>. The account
/// signed in with the temporary one seconds ago and the token proves it, so
/// asking again is friction that buys nothing — and the whole purpose of the
/// screen is that the old password stops mattering.
/// </remarks>
public class SetPasswordRequest
{
    [Required, StringLength(200, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters.")]
    public string NewPassword { get; set; } = "";
}

// ── Auth responses ───────────────────────────────────────────────────────────

/// <summary>The signed-in user, as the dashboard's AuthContext holds it.</summary>
public record AuthUserDto
{
    public required string Id { get; init; }
    public required string Email { get; init; }
    public required string Name { get; init; }

    /// <summary>Owner, Manager, Advisor, Mechanic or Customer.</summary>
    public required string Role { get; init; }

    public required string Workshop { get; init; }
    public required string CompanyCode { get; init; }
    public string? Phone { get; init; }

    /// <summary>
    /// Absolute URL of their profile photo, or null when they have none.
    /// </summary>
    /// <remarks>
    /// Absolute rather than the stored relative path, so the app can hand it
    /// straight to an image widget. Same treatment job photos get.
    /// </remarks>
    public string? PhotoUrl { get; init; }

    // ── Mobile ───────────────────────────────────────────────────────────────
    // Null for staff. The app reads these straight off the login response so it
    // knows which shell to show and which customer it is speaking for, without
    // a second round trip.

    /// <summary>Set for a Mechanic — the name they are assigned under on jobs.</summary>
    public string? MechanicName { get; init; }

    /// <summary>Set for a Customer — the customer record this login owns.</summary>
    public string? CustomerId { get; init; }
}

/// <summary>What a successful login or refresh returns.</summary>
public record AuthResultDto
{
    /// <summary>Short-lived JWT. Sent as <c>Authorization: Bearer …</c>.</summary>
    public required string AccessToken { get; init; }

    /// <summary>When the access token expires (UTC). The client refreshes before this.</summary>
    public required DateTime AccessTokenExpiresAt { get; init; }

    /// <summary>Long-lived token used to obtain a new pair. Rotated on every use.</summary>
    public required string RefreshToken { get; init; }

    public required DateTime RefreshTokenExpiresAt { get; init; }

    public required AuthUserDto User { get; init; }

    /// <summary>
    /// True while the password is still one somebody else chose.
    /// </summary>
    /// <remarks>
    /// The client sends this session straight to the "choose a password" screen.
    /// It is a signal, not a control — the token itself is restricted to that
    /// one endpoint, so a client that ignores this gets 403s rather than access.
    /// </remarks>
    public bool MustSetPassword { get; init; }
}
