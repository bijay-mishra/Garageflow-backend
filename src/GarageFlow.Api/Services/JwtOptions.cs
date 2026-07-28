namespace GarageFlow.Api.Services;

/// <summary>JWT settings, bound from the <c>Jwt</c> section of appsettings.</summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// Symmetric signing key. Must be at least 32 bytes for HMAC-SHA256.
    /// </summary>
    /// <remarks>
    /// The value in appsettings is a development placeholder. In production this
    /// belongs in user-secrets, an environment variable or a key vault — anyone
    /// holding it can mint tokens for any user.
    /// </remarks>
    public string Key { get; set; } = "";

    public string Issuer { get; set; } = "GarageFlow";
    public string Audience { get; set; } = "GarageFlowDashboard";

    /// <summary>
    /// Access token lifetime. Short on purpose: an access token cannot be
    /// revoked once issued, so its blast radius is bounded by this number.
    /// </summary>
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>Refresh token lifetime — how long "stay signed in" lasts.</summary>
    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>How long an emailed password-reset link stays valid.</summary>
    public int PasswordResetTokenMinutes { get; set; } = 30;
}
