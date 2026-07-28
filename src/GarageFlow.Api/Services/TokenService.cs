using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using GarageFlow.Api.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GarageFlow.Api.Services;

/// <summary>
/// Mints and hashes tokens. The only place in the app that knows how a token is
/// built, so changing the algorithm or the claim set is a one-file change.
/// </summary>
public class TokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JwtOptions _options = options.Value;

    /// <summary>Claim carrying the tenant, so a token from one workshop cannot read another's data.</summary>
    public const string CompanyCodeClaim = "company_code";

    /// <summary>
    /// Builds a signed access token for <paramref name="user"/>.
    /// </summary>
    /// <returns>The compact JWT and the moment it expires (UTC).</returns>
    public (string Token, DateTime ExpiresAt) CreateAccessToken(User user)
    {
        var expiresAt = clock.GetUtcNow().UtcDateTime.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email),
            // A unique id per token, so an individual token can be denylisted later.
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.Role, user.Role),
            new(CompanyCodeClaim, user.CompanyCode),
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Key)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: clock.GetUtcNow().UtcDateTime,
            expires: expiresAt,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    /// <summary>
    /// Generates a cryptographically random token to hand to the client.
    /// Used for both refresh tokens and password-reset links.
    /// </summary>
    public static string CreateSecureToken()
    {
        // 32 bytes of CSPRNG output — not Guid, which is not built for secrecy.
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncoder.Encode(bytes);
    }

    /// <summary>
    /// Hashes a token for storage. Plain SHA-256 rather than a slow KDF is
    /// correct here: these tokens are already 256 bits of randomness, so there
    /// is no low-entropy guess for an attacker to brute-force.
    /// </summary>
    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public DateTime RefreshTokenExpiry() =>
        clock.GetUtcNow().UtcDateTime.AddDays(_options.RefreshTokenDays);

    public DateTime PasswordResetExpiry() =>
        clock.GetUtcNow().UtcDateTime.AddMinutes(_options.PasswordResetTokenMinutes);
}
