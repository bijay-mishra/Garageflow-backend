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
/// <remarks>
/// Authorisation here is opt-*out*: the controller requires a token and the
/// genuinely public actions say so individually.
///
/// It was the other way round — <c>[AllowAnonymous]</c> on the class, with
/// <c>[Authorize]</c> on the handful of actions that needed it. That does not
/// work, and fails silently: ASP.NET Core skips authorisation entirely when it
/// finds <c>IAllowAnonymous</c> anywhere in an endpoint's metadata, so the
/// action-level attributes were decoration. Every role guard in this file was
/// dead, and a mechanic could call a staff-only endpoint and be handed a token.
///
/// The damage was limited because each of those actions re-reads the user from
/// the token and refuses when there is none — an anonymous caller still got a
/// 401. But the *role* check never ran, and any action added later would have
/// inherited the same hole. With the default inverted, forgetting an attribute
/// now locks an endpoint rather than opening it.
/// </remarks>
[ApiController]
[Route("api/auth")]
[Produces("application/json")]
[Authorize]
public class AuthController(
    GarageFlowDbContext db,
    TokenService tokens,
    IPasswordHasher<User> passwordHasher,
    IEmailSender email,
    IOptions<EmailOptions> emailOptions,
    PhotoStorage photos,
    GoogleAuth google,
    TimeProvider clock,
    ILogger<AuthController> logger) : ControllerBase
{
    /// <summary>
    /// Signs in with company code + email + password and returns a token pair.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse<AuthResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<AuthResultDto>>> Login(LoginRequest request, CancellationToken ct)
    {
        var companyCode = request.CompanyCode.Trim().ToUpperInvariant();
        var emailAddress = request.Email.Trim();

        // Two ways in, and which one applies is decided by whether a company
        // code was supplied rather than by a separate endpoint — the client sends
        // what it has and the server works out the rest.
        //
        // With a code: staff and mechanics, where the code is a credential and
        // the same email may exist under two workshops.
        //
        // Without: a customer. Their account sits above any single garage, so
        // email alone identifies it. Restricted to the Customer role so this can
        // never become a way to reach a staff login without knowing the tenant.
        // Two roles sign in without a company code, for opposite reasons: a
        // customer belongs above every garage, and the superadmin belongs to
        // none. Everyone else is staff of exactly one workshop and the code is
        // part of their credentials.
        var user = string.IsNullOrWhiteSpace(companyCode)
            ? await db.Users.FirstOrDefaultAsync(
                u => u.Email == emailAddress &&
                     (u.Role == Vocabulary.CustomerRole || u.Role == Vocabulary.SuperAdminRole),
                ct)
            : await db.Users.FirstOrDefaultAsync(
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

        // Suspending a company has to stop its staff signing in, or suspension
        // means nothing. Checked after the password so the response cannot be
        // used to discover which companies are suspended.
        if (!string.IsNullOrEmpty(user.CompanyCode))
        {
            var companyActive = await db.Workshops
                .Where(w => w.CompanyCode == user.CompanyCode)
                .Select(w => (bool?)w.IsActive)
                .FirstOrDefaultAsync(ct);

            // Null means no workshop row yet — a tenant created before this
            // column existed. Treated as active rather than locking them out.
            if (companyActive == false)
            {
                return Unauthorized(ApiResponse.Failure(
                    "This workshop's account is suspended. Please contact GarageFlow."));
            }
        }

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
    [AllowAnonymous]
    // The restriction rides on the token, and CreateAccessToken re-reads the
    // flag — so a refresh mid-flow returns a token that is still restricted.
    // Blocking the refresh itself would only expire the session mid-screen.
    [AllowWhileSettingPassword]
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
    [AllowAnonymous]
    // Anonymous already, but the client sends its bearer on every request — and
    // without this the filter would refuse to let a half-signed-in session leave.
    [AllowWhileSettingPassword]
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
    // Reachable while the password is still being set: the screen greets them by
    // name, and a client that cannot read its own session cannot draw anything.
    [AllowWhileSettingPassword]
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
    /// Emails a six-digit code that lets somebody choose a new password.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A code rather than a link, and the phone is the reason. A link in an email
    /// opens a browser, not the app; bringing it back into Flutter needs Android
    /// App Links and a verified <c>assetlinks.json</c> served from a public HTTPS
    /// domain, which is infrastructure this product does not have yet. So the
    /// mobile reset was a dead end — the mail arrived and the app could do
    /// nothing with it, because the page it pointed at is the dashboard. Six
    /// digits work identically on both clients with nothing to set up, and the
    /// app can offer to paste them.
    /// </para>
    /// <para>
    /// Always answers success, whether or not the account exists — otherwise this
    /// endpoint becomes a way to enumerate valid email addresses. The masked
    /// destination echoes what was typed for the same reason.
    /// </para>
    /// </remarks>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse<PasswordResetStartedDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PasswordResetStartedDto>>> ForgotPassword(
        ForgotPasswordRequest request, CancellationToken ct)
    {
        var emailAddress = request.Email.Trim();

        // Built once and returned on every path, success included.
        var vague = ApiResponse<PasswordResetStartedDto>.Ok(
            new PasswordResetStartedDto
            {
                SentTo = Mask(emailAddress),
                ExpiresInMinutes = tokens.PasswordResetMinutes,
            },
            "If an account matches those details, a six-digit code is on its way.");

        var user = await FindForResetAsync(request.CompanyCode, emailAddress, ct);

        if (user is null || !user.IsActive) return Ok(vague);

        var now = clock.GetUtcNow().UtcDateTime;

        // Asking again straight away resends nothing. The caller cannot tell —
        // the answer is the same sentence either way.
        if (user.PasswordResetSentAt is { } last && now - last < ResendInterval)
            return Ok(vague);

        var code = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString();

        // One live code per account: asking again replaces the last one rather
        // than leaving several valid at once.
        user.PasswordResetCodeHash = TokenService.HashToken(code);
        user.PasswordResetExpiresAt = tokens.PasswordResetExpiry();
        user.PasswordResetAttempts = 0;
        user.PasswordResetSentAt = now;

        await db.SaveChangesAsync(ct);

        // Deep-links straight to the second step with the account already filled
        // in. The code is deliberately *not* in the URL: it would then sit in
        // browser history and in the referrer of anything that page loads.
        var link =
            $"{emailOptions.Value.DashboardUrl.TrimEnd('/')}/reset-password" +
            $"?company={WebUtility.UrlEncode(user.CompanyCode)}" +
            $"&email={WebUtility.UrlEncode(user.Email)}";

        await email.SendAsync(
            user.Email,
            "Your GarageFlow password reset code",
            ResetEmailBody(user.FullName, code, tokens.PasswordResetMinutes, link),
            ct);

        return Ok(vague);
    }

    /// <summary>
    /// Checks a reset code without spending it.
    /// </summary>
    /// <remarks>
    /// Lets the client move to the password screen on a good code. Without it,
    /// somebody who mistyped a digit would find that out only after choosing and
    /// confirming a new password, and would have to type all three again.
    ///
    /// Wrong guesses still count here, so this is not a free oracle to grind the
    /// code against — five wrong answers burn it whichever endpoint they arrive
    /// at.
    /// </remarks>
    [HttpPost("verify-reset-code")]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse>> VerifyResetCode(
        VerifyResetCodeRequest request, CancellationToken ct)
    {
        var user = await FindForResetAsync(request.CompanyCode, request.Email, ct);
        var failure = await CheckResetCodeAsync(user, request.Code, ct);

        if (failure is not null) return BadRequest(ApiResponse.Failure(failure));

        return Ok(ApiResponse.Success("Code accepted. Choose your new password."));
    }

    // ── Open sign-up, and choosing a garage ──────────────────────────────────

    /// <summary>
    /// Creates a customer account. No company code, no invitation.
    /// </summary>
    /// <remarks>
    /// The account belongs to no garage. That is deliberate and is the whole
    /// shape of the thing: a person signs up, browses the directory, and joins
    /// whichever garages they use. Until they join one there is nothing to see,
    /// and the app takes them to the directory.
    ///
    /// Customer role only. There is no parameter here that could produce a
    /// mechanic or an owner — staff accounts are created by a workshop on the
    /// dashboard, which is how a garage actually onboards someone it employs.
    /// </remarks>
    [HttpPost("signup")]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse<AuthResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<AuthResultDto>>> SignUp(
        SignUpRequest request, CancellationToken ct)
    {
        var emailAddress = request.Email.Trim();

        // Unique across customers, since email alone is how they sign in.
        var taken = await db.Users.AnyAsync(
            u => u.Email == emailAddress && u.Role == Vocabulary.CustomerRole, ct);

        if (taken)
        {
            return BadRequest(ApiResponse.Failure(
                "There is already an account with that email. Try signing in, or reset your password."));
        }

        var now = clock.GetUtcNow().UtcDateTime;

        var user = new User
        {
            Id = Ids.Next(await db.Users.Select(u => u.Id).ToListAsync(ct), "USR"),
            // Blank until they pick a garage. Every scoped endpoint reads this,
            // so an account with nothing here simply sees nothing — which is the
            // correct answer for somebody who has not joined anywhere.
            CompanyCode = "",
            Email = emailAddress,
            FullName = request.Name.Trim(),
            Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim(),
            Role = Vocabulary.CustomerRole,
            Workshop = "",
            IsActive = true,
            CreatedAt = now,
            LastLoginAt = now,
            PasswordHash = string.Empty,
        };

        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Customer {UserId} signed up", user.Id);

        var result = await IssueTokensAsync(user, ct);

        return Ok(ApiResponse<AuthResultDto>.Ok(
            result, $"Welcome, {user.FullName}. Choose a garage to get started."));
    }

    /// <summary>
    /// Swaps the caller's token for one scoped to a garage they belong to.
    /// </summary>
    /// <remarks>
    /// This is the hinge the whole multi-garage feature turns on. Rather than
    /// teaching sixty endpoints that a customer might belong to several
    /// workshops, selecting one moves the account's cursor and mints a fresh
    /// token carrying that workshop and that customer record. Everything
    /// downstream — bookings, jobs, bills, deliveries — keeps working exactly as
    /// written, because from its point of view nothing has changed.
    ///
    /// The link is checked, not trusted: a token can only ever be scoped to a
    /// garage the person has actually joined.
    /// </remarks>
    [HttpPost("select-workshop")]
    [Authorize(Roles = Vocabulary.CustomerRole)]
    [ProducesResponseType<ApiResponse<AuthResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<AuthResultDto>>> SelectWorkshop(
        SelectWorkshopRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? User.FindFirstValue("sub");

        var user = await db.Users
            .Include(u => u.WorkshopLinks)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null) return Unauthorized(ApiResponse.Failure("Please sign in again."));

        var code = request.CompanyCode.Trim().ToUpperInvariant();
        var link = user.WorkshopLinks.FirstOrDefault(l => l.CompanyCode == code);

        if (link is null)
            return BadRequest(ApiResponse.Failure("You have not joined that garage yet."));

        var workshopName = await db.Workshops
            .Where(w => w.CompanyCode == code)
            .Select(w => w.Name)
            .FirstOrDefaultAsync(ct);

        user.CompanyCode = code;
        user.CustomerId = link.CustomerId;
        user.Workshop = workshopName ?? "";

        // The chosen garage becomes the one the app opens on next time.
        foreach (var other in user.WorkshopLinks) other.IsPrimary = other.Id == link.Id;

        await db.SaveChangesAsync(ct);

        var result = await IssueTokensAsync(user, ct);

        return Ok(ApiResponse<AuthResultDto>.Ok(
            result, $"Now viewing {workshopName ?? code}."));
    }

    /// <summary>
    /// Changes the branch or accounting year this session is looking at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The topbar's two dropdowns used to be pure client state: a value in
    /// localStorage, read from a hardcoded list, that never reached the server.
    /// Switching year changed a label and nothing else — no request, no new
    /// token, no refetch — so the dashboard went on showing exactly the same
    /// rows under a different heading. That is worse than not having the control,
    /// because it looks like it worked.
    /// </para>
    /// <para>
    /// This is the fix, and it is the same token-exchange the garage picker uses:
    /// the selection is validated, persisted on the account, and a fresh token is
    /// minted carrying it. The new token is what makes the change real — the
    /// server answers against it, and the client drops its whole cache because
    /// every cached answer belonged to the previous one.
    /// </para>
    /// <para>
    /// Staff only. A mechanic or customer has no accounting year to choose.
    /// </para>
    /// </remarks>
    [HttpPost("select-workspace")]
    [Authorize(Roles = "Owner,Manager,Advisor")]
    [ProducesResponseType<ApiResponse<AuthResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<AuthResultDto>>> SelectWorkspace(
        SelectWorkspaceRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? User.FindFirstValue("sub");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null) return Unauthorized(ApiResponse.Failure("Please sign in again."));

        var changed = new List<string>();

        if (request.FiscalYear is not null)
        {
            var year = FiscalCalendar.Find(request.FiscalYear, clock);

            // Refused rather than silently replaced with the current year: a
            // client asking for books that do not exist has a bug, and quietly
            // showing it different books would hide that.
            if (year is null)
            {
                return BadRequest(ApiResponse.Failure(
                    $"'{request.FiscalYear}' is not a fiscal year you can select."));
            }

            if (user.FiscalYear != year.Code) changed.Add(year.Code);
            user.FiscalYear = year.Code;
        }

        if (request.BranchId is not null)
        {
            // Empty string clears the selection — distinct from the property
            // being absent, which means "leave it alone".
            if (request.BranchId.Length == 0)
            {
                user.BranchId = null;
            }
            else
            {
                var branch = await db.Branches.AsNoTracking().FirstOrDefaultAsync(
                    b => b.Id == request.BranchId && b.CompanyCode == user.CompanyCode && b.IsActive,
                    ct);

                // Scoped to the caller's own tenant, so a guessed id from another
                // workshop is not a branch this account may point at.
                if (branch is null)
                    return BadRequest(ApiResponse.Failure("That branch is not available."));

                if (user.BranchId != branch.Id) changed.Add(branch.Name);
                user.BranchId = branch.Id;
            }
        }

        await db.SaveChangesAsync(ct);

        var result = await IssueTokensAsync(user, ct);

        return Ok(ApiResponse<AuthResultDto>.Ok(
            result,
            changed.Count == 0
                ? "Workspace unchanged."
                : $"Now viewing {string.Join(" · ", changed)}."));
    }

    /// <summary>
    /// Signs in — or signs up — with a Google ID token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Customers only, and deliberately so. This is the one door a stranger can
    /// walk through unassisted, and a staff login has to be issued by the
    /// workshop whose job data it can see. A Google account proves an email
    /// address, not employment.
    /// </para>
    /// <para>
    /// Matching is on the verified email. Someone who signed up with a password
    /// and later taps the Google button lands in their own account rather than a
    /// duplicate — which is what people expect, and the reason the address has
    /// to be *verified* before it is trusted as a key.
    /// </para>
    /// <para>
    /// No password is set on an account created this way. That is not an
    /// oversight: there is nothing to set it to, and inventing one would create
    /// a credential nobody knows. They sign in with Google, or use the reset
    /// link to choose a password later.
    /// </para>
    /// </remarks>
    [HttpPost("google")]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse<AuthResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<AuthResultDto>>> GoogleSignIn(
        GoogleSignInRequest request, CancellationToken ct)
    {
        if (!google.IsConfigured)
        {
            return BadRequest(ApiResponse.Failure(
                "Google sign-in is not set up on this server."));
        }

        var identity = await google.VerifyAsync(request.IdToken, ct);

        // One message for every failure. Distinguishing "expired" from "wrong
        // audience" tells a prober how far they got.
        if (identity is null)
        {
            return Unauthorized(ApiResponse.Failure(
                "Could not verify that Google account. Please try again."));
        }

        var user = await db.Users
            .Include(u => u.WorkshopLinks)
            .FirstOrDefaultAsync(
                u => u.Email == identity.Email && u.Role == Vocabulary.CustomerRole, ct);

        var now = clock.GetUtcNow().UtcDateTime;

        if (user is null)
        {
            // A staff account on this address must not be silently converted
            // into a customer one, nor signed into without its password.
            var staffClash = await db.Users.AnyAsync(u => u.Email == identity.Email, ct);

            if (staffClash)
            {
                return BadRequest(ApiResponse.Failure(
                    "That email already belongs to a workshop account. Sign in with your password."));
            }

            var existingIds = await db.Users.Select(u => u.Id).ToListAsync(ct);

            user = new User
            {
                Id = Ids.Next(existingIds, "USR"),
                // No garage yet — exactly like a password signup. They land on
                // the directory and choose one.
                CompanyCode = "",
                Email = identity.Email,
                FullName = identity.Name,
                Role = Vocabulary.CustomerRole,
                // Unusable by design: no password was chosen, so none can be
                // guessed. A random hash beats an empty string, which some
                // verifiers treat as "match anything".
                PasswordHash = passwordHasher.HashPassword(new User(), TokenService.CreateSecureToken()),
                IsActive = true,
                CreatedAt = now,
            };

            db.Users.Add(user);
        }

        if (!user.IsActive)
        {
            return Unauthorized(ApiResponse.Failure(
                "This account has been deactivated. Contact your workshop."));
        }

        user.LastLoginAt = now;

        var result = await IssueTokensAsync(user, ct);

        logger.LogInformation("Google sign-in for {Email}", user.Email);

        return Ok(ApiResponse<AuthResultDto>.Ok(
            result,
            user.WorkshopLinks.Count == 0
                ? $"Welcome, {user.FullName}. Choose a garage to get started."
                : $"Welcome back, {user.FullName}."));
    }

    /// <summary>
    /// Sets the signed-in user's profile photo.
    /// </summary>
    /// <remarks>
    /// Always the caller's own — there is no user id in the route, so this
    /// cannot be pointed at somebody else's account. The previous file is
    /// deleted once the new one is safely written, so a failed upload leaves the
    /// old photo in place rather than none.
    /// </remarks>
    [HttpPost("photo")]
    [ProducesResponseType<ApiResponse<AuthUserDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(PhotoStorage.MaxBytes)]
    public async Task<ActionResult<ApiResponse<AuthUserDto>>> UploadPhoto(
        IFormFile file, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return Unauthorized(ApiResponse.Failure("Please sign in again."));

        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse.Failure("Choose a photo to upload."));

        if (file.Length > PhotoStorage.MaxBytes)
        {
            return BadRequest(ApiResponse.Failure(
                "That photo is too large. Please choose one under 4 MB."));
        }

        // Content type checked against an allow-list, and the extension comes
        // from that list rather than the filename the client sent.
        if (!PhotoStorage.IsAllowed(file.ContentType))
        {
            return BadRequest(ApiResponse.Failure(
                $"That file type is not supported. Use one of: {PhotoStorage.AllowedList}."));
        }

        var previous = user.PhotoPath;

        user.PhotoPath = await photos.SaveAvatarAsync(file, user.Id, ct);
        await db.SaveChangesAsync(ct);

        // Only after the row is committed. The other order risks deleting the
        // old file and then failing to save, leaving a user with no photo and a
        // database row pointing at nothing.
        if (previous is not null) photos.Delete(previous);

        return Ok(ApiResponse<AuthUserDto>.Ok(ToDto(user), "Photo updated."));
    }

    /// <summary>Removes the signed-in user's profile photo.</summary>
    [HttpDelete("photo")]
    [ProducesResponseType<ApiResponse<AuthUserDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<AuthUserDto>>> DeletePhoto(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return Unauthorized(ApiResponse.Failure("Please sign in again."));

        var previous = user.PhotoPath;

        user.PhotoPath = null;
        await db.SaveChangesAsync(ct);

        if (previous is not null) photos.Delete(previous);

        return Ok(ApiResponse<AuthUserDto>.Ok(ToDto(user), "Photo removed."));
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
    [AllowAnonymous]
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
    [AllowAnonymous]
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

    /// <summary>Sets a new password using the code from the email.</summary>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse>> ResetPassword(
        ResetPasswordRequest request, CancellationToken ct)
    {
        var user = await FindForResetAsync(request.CompanyCode, request.Email, ct);

        // Re-checked here rather than trusting that verify-reset-code was called:
        // the two are separate requests, and nothing stops a client posting
        // straight to this one.
        var failure = await CheckResetCodeAsync(user, request.Code, ct);

        if (failure is not null) return BadRequest(ApiResponse.Failure(failure));

        user!.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);

        // They chose this one themselves, off a code sent to their own inbox, so
        // there is nothing left to hand over — an account that was mid-handover
        // and went round this way is done.
        user.MustSetPassword = false;

        ClearResetCode(user);

        // Changing a password signs out every other device. Whoever forced the
        // reset is signed out too, if that is what happened.
        await RevokeAllRefreshTokensAsync(user.Id, ct);

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Password reset completed for {Email}", user.Email);

        return Ok(ApiResponse.Success("Your password has been reset. You can now sign in."));
    }

    // ── Reset helpers ────────────────────────────────────────────────────────

    /// <summary>Wrong guesses that burn the code outright.</summary>
    private const int MaxResetAttempts = 5;

    /// <summary>How soon a second code may be sent to the same account.</summary>
    private static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The account a reset request refers to, or null.
    /// </summary>
    /// <remarks>
    /// The same two ways in that <see cref="Login"/> accepts, and for the same
    /// reason: a customer has no company code to give, and neither has the
    /// operator. Without the blank-code branch the reset was unreachable for both
    /// — the endpoint returned its reassuring message and sent nothing.
    /// </remarks>
    private async Task<User?> FindForResetAsync(
        string companyCode, string emailAddress, CancellationToken ct)
    {
        var code = companyCode.Trim().ToUpperInvariant();
        var address = emailAddress.Trim();

        return string.IsNullOrWhiteSpace(code)
            ? await db.Users.FirstOrDefaultAsync(
                u => u.Email == address &&
                     (u.Role == Vocabulary.CustomerRole || u.Role == Vocabulary.SuperAdminRole), ct)
            : await db.Users.FirstOrDefaultAsync(
                u => u.CompanyCode == code && u.Email == address, ct);
    }

    /// <summary>
    /// Null when the code is good, otherwise the sentence to show.
    /// </summary>
    /// <remarks>
    /// Does not spend the code — that is <see cref="ResetPassword"/>'s job, and
    /// verifying has to be repeatable for the two-step client flow to work.
    ///
    /// One message for "no such account", "no code outstanding", "expired" and
    /// "wrong digits". Telling them apart would say which accounts exist and
    /// which have a live reset in progress.
    /// </remarks>
    private async Task<string?> CheckResetCodeAsync(User? user, string code, CancellationToken ct)
    {
        const string bad = "That code is wrong or has expired. Ask for a new one.";

        if (user?.PasswordResetCodeHash is null || user.PasswordResetExpiresAt is null) return bad;

        if (user.PasswordResetExpiresAt < clock.GetUtcNow().UtcDateTime) return bad;

        // A six-digit code is a million guesses, which is an afternoon for a
        // script. Five wrong tries burns it.
        if (user.PasswordResetAttempts >= MaxResetAttempts)
        {
            ClearResetCode(user);
            await db.SaveChangesAsync(ct);

            return "Too many wrong codes. Ask for a new one.";
        }

        var matches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(TokenService.HashToken(code.Trim())),
            Encoding.UTF8.GetBytes(user.PasswordResetCodeHash));

        if (!matches)
        {
            user.PasswordResetAttempts++;
            await db.SaveChangesAsync(ct);

            return bad;
        }

        return null;
    }

    /// <summary>
    /// Retires the outstanding code. Does not save.
    /// </summary>
    /// <remarks>
    /// Leaves <c>PasswordResetSentAt</c> alone on purpose: it is the resend
    /// throttle, not part of the code, and clearing it here would let five wrong
    /// guesses buy an immediate second email.
    /// </remarks>
    private static void ClearResetCode(User user)
    {
        user.PasswordResetCodeHash = null;
        user.PasswordResetExpiresAt = null;
        user.PasswordResetAttempts = 0;
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

        // Their own choice, proved with their own current password.
        user.MustSetPassword = false;

        await RevokeAllRefreshTokensAsync(user.Id, ct);
        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse.Success("Password changed. Please sign in again."));
    }

    /// <summary>
    /// Replaces the one-time password an account was handed, on first sign-in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one thing a half-signed-in session can do. Everything else is refused
    /// by <see cref="MustSetPasswordFilter"/> until this succeeds.
    /// </para>
    /// <para>
    /// Returns a fresh token pair rather than asking them to sign in again. They
    /// have just proved who they are twice over, and a second login screen at
    /// this point is how a first-run flow gets abandoned — the account then sits
    /// on the handed-over password, which is the exact situation this exists to
    /// end.
    /// </para>
    /// <para>
    /// Every other session is revoked on the way through. If the temporary
    /// password had been used elsewhere — or was still sitting in a chat
    /// message — those sessions die here.
    /// </para>
    /// </remarks>
    [HttpPost("set-password")]
    [Authorize]
    [AllowWhileSettingPassword]
    [ProducesResponseType<ApiResponse<AuthResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<AuthResultDto>>> SetPassword(
        SetPasswordRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return Unauthorized(ApiResponse.Failure("Your session is no longer valid. Please sign in again."));

        // Reusing the handed-over password would leave it working, which is the
        // one outcome this whole flow exists to prevent.
        var same = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.NewPassword);

        if (same != PasswordVerificationResult.Failed)
        {
            return BadRequest(ApiResponse.Failure(
                "Choose a password different from the one you were given."));
        }

        user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);
        user.MustSetPassword = false;

        await RevokeAllRefreshTokensAsync(user.Id, ct);
        await db.SaveChangesAsync(ct);

        var result = await IssueTokensAsync(user, ct);

        logger.LogInformation("Account {UserId} set its own password on first sign-in.", user.Id);

        return Ok(ApiResponse<AuthResultDto>.Ok(result, "Password set. You are signed in."));
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
            MustSetPassword = user.MustSetPassword,
        };
    }

    private async Task RevokeAllRefreshTokensAsync(string userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.RevokedAt, now), ct);
    }

    /// <summary>
    /// An instance method rather than static, because the photo URL has to be
    /// absolute and only the request knows the host.
    /// </summary>
    private AuthUserDto ToDto(User user) => new()
    {
        Id = user.Id,
        Email = user.Email,
        Name = user.FullName,
        Role = user.Role,
        Workshop = user.Workshop,
        CompanyCode = user.CompanyCode,
        Phone = user.Phone,
        PhotoUrl = user.PhotoPath is null
            ? null
            : $"{Request.Scheme}://{Request.Host}/{user.PhotoPath}",
        MechanicName = user.MechanicName,
        CustomerId = user.CustomerId,
    };

    /// <summary>
    /// The reset email: the code, large, and a link that opens the dashboard on
    /// the step that asks for it.
    /// </summary>
    /// <remarks>
    /// The link carries the account, never the code. Someone reading this on a
    /// phone types the six digits into the app; someone at a desk clicks through
    /// and finds the fields already filled in.
    /// </remarks>
    private static string ResetEmailBody(string name, string code, int minutes, string link) => $"""
        <div style="font-family:system-ui,-apple-system,Segoe UI,sans-serif;max-width:520px;margin:0 auto;color:#0f172a">
          <h2 style="margin:0 0 12px">Reset your password</h2>
          <p style="margin:0 0 16px">Hi {WebUtility.HtmlEncode(name)},</p>
          <p style="margin:0 0 16px">
            Enter this code in GarageFlow to choose a new password:
          </p>
          <p style="font-size:32px;font-weight:700;letter-spacing:8px;margin:0 0 24px">{code}</p>
          <p style="margin:0 0 24px">
            <a href="{link}" style="display:inline-block;background:#2563eb;color:#fff;text-decoration:none;
               padding:12px 20px;border-radius:8px;font-weight:600">Reset it in the dashboard</a>
          </p>
          <p style="margin:0 0 8px;color:#64748b;font-size:13px">
            The code expires in {minutes} minutes and can be used once.
          </p>
          <p style="margin:0;color:#64748b;font-size:13px">
            If you did not request this, you can ignore this email — your password will not change,
            and nobody can use the code without your mailbox.
          </p>
        </div>
        """;
}
