namespace GarageFlow.Api.Domain;

/// <summary>
/// A record of the superadmin entering a company.
/// </summary>
/// <remarks>
/// Impersonation is the most powerful thing this product can do: one person
/// gains full access to a real business's books, and the workshop cannot see it
/// happening. That is defensible only if it leaves a trail nobody can quietly
/// skip — so the row is written in the same transaction that mints the token,
/// and there is no code path that grants one without it.
///
/// Append-only by convention. Nothing in the app updates or deletes these.
/// </remarks>
public class ImpersonationLog
{
    public int Id { get; set; }

    /// <summary>The superadmin who did it.</summary>
    public string UserId { get; set; } = default!;
    public string UserEmail { get; set; } = "";

    /// <summary>The company they entered.</summary>
    public string CompanyCode { get; set; } = default!;

    public DateTime At { get; set; }

    /// <summary>
    /// Why, if they said. Optional — demanding a reason for every visit trains
    /// people to type "support" and means nothing.
    /// </summary>
    public string Reason { get; set; } = "";
}
