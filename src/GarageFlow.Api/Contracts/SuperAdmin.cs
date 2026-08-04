using System.ComponentModel.DataAnnotations;

namespace GarageFlow.Api.Contracts;

// ── Superadmin console ───────────────────────────────────────────────────────
// The shapes the operator's console works in. Everything here crosses company
// boundaries by design, which is why it lives in its own file rather than
// beside the tenant-scoped contracts.

/// <summary>A company, as the console lists it.</summary>
public record CompanyDto
{
    public required string CompanyCode { get; init; }
    public required string Name { get; init; }
    public required string LegalName { get; init; }
    public required string Phone { get; init; }
    public required string Email { get; init; }
    public required string Address { get; init; }

    /// <summary>
    /// Absolute URL of the company's logo, or null if it has not set one.
    /// </summary>
    /// <remarks>
    /// The console shows it beside the name for the same reason the list carries
    /// counts: a page of forty company codes is faster to read by mark than by
    /// word, and the operator is the one person who sees them all at once.
    /// </remarks>
    public required string? LogoUrl { get; init; }

    /// <summary>False means nobody there can sign in.</summary>
    public required bool IsActive { get; init; }

    /// <summary>Whether it appears in the customer app's garage directory.</summary>
    public required bool IsListed { get; init; }

    public required string[] EnabledModules { get; init; }

    // Enough to tell a live company from a dormant one at a glance, without
    // opening it.
    public required int UserCount { get; init; }
    public required int CustomerCount { get; init; }
    public required int JobCount { get; init; }

    public required DateTime CreatedAt { get; init; }

    /// <summary>The most recent sign-in by anyone there. Null if never.</summary>
    public required DateTime? LastActiveAt { get; init; }
}

/// <summary>
/// A company, plus the one-time password its owner signs in with.
/// </summary>
/// <remarks>
/// The password appears in this response and nowhere else, ever — only its hash
/// is stored. That is deliberate: an operator who can look it up later is an
/// operator who can sign in as the owner later, which is the whole thing this
/// design is trying to stop. If the console loses it before it is handed over,
/// the answer is to reset it and hand over a new one.
/// </remarks>
public record CompanyCreatedDto
{
    public required CompanyDto Company { get; init; }

    /// <summary>Read this to the owner. It stops working once they replace it.</summary>
    public required string OneTimePassword { get; init; }
}

/// <summary>Creates a company and the owner who will run it.</summary>
public class CreateCompanyRequest
{
    /// <summary>What staff type at sign-in. Letters, numbers and hyphens.</summary>
    [Required, StringLength(40, MinimumLength = 2)]
    public string CompanyCode { get; set; } = "";

    [Required, StringLength(160, MinimumLength = 2)]
    public string Name { get; set; } = "";

    [StringLength(200)] public string? LegalName { get; set; }
    [StringLength(300)] public string? Address { get; set; }
    [StringLength(40)] public string? Phone { get; set; }
    [EmailAddress, StringLength(160)] public string? Email { get; set; }

    // ── The first owner ──────────────────────────────────────────────────────

    [Required, StringLength(160, MinimumLength = 2)]
    public string OwnerName { get; set; } = "";

    [Required, EmailAddress, StringLength(160)]
    public string OwnerEmail { get; set; } = "";

    [StringLength(40)] public string? OwnerPhone { get; set; }

    /// <summary>
    /// A one-time password to hand over. Leave blank for a generated one.
    /// </summary>
    /// <remarks>
    /// Either way the owner is made to replace it at first sign-in, so this is
    /// only ever the thing that gets read down a phone. Blank is the better
    /// choice: a person inventing a password for somebody else invents a
    /// memorable one, and a memorable one used across every company an operator
    /// sets up is a master key.
    /// </remarks>
    [StringLength(200, MinimumLength = 8,
        ErrorMessage = "Password must be at least 8 characters.")]
    public string? OwnerPassword { get; set; }

    /// <summary>Omit for the default set. Unknown names are ignored.</summary>
    public string[]? EnabledModules { get; set; }
}

/// <summary>Changes a company's modules, name, or whether it is suspended.</summary>
public class UpdateCompanyRequest
{
    /// <summary>Null leaves them alone; an empty array removes every module.</summary>
    public string[]? EnabledModules { get; set; }

    public bool? IsActive { get; set; }

    // Every field is optional and null means "leave it alone", matching the
    // workshop settings screen. The company code is deliberately absent: it is
    // what staff type at sign-in, and changing it would lock them all out.
    [StringLength(160)] public string? Name { get; set; }
    [StringLength(200)] public string? LegalName { get; set; }
    [StringLength(300)] public string? Address { get; set; }
    [StringLength(40)] public string? Phone { get; set; }
    [StringLength(160)] public string? Email { get; set; }
}

/// <summary>Deleting a company and everything in it.</summary>
public class DeleteCompanyRequest
{
    /// <summary>
    /// The company code, typed again.
    /// </summary>
    /// <remarks>
    /// This erases a real business's customers, vehicles, job cards and bills.
    /// Typing the code is the one confirmation that cannot happen by misclicking,
    /// which is why it is required rather than a yes/no dialog.
    /// </remarks>
    [Required]
    public string ConfirmCompanyCode { get; set; } = "";
}

/// <summary>Entering a company as its owner.</summary>
public class ImpersonateRequest
{
    /// <summary>
    /// Optional note for the audit trail. Not required on purpose — demanding a
    /// reason every time trains people to type "support" and records nothing.
    /// </summary>
    [StringLength(300)]
    public string? Reason { get; set; }
}

/// <summary>One recorded impersonation.</summary>
public record ImpersonationDto
{
    public required string UserEmail { get; init; }
    public required string CompanyCode { get; init; }
    public required DateTime At { get; init; }
    public required string Reason { get; init; }
}
