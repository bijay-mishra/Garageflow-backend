using System.ComponentModel.DataAnnotations;

namespace GarageFlow.Api.Contracts;

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

    /// <summary>Owner, Manager or Advisor.</summary>
    public required string Role { get; init; }

    public required string Workshop { get; init; }
    public required string CompanyCode { get; init; }
    public string? Phone { get; init; }
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
