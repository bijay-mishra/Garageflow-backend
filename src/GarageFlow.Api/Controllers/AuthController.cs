using System.Net;
using System.Security.Claims;
using GarageFlow.Api.Contracts;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using GarageFlow.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GarageFlow.Api.Controllers;

/// <summary>Sign-in, token refresh, sign-out and password reset.</summary>
[ApiController]
[Route("api/auth")]
[Produces("application/json")]
[AllowAnonymous]
public class AuthController(
    GarageFlowDbContext db,
    TokenService tokens,
    IPasswordHasher<User> passwordHasher,
    IEmailSender email,
    IOptions<EmailOptions> emailOptions,
    TimeProvider clock,
    ILogger<AuthController> logger) : ControllerBase
{
    /// <summary>
    /// Signs in with company code + email + password and returns a token pair.
    /// </summary>
    [HttpPost("login")]
    [ProducesResponseType<ApiResponse<AuthResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<AuthResultDto>>> Login(LoginRequest request, CancellationToken ct)
    {
        var companyCode = request.CompanyCode.Trim().ToUpperInvariant();
        var emailAddress = request.Email.Trim();

        var user = await db.Users.FirstOrDefaultAsync(
            u => u.CompanyCode == companyCode && u.Email == emailAddress, ct);

        // One message for "no such user", "wrong password" and "wrong company",
        // so the response cannot be used to discover which accounts exist.
        const string invalid = "Company code, email or password is incorrect.";

        if (user is null)
        {
            // Still spend the time hashing, so a missing account is not
            // detectably faster than a wrong password.
            passwordHasher.HashPassword(new User(), request.Password);
            return Unauthorized(ApiResponse.Failure(invalid));
        }

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);

        if (verification == PasswordVerificationResult.Failed)
            return Unauthorized(ApiResponse.Failure(invalid));

        if (!user.IsActive)
            return Unauthorized(ApiResponse.Failure("This account has been deactivated. Contact your administrator."));

        // The hasher tells us when a stored hash used older parameters.
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        user.LastLoginAt = clock.GetUtcNow().UtcDateTime;

        var result = await IssueTokensAsync(user, ct);

        logger.LogInformation("User {Email} signed in to {CompanyCode}", user.Email, user.CompanyCode);

        return Ok(ApiResponse<AuthResultDto>.Ok(result, $"Welcome back, {user.FullName}."));
    }

    /// <summary>
    /// Exchanges a refresh token for a new pair. The old token is revoked, so a
    /// refresh token works exactly once.
    /// </summary>
    [HttpPost("refresh")]
    [ProducesResponseType<ApiResponse<AuthResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<AuthResultDto>>> Refresh(
        RefreshTokenRequest request, CancellationToken ct)
    {
        var hash = TokenService.HashToken(request.RefreshToken);

        var stored = await db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (stored?.User is null || !stored.IsActive || !stored.User.IsActive)
            return Unauthorized(ApiResponse.Failure("Your session has expired. Please sign in again."));

        // Rotation: this token is spent the moment it is used.
        stored.RevokedAt = clock.GetUtcNow().UtcDateTime;

        var result = await IssueTokensAsync(stored.User, ct);

        return Ok(ApiResponse<AuthResultDto>.Ok(result, "Session refreshed."));
    }

    /// <summary>
    /// Signs out by revoking the refresh token.
    /// </summary>
    /// <remarks>
    /// The access token is not revoked — it cannot be, which is why it is
    /// short-lived. The client discards it; it dies on its own within minutes.
    /// </remarks>
    [HttpPost("logout")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse>> Logout(RefreshTokenRequest request, CancellationToken ct)
    {
        var hash = TokenService.HashToken(request.RefreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (stored is { RevokedAt: null })
        {
            stored.RevokedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }

        // Always succeeds: signing out twice, or with a token the server has
        // never seen, should still leave the client signed out.
        return Ok(ApiResponse.Success("Signed out."));
    }

    /// <summary>The signed-in user, from the bearer token.</summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<ApiResponse<AuthUserDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<AuthUserDto>>> Me(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null || !user.IsActive)
            return Unauthorized(ApiResponse.Failure("Your session is no longer valid. Please sign in again."));

        return Ok(ApiResponse<AuthUserDto>.Ok(ToDto(user), "Profile loaded."));
    }

    /// <summary>Updates the signed-in user's own name, email and phone.</summary>
    /// <remarks>
    /// Changing the email changes the sign-in identity, so it has to stay
    /// unique within the tenant — the next sign-in uses the new address.
    /// </remarks>
    [HttpPut("profile")]
    [Authorize]
    [ProducesResponseType<ApiResponse<AuthUserDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<AuthUserDto>>> UpdateProfile(
        UpdateProfileRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return Unauthorized(ApiResponse.Failure("Your session is no longer valid. Please sign in again."));

        var newEmail = request.Email.Trim();

        if (!string.Equals(newEmail, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            var taken = await db.Users.AnyAsync(
                u => u.CompanyCode == user.CompanyCode && u.Email == newEmail && u.Id != user.Id, ct);

            if (taken)
                return BadRequest(ApiResponse.Failure($"'{newEmail}' is already used by another account."));

            user.Email = newEmail;
        }

        user.FullName = request.Name.Trim();
        user.Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();

        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse<AuthUserDto>.Ok(ToDto(user), "Profile updated successfully."));
    }

    /// <summary>
    /// Emails a password reset link.
    /// </summary>
    /// <remarks>
    /// Always answers success, whether or not the account exists — otherwise
    /// this endpoint becomes a way to enumerate valid email addresses.
    /// </remarks>
    [HttpPost("forgot-password")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse>> ForgotPassword(
        ForgotPasswordRequest request, CancellationToken ct)
    {
        const string alwaysTheSame =
            "If an account matches those details, a password reset link is on its way.";

        var companyCode = request.CompanyCode.Trim().ToUpperInvariant();
        var emailAddress = request.Email.Trim();

        var user = await db.Users.FirstOrDefaultAsync(
            u => u.CompanyCode == companyCode && u.Email == emailAddress, ct);

        if (user is null || !user.IsActive)
            return Ok(ApiResponse.Success(alwaysTheSame));

        // The client gets the raw token; only its hash is stored.
        var token = TokenService.CreateSecureToken();
        user.PasswordResetTokenHash = TokenService.HashToken(token);
        user.PasswordResetExpiresAt = tokens.PasswordResetExpiry();
        await db.SaveChangesAsync(ct);

        var link = $"{emailOptions.Value.DashboardUrl.TrimEnd('/')}/reset-password?token={WebUtility.UrlEncode(token)}";

        await email.SendAsync(user.Email, "Reset your GarageFlow password", ResetEmailBody(user.FullName, link), ct);

        return Ok(ApiResponse.Success(alwaysTheSame));
    }

    /// <summary>Sets a new password using the token from the emailed link.</summary>
    [HttpPost("reset-password")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse>> ResetPassword(
        ResetPasswordRequest request, CancellationToken ct)
    {
        var hash = TokenService.HashToken(request.Token);
        var now = clock.GetUtcNow().UtcDateTime;

        var user = await db.Users.FirstOrDefaultAsync(u => u.PasswordResetTokenHash == hash, ct);

        if (user is null || user.PasswordResetExpiresAt is null || user.PasswordResetExpiresAt < now)
            return BadRequest(ApiResponse.Failure("This reset link is invalid or has expired. Please request a new one."));

        user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);

        // Single use.
        user.PasswordResetTokenHash = null;
        user.PasswordResetExpiresAt = null;

        // Changing a password signs out every other device.
        await RevokeAllRefreshTokensAsync(user.Id, ct);

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Password reset completed for {Email}", user.Email);

        return Ok(ApiResponse.Success("Your password has been reset. You can now sign in."));
    }

    /// <summary>Changes the signed-in user's own password.</summary>
    [HttpPut("change-password")]
    [Authorize]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse>> ChangePassword(
        ChangePasswordRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return Unauthorized(ApiResponse.Failure("Your session is no longer valid. Please sign in again."));

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword);

        if (verification == PasswordVerificationResult.Failed)
            return BadRequest(ApiResponse.Failure("Your current password is incorrect."));

        user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);
        await RevokeAllRefreshTokensAsync(user.Id, ct);
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse.Success("Password changed. Please sign in again."));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Issues a fresh pair and stores the refresh token's hash. Saves.</summary>
    private async Task<AuthResultDto> IssueTokensAsync(User user, CancellationToken ct)
    {
        var (accessToken, accessExpiresAt) = tokens.CreateAccessToken(user);

        var refreshToken = TokenService.CreateSecureToken();
        var refreshExpiresAt = tokens.RefreshTokenExpiry();

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = TokenService.HashToken(refreshToken),
            ExpiresAt = refreshExpiresAt,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        });

        await db.SaveChangesAsync(ct);

        return new AuthResultDto
        {
            AccessToken = accessToken,
            AccessTokenExpiresAt = accessExpiresAt,
            RefreshToken = refreshToken,
            RefreshTokenExpiresAt = refreshExpiresAt,
            User = ToDto(user),
        };
    }

    private async Task RevokeAllRefreshTokensAsync(string userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.RevokedAt, now), ct);
    }

    private static AuthUserDto ToDto(User user) => new()
    {
        Id = user.Id,
        Email = user.Email,
        Name = user.FullName,
        Role = user.Role,
        Workshop = user.Workshop,
        CompanyCode = user.CompanyCode,
        Phone = user.Phone,
    };

    private static string ResetEmailBody(string name, string link) => $"""
        <div style="font-family:system-ui,-apple-system,Segoe UI,sans-serif;max-width:520px;margin:0 auto;color:#0f172a">
          <h2 style="margin:0 0 12px">Reset your password</h2>
          <p style="margin:0 0 16px">Hi {WebUtility.HtmlEncode(name)},</p>
          <p style="margin:0 0 16px">
            We received a request to reset your GarageFlow password. Click the button below to choose a new one.
          </p>
          <p style="margin:0 0 24px">
            <a href="{link}" style="display:inline-block;background:#2563eb;color:#fff;text-decoration:none;
               padding:12px 20px;border-radius:8px;font-weight:600">Reset password</a>
          </p>
          <p style="margin:0 0 8px;color:#64748b;font-size:13px">
            This link expires in 30 minutes and can be used once.
          </p>
          <p style="margin:0;color:#64748b;font-size:13px">
            If you did not request this, you can ignore this email — your password will not change.
          </p>
        </div>
        """;
}
