namespace GarageFlow.Api.Domain;

/// <summary>
/// One person's membership of one garage.
/// </summary>
/// <remarks>
/// This is what turns a single-workshop product into one a customer can use
/// across several. Before it, <c>User.CompanyCode</c> and <c>User.CustomerId</c>
/// bound an account to exactly one garage — which is right for staff and wrong
/// for a customer, who may service one car at the place near work and another
/// near home.
///
/// A row here says: this account is customer <c>CustomerId</c> at workshop
/// <c>CompanyCode</c>. Each garage still keeps its own customer record, with its
/// own vehicles, jobs and invoices — nothing is shared between them, which is
/// what a garage expects of its own books. The person is simply the same person.
///
/// Staff never get a row here. A mechanic belongs to the shop that employs them,
/// and <c>User.CompanyCode</c> continues to say so.
/// </remarks>
public class UserWorkshopLink
{
    public int Id { get; set; }

    public string UserId { get; set; } = default!;
    public User? User { get; set; }

    /// <summary>The garage. Matches <see cref="Workshop.CompanyCode"/>.</summary>
    public string CompanyCode { get; set; } = default!;

    /// <summary>The customer record this person is, inside that garage.</summary>
    public string CustomerId { get; set; } = default!;
    public Customer? Customer { get; set; }

    /// <summary>
    /// The garage the app opens on. Exactly one link per user should carry it.
    /// </summary>
    /// <remarks>
    /// A convenience, not a permission: switching garages is a token exchange
    /// that checks the link exists, and this only decides which one is offered
    /// first. It is the "favourite" in product terms.
    /// </remarks>
    public bool IsPrimary { get; set; }

    public DateTime JoinedAt { get; set; }
}
