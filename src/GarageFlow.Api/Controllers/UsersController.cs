using GarageFlow.Api.Contracts;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using GarageFlow.Api.Mapping;
using GarageFlow.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Controllers;

/// <summary>
/// Account management for the workshop: creating the mechanic and customer
/// logins that the mobile app signs in with.
/// </summary>
/// <remarks>
/// There is no public sign-up anywhere in this API. Accounts exist because
/// someone at the workshop created them, which is how a garage actually
/// onboards people and means a stranger cannot mint a login that sees job data.
///
/// Owners and Managers only — an Advisor can run the front desk without being
/// able to hand out credentials.
/// </remarks>
[Authorize(Roles = "Owner,Manager")]
[ApiController]
[Route("api/users")]
[Produces("application/json")]
public class UsersController(
    GarageFlowDbContext db,
    IPasswordHasher<User> passwordHasher,
    CurrentUserService currentUser,
    TimeProvider clock) : ControllerBase
{
    /// <summary>Lists accounts in the caller's workshop.</summary>
    [HttpGet]
    [ProducesResponseType<ApiResponse<PagedList<UserDto>>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<PagedList<UserDto>>>> List(
        [FromQuery] TableQuery query,
        [FromQuery] string? role,
        CancellationToken ct)
    {
        var me = await currentUser.GetAsync(User, ct);
        if (me is null) return Forbid();

        // Scoped to the caller's tenant: an owner of one workshop has no
        // business listing another's staff.
        var users = db.Users.AsNoTracking().Where(u => u.CompanyCode == me.CompanyCode);

        if (!string.IsNullOrWhiteSpace(role))
            users = users.Where(u => u.Role == role);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            users = users.Where(u =>
                EF.Functions.Like(u.FullName, $"%{term}%") ||
                EF.Functions.Like(u.Email, $"%{term}%") ||
                EF.Functions.Like(u.MechanicName!, $"%{term}%"));
        }

        var projected = users.ToDto().OrderByProperty(query.SortBy, query.Descending);

        if (string.IsNullOrWhiteSpace(query.SortBy))
            projected = projected.OrderBy(u => u.Role).ThenBy(u => u.Name);

        var page = await projected.ToPagedListAsync(query, ct);

        return Ok(ApiResponse<PagedList<UserDto>>.Ok(page, $"{page.Count} account(s)."));
    }

    /// <summary>One account.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType<ApiResponse<UserDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<UserDto>>> Get(string id, CancellationToken ct)
    {
        var me = await currentUser.GetAsync(User, ct);
        if (me is null) return Forbid();

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == id && u.CompanyCode == me.CompanyCode)
            .ToDto()
            .FirstOrDefaultAsync(ct);

        if (user is null)
            return NotFound(ApiResponse.Failure($"Account '{id}' was not found."));

        return Ok(ApiResponse<UserDto>.Ok(user, "Account loaded."));
    }

    /// <summary>Creates an account — staff, mechanic or customer.</summary>
    [HttpPost]
    [ProducesResponseType<ApiResponse<UserDto>>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<UserDto>>> Create(CreateUserRequest request, CancellationToken ct)
    {
        var me = await currentUser.GetAsync(User, ct);
        if (me is null) return Forbid();

        var email = request.Email.Trim();

        if (await db.Users.AnyAsync(u => u.CompanyCode == me.CompanyCode && u.Email == email, ct))
            return BadRequest(ApiResponse.Failure($"An account with the email {email} already exists."));

        if (Validate(request.Role, request.MechanicName, request.CustomerId) is { } problem)
            return BadRequest(ApiResponse.Failure(problem));

        if (request.Role == Vocabulary.CustomerRole)
        {
            var exists = await db.Customers.AnyAsync(c => c.Id == request.CustomerId, ct);
            if (!exists)
                return BadRequest(ApiResponse.Failure($"Customer '{request.CustomerId}' does not exist."));
        }

        var user = new User
        {
            Id = Ids.Next(await db.Users.Select(u => u.Id).ToListAsync(ct), "USR"),
            CompanyCode = me.CompanyCode,
            Email = email,
            FullName = request.Name.Trim(),
            Phone = request.Phone?.Trim(),
            Role = request.Role,
            Workshop = me.Workshop,
            IsActive = true,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
            MechanicName = request.Role == Vocabulary.MechanicRole ? request.MechanicName!.Trim() : null,
            CustomerId = request.Role == Vocabulary.CustomerRole ? request.CustomerId : null,
            PasswordHash = string.Empty,
        };

        // Hashed after construction: the hasher salts per user, so it needs the
        // instance it is hashing for.
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        var dto = await db.Users.AsNoTracking().Where(u => u.Id == user.Id).ToDto().FirstAsync(ct);

        return CreatedAtAction(
            nameof(Get),
            new { id = user.Id },
            ApiResponse<UserDto>.Ok(dto, $"Account for {user.FullName} created."));
    }

    /// <summary>Updates an account. Only the fields present in the body are applied.</summary>
    [HttpPut("{id}")]
    [ProducesResponseType<ApiResponse<UserDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<UserDto>>> Update(
        string id, UpdateUserRequest request, CancellationToken ct)
    {
        var me = await currentUser.GetAsync(User, ct);
        if (me is null) return Forbid();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.CompanyCode == me.CompanyCode, ct);

        if (user is null)
            return NotFound(ApiResponse.Failure($"Account '{id}' was not found."));

        // Locking yourself out is not a recoverable mistake from inside the app,
        // so it is refused rather than confirmed.
        if (user.Id == me.Id && request.IsActive is false)
            return BadRequest(ApiResponse.Failure("You cannot deactivate your own account."));

        if (user.Id == me.Id && request.Role is not null && request.Role != user.Role)
            return BadRequest(ApiResponse.Failure("You cannot change your own role."));

        var role = request.Role ?? user.Role;
        var mechanicName = request.MechanicName ?? user.MechanicName;
        var customerId = request.CustomerId ?? user.CustomerId;

        if (Validate(role, mechanicName, customerId) is { } problem)
            return BadRequest(ApiResponse.Failure(problem));

        if (role == Vocabulary.CustomerRole && customerId != user.CustomerId)
        {
            if (!await db.Customers.AnyAsync(c => c.Id == customerId, ct))
                return BadRequest(ApiResponse.Failure($"Customer '{customerId}' does not exist."));
        }

        if (request.Name is not null) user.FullName = request.Name.Trim();
        if (request.Phone is not null) user.Phone = request.Phone.Trim();
        if (request.IsActive is { } active) user.IsActive = active;

        user.Role = role;

        // The two links are mutually exclusive, and changing role has to clear
        // the one that no longer applies — a former mechanic keeping their name
        // would go on claiming job cards.
        user.MechanicName = role == Vocabulary.MechanicRole ? mechanicName?.Trim() : null;
        user.CustomerId = role == Vocabulary.CustomerRole ? customerId : null;

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

            // A password reset by staff ends every session that account has open,
            // which is the point of resetting it.
            await db.RefreshTokens
                .Where(t => t.UserId == user.Id && t.RevokedAt == null)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(t => t.RevokedAt, clock.GetUtcNow().UtcDateTime), ct);
        }

        await db.SaveChangesAsync(ct);

        var dto = await db.Users.AsNoTracking().Where(u => u.Id == id).ToDto().FirstAsync(ct);

        return Ok(ApiResponse<UserDto>.Ok(dto, $"Account for {user.FullName} updated."));
    }

    /// <summary>
    /// Deactivates an account. Accounts are never deleted — their notifications
    /// and the history of who did what stay attached to a real row.
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse>> Deactivate(string id, CancellationToken ct)
    {
        var me = await currentUser.GetAsync(User, ct);
        if (me is null) return Forbid();

        if (id == me.Id)
            return BadRequest(ApiResponse.Failure("You cannot deactivate your own account."));

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.CompanyCode == me.CompanyCode, ct);

        if (user is null)
            return NotFound(ApiResponse.Failure($"Account '{id}' was not found."));

        user.IsActive = false;

        await db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAt, clock.GetUtcNow().UtcDateTime), ct);

        await db.SaveChangesAsync(ct);

        return Ok(ApiResponse.Success($"Account for {user.FullName} deactivated."));
    }

    /// <summary>
    /// Checks that a role has the link it needs. Returns the problem, or null
    /// when the combination is coherent.
    /// </summary>
    private static string? Validate(string role, string? mechanicName, string? customerId) => role switch
    {
        Vocabulary.MechanicRole when string.IsNullOrWhiteSpace(mechanicName) =>
            "A mechanic account needs the name they are assigned under on job cards.",
        Vocabulary.CustomerRole when string.IsNullOrWhiteSpace(customerId) =>
            "A customer account needs the customer it belongs to.",
        _ => null,
    };
}
