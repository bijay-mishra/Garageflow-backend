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
    /// <remarks>
    /// Null answers two different questions with one value: "the caller is
    /// staff" and "the caller is a customer with no record here". That is safe
    /// only where null is treated as *deny* — see <see cref="CustomerScopeAsync"/>
    /// for the read paths, where treating null as "no filter" would hand one
    /// caller everybody's data.
    /// </remarks>
    public async Task<string?> CustomerIdAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var user = await GetAsync(principal, ct);

        return user is { Role: Vocabulary.CustomerRole } && !string.IsNullOrWhiteSpace(user.CustomerId)
            ? user.CustomerId
            : null;
    }

    /// <summary>
    /// How much customer-scoped data this caller may read.
    /// </summary>
    /// <remarks>
    /// Exists because <see cref="CustomerIdAsync"/> cannot tell "staff" apart
    /// from "customer who has joined no garage", and the two must not be handled
    /// the same way. Every list endpoint used to read:
    ///
    /// <code>
    /// var customerId = await currentUser.CustomerIdAsync(User, ct);
    /// if (customerId is not null) rows = rows.Where(r => r.CustomerId == customerId);
    /// </code>
    ///
    /// which was correct while every customer account was created by a workshop
    /// and therefore always had a record. Open sign-up broke that: a stranger can
    /// now hold a Customer token with no <c>CustomerId</c>, fall through the
    /// <c>is not null</c> check, and read every row in the workshop — bookings,
    /// invoices, handovers, other people's addresses.
    ///
    /// Naming the three states makes the unsafe case impossible to write by
    /// accident: only <see cref="CustomerScope.Staff"/> means unfiltered.
    /// </remarks>
    public async Task<CustomerScope> CustomerScopeAsync(
        ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var user = await GetAsync(principal, ct);

        if (user is not { Role: Vocabulary.CustomerRole }) return CustomerScope.Staff;

        return string.IsNullOrWhiteSpace(user.CustomerId)
            ? CustomerScope.Homeless
            : CustomerScope.For(user.CustomerId);
    }
}

/// <summary>
/// Which customer-scoped rows a caller may see: all of them, one customer's, or
/// none.
/// </summary>
/// <param name="CustomerId">
/// The single customer to filter on, or null for the other two cases — so it
/// must never be used without checking <see cref="SeesNothing"/> first.
/// </param>
/// <param name="SeesNothing">
/// True for a customer account with no record in the garage the token points at.
/// A signed-up customer who has joined nowhere is in this state, and the honest
/// answer to every "list my …" is an empty one.
/// </param>
public readonly record struct CustomerScope(string? CustomerId, bool SeesNothing)
{
    /// <summary>A staff caller: no filter, every row.</summary>
    public static readonly CustomerScope Staff = new(null, false);

    /// <summary>A customer with no record here.</summary>
    public static readonly CustomerScope Homeless = new(null, true);

    public static CustomerScope For(string customerId) => new(customerId, false);

    /// <summary>True when this caller reads the whole workshop.</summary>
    public bool IsStaff => CustomerId is null && !SeesNothing;
}
