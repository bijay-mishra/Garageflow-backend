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

    // ── Workspace ────────────────────────────────────────────────────────────
    // The branch and accounting year the session is looking at. In the token
    // rather than sent per request for the same reason the tenant is: a value
    // the client supplies is a value the client can change, and "show me last
    // year's books" then becomes "show me whatever I ask for".
    //
    // Both are also what make the switch observable. Changing either mints a new
    // token, which is the signal the dashboard hangs its full refetch on.

    /// <summary>Selected branch, e.g. <c>BR-001</c>. Absent when none is chosen.</summary>
    public const string BranchClaim = "branch_id";

    /// <summary>Selected accounting year, e.g. <c>2082/83</c>.</summary>
    public const string FiscalYearClaim = "fiscal_year";

    /// <summary>
    /// Present only while the account is still on a password somebody else set.
    /// </summary>
    /// <remarks>
    /// A token carrying this reaches exactly one endpoint — see
    /// <c>MustSetPasswordFilter</c>. It is on the token rather than read from
    /// the database per request because it has to be true of the <i>session</i>:
    /// clearing the flag issues a new pair, and the old token stays restricted
    /// for the few minutes it has left rather than silently gaining the run of
    /// the API.
    /// </remarks>
    public const string MustSetPasswordClaim = "must_set_password";

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

        // Omitted rather than sent empty when nothing is selected: an absent
        // claim reads as "no choice made", where "" would look like a real
        // branch whose id happens to be blank.
        if (!string.IsNullOrWhiteSpace(user.BranchId))
            claims.Add(new Claim(BranchClaim, user.BranchId));

        if (!string.IsNullOrWhiteSpace(user.FiscalYear))
            claims.Add(new Claim(FiscalYearClaim, user.FiscalYear));

        if (user.MustSetPassword) claims.Add(new Claim(MustSetPasswordClaim, "1"));

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
    /// A one-time password to hand over, in a form somebody can read aloud.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four groups of four, hyphenated — <c>K7RM-92FP-4XQT-8HDW</c>. Random
    /// enough that it cannot be guessed in the minutes before it is used, and
    /// shaped so it survives being read down a phone line, which is exactly how
    /// these get handed over.
    /// </para>
    /// <para>
    /// The alphabet drops the characters that get misheard or mistyped: no O
    /// against 0, no I or L against 1, no U against V. 32 symbols across 16
    /// places is 80 bits, so the omissions cost nothing that matters.
    /// </para>
    /// <para>
    /// It never needs to survive: the account is made to replace it at first
    /// sign-in, and only its hash is ever stored.
    /// </para>
    /// </remarks>
    public static string CreateOneTimePassword()
    {
        const string alphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";

        var chars = RandomNumberGenerator.GetString(alphabet, 16);

        return string.Join('-', Enumerable.Range(0, 4).Select(i => chars.Substring(i * 4, 4)));
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
        clock.GetUtcNow().UtcDateTime.AddMinutes(_options.PasswordResetCodeMinutes);

    /// <summary>
    /// The same window in minutes, for telling the user how long they have.
    /// </summary>
    /// <remarks>
    /// Exposed rather than duplicated as a constant in the controller, so the
    /// sentence on screen cannot drift away from the expiry actually enforced.
    /// </remarks>
    public int PasswordResetMinutes => _options.PasswordResetCodeMinutes;
}
