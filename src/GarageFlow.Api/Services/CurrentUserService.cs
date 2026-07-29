using System.Security.Claims;
using GarageFlow.Api.Data;
using GarageFlow.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace GarageFlow.Api.Services;

/// <summary>
/// Resolves the signed-in <see cref="User"/> row for the current request.
/// </summary>
/// <remarks>
/// The JWT carries the role, but not the mechanic name or customer id that the
/// mobile endpoints filter on — and deliberately so. Those links can be changed
/// by staff at any moment, and a token minted beforehand would keep asserting
/// the old value until it expired. A mechanic reading another mechanic's jobs,
/// or a customer reading someone else's, is exactly the bug that would cause.
///
/// So the link is read from the database on the requests that depend on it. It
/// is one indexed lookup by primary key, cached for the lifetime of the request,
/// and the authority is always the current row rather than a snapshot.
/// </remarks>
public class CurrentUserService(GarageFlowDbContext db)
{
    private User? _cached;

    /// <summary>The <c>sub</c> claim — present on every authenticated request.</summary>
    public static string? IdOf(ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>
    /// Loads the current user, or null when the token names an account that has
    /// since been deleted or deactivated.
    /// </summary>
    public async Task<User?> GetAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;

        var id = IdOf(principal);
        if (string.IsNullOrEmpty(id)) return null;

        _cached = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.IsActive, ct);
        return _cached;
    }

    /// <summary>
    /// The mechanic name this request may act as, or null if the caller is not
    /// an active mechanic account with a name assigned.
    /// </summary>
    public async Task<string?> MechanicNameAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var user = await GetAsync(principal, ct);

        return user is { Role: Vocabulary.MechanicRole } && !string.IsNullOrWhiteSpace(user.MechanicName)
            ? user.MechanicName
            : null;
    }

    /// <summary>
    /// The customer this request may read, or null if the caller is not an
    /// active customer account linked to a customer record.
    /// </summary>
    public async Task<string?> CustomerIdAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var user = await GetAsync(principal, ct);

        return user is { Role: Vocabulary.CustomerRole } && !string.IsNullOrWhiteSpace(user.CustomerId)
            ? user.CustomerId
            : null;
    }
}
