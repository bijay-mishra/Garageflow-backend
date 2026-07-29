using System.ComponentModel.DataAnnotations;

// ── Customer self-registration ───────────────────────────────────────────────
// Two legs. The first proves the person is a customer the workshop already
// holds, by sending a code to the contact already on that record; the second
// redeems it. There is no open sign-up: a stranger cannot create an account, and
// a real customer's vehicles and history are there on first sign-in.

namespace GarageFlow.Api.Contracts;

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
    /// <summary>Tenant code, e.g. <c>DEMO</c>. Case-insensitive.</summary>
    [Required, StringLength(40)]
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
    [Required, StringLength(40)]
    public string CompanyCode { get; set; } = "";

    [Required, EmailAddress, StringLength(160)]
    public string Email { get; set; } = "";
}

public class ResetPasswordRequest
{
    /// <summary>The token from the emailed link. Single use.</summary>
    [Required]
    public string Token { get; set; } = "";

    [Required, StringLength(200, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters.")]
    public string NewPassword { get; set; } = "";
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
}
