using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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

    // ── Customer self-registration ───────────────────────────────────────────
    // Two legs, and neither of them is an open sign-up. A person proves they are
    // a customer the workshop already holds by receiving a code at the phone or
    // email on that record. A stranger cannot create an account; a real customer
    // finds their vehicles and history waiting on first sign-in.

    /// <summary>
    /// Sends a six-digit code to the contact the workshop has on file.
    /// </summary>
    /// <remarks>
    /// Answers <em>byte for byte</em> identically whether or not the contact
    /// matches anything, for the same reason as forgot-password: otherwise this
    /// becomes a way to ask a workshop whether it holds a given phone number.
    ///
    /// That includes the masked destination, which echoes what the caller typed
    /// rather than naming the mailbox the code actually went to. Naming it would
    /// confirm a customer exists — and worse, typing a phone number and getting
    /// an email back would confirm it twice over. The cost is that a customer
    /// with two addresses has to check both, which is a fair trade for not
    /// handing out a customer list.
    /// </remarks>
    [HttpPost("register/start")]
    [ProducesResponseType<ApiResponse<RegistrationStartedDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RegistrationStartedDto>>> StartRegistration(
        StartRegistrationRequest request, CancellationToken ct)
    {
        const int expiryMinutes = 15;

        var companyCode = request.CompanyCode.Trim().ToUpperInvariant();
        var contact = request.Contact.Trim();

        // Built once and returned on every path — success included. Anything
        // that varies with whether the customer exists is an enumeration oracle.
        var vague = ApiResponse<RegistrationStartedDto>.Ok(
            new RegistrationStartedDto { SentTo = Mask(contact), ExpiresInMinutes = expiryMinutes },
            "If those details match our records, a code is on its way to the email we have on file.");

        var customer = await FindCustomerByContactAsync(contact, ct);

        if (customer is null) return Ok(vague);

        // Already has a login — nothing to claim. Sending a code anyway would
        // let someone discover which customers have signed up.
        var alreadyRegistered = await db.Users.AnyAsync(
            u => u.CompanyCode == companyCode && u.CustomerId == customer.Id, ct);

        if (alreadyRegistered) return Ok(vague);

        // Codes go by email. The customer's phone is accepted as the *identifier*
        // because that is what a workshop usually writes down, but delivering to
        // it needs an SMS provider — see the note in appsettings. A customer with
        // no email on file cannot self-register yet, and staff create their
        // account as before.
        if (string.IsNullOrWhiteSpace(customer.Email)) return Ok(vague);

        var now = clock.GetUtcNow().UtcDateTime;

        // One live code per customer. Asking again replaces the last one rather
        // than leaving several valid at once.
        var previous = await db.CustomerRegistrations
            .Where(r => r.CustomerId == customer.Id && r.ConsumedAt == null)
            .ToListAsync(ct);

        db.CustomerRegistrations.RemoveRange(previous);

        var code = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString();

        db.CustomerRegistrations.Add(new CustomerRegistration
        {
            CustomerId = customer.Id,
            CompanyCode = companyCode,
            Contact = contact,
            CodeHash = TokenService.HashToken(code),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(expiryMinutes),
        });

        await db.SaveChangesAsync(ct);

        await email.SendAsync(
            customer.Email,
            "Your GarageFlow verification code",
            RegistrationEmailBody(customer.Name, code, expiryMinutes),
            ct);

        return Ok(vague);
    }

    /// <summary>
    /// Redeems the code and creates the customer's login.
    /// </summary>
    [HttpPost("register/complete")]
    [ProducesResponseType<ApiResponse<AuthResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<AuthResultDto>>> CompleteRegistration(
        CompleteRegistrationRequest request, CancellationToken ct)
    {
        const string badCode = "That code is wrong or has expired. Ask for a new one.";

        var companyCode = request.CompanyCode.Trim().ToUpperInvariant();
        var contact = request.Contact.Trim();
        var now = clock.GetUtcNow().UtcDateTime;

        var registration = await db.CustomerRegistrations
            .Include(r => r.Customer)
            .Where(r => r.CompanyCode == companyCode && r.Contact == contact && r.ConsumedAt == null)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);

        if (registration is null || registration.ExpiresAt < now)
            return BadRequest(ApiResponse.Failure(badCode));

        // A six-digit code is a million guesses, which is an afternoon for a
        // script. Five wrong tries burns it.
        if (registration.Attempts >= 5)
        {
            db.CustomerRegistrations.Remove(registration);
            await db.SaveChangesAsync(ct);

            return BadRequest(ApiResponse.Failure("Too many wrong codes. Ask for a new one."));
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(TokenService.HashToken(request.Code.Trim())),
                Encoding.UTF8.GetBytes(registration.CodeHash)))
        {
            registration.Attempts++;
            await db.SaveChangesAsync(ct);

            return BadRequest(ApiResponse.Failure(badCode));
        }

        var customer = registration.Customer!;

        // The address they will sign in with: whatever they supplied, else the
        // contact when that was an email, else the one on the customer record.
        var loginEmail = (request.Email ?? (contact.Contains('@') ? contact : customer.Email)).Trim();

        if (string.IsNullOrWhiteSpace(loginEmail))
            return BadRequest(ApiResponse.Failure("We need an email address to sign you in with."));

        if (await db.Users.AnyAsync(u => u.CompanyCode == companyCode && u.Email == loginEmail, ct))
            return BadRequest(ApiResponse.Failure("There is already an account with that email. Try signing in."));

        var workshop = await db.Workshops
            .Where(w => w.CompanyCode == companyCode)
            .Select(w => w.Name)
            .FirstOrDefaultAsync(ct);

        var user = new User
        {
            Id = Ids.Next(await db.Users.Select(u => u.Id).ToListAsync(ct), "USR"),
            CompanyCode = companyCode,
            Email = loginEmail,
            FullName = customer.Name,
            Phone = customer.Phone,
            Role = Vocabulary.CustomerRole,
            CustomerId = customer.Id,
            Workshop = workshop ?? "",
            IsActive = true,
            CreatedAt = now,
            LastLoginAt = now,
            PasswordHash = string.Empty,
        };

        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        registration.ConsumedAt = now;
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Customer {CustomerId} registered as {UserId}", customer.Id, user.Id);

        // Signed straight in. Making someone type the password they just chose,
        // on the next screen, is ceremony for nothing.
        var result = await IssueTokensAsync(user, ct);

        return Ok(ApiResponse<AuthResultDto>.Ok(result, $"Welcome, {customer.Name}."));
    }

    /// <summary>
    /// The customer whose phone or email matches, if exactly one does.
    /// </summary>
    /// <remarks>
    /// Phone numbers are compared with spaces, dashes and brackets stripped:
    /// a shop types "+977 9841012345" and the customer types "9841012345", and
    /// both have to find the same record. Only the last nine digits are compared,
    /// so a country code written one day and omitted the next still matches.
    /// </remarks>
    private async Task<Customer?> FindCustomerByContactAsync(string contact, CancellationToken ct)
    {
        if (contact.Contains('@'))
        {
            return await db.Customers.FirstOrDefaultAsync(
                c => c.Email != "" && c.Email == contact, ct);
        }

        var digits = new string(contact.Where(char.IsDigit).ToArray());

        if (digits.Length < 7) return null;

        var tail = digits[^9..];

        // Loaded and compared in memory: the normalisation has no SQL
        // equivalent, and a customer list is small enough that this is cheaper
        // than a computed column nobody else would use.
        var candidates = await db.Customers
            .Where(c => c.Phone != "")
            .Select(c => new { c.Id, c.Phone })
            .ToListAsync(ct);

        var match = candidates
            .Select(c => new
            {
                c.Id,
                Digits = new string(c.Phone.Where(char.IsDigit).ToArray()),
            })
            .Where(c => c.Digits.Length >= 9 && c.Digits[^9..] == tail)
            .ToList();

        // Two customers sharing a number is a data problem the shop has to sort
        // out; guessing which one they meant would be worse.
        if (match.Count != 1) return null;

        return await db.Customers.FirstOrDefaultAsync(c => c.Id == match[0].Id, ct);
    }

    /// <summary>"ramesh.s@gmail.com" → "ra••••@gmail.com"; a phone keeps its last three.</summary>
    private static string Mask(string contact)
    {
        if (contact.Contains('@'))
        {
            var parts = contact.Split('@');
            var name = parts[0];
            var shown = name.Length <= 2 ? name : name[..2];

            return $"{shown}••••@{parts[1]}";
        }

        return contact.Length <= 3 ? "•••" : $"••••••{contact[^3..]}";
    }

    private static string RegistrationEmailBody(string name, string code, int minutes) => $"""
        <p>Hello {WebUtility.HtmlEncode(name)},</p>
        <p>Your GarageFlow verification code is:</p>
        <p style="font-size:26px;font-weight:700;letter-spacing:5px;margin:18px 0">{code}</p>
        <p>It expires in {minutes} minutes. If you did not ask for this, you can ignore this email —
        nobody can use it without your mailbox.</p>
        """;

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
        MechanicName = user.MechanicName,
        CustomerId = user.CustomerId,
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
