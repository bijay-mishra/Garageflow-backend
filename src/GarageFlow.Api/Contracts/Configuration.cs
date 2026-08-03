using System.ComponentModel.DataAnnotations;

namespace GarageFlow.Api.Contracts;

// ── Configuration ────────────────────────────────────────────────────────────
// The lists a workshop keeps about itself: its accounting years and its
// locations. Same shapes whether the caller is the workshop's own owner or a
// platform operator working on their behalf — one screen, one set of rules.

public record FiscalYearRecordDto
{
    public required int Id { get; init; }

    /// <summary>How it is written and spoken: <c>2082/83</c>.</summary>
    public required string Code { get; init; }

    public required DateOnly Start { get; init; }
    public required DateOnly End { get; init; }

    /// <summary>Closed years can be read but nothing new lands in them.</summary>
    public required bool IsClosed { get; init; }

    /// <summary>True for the year today falls in.</summary>
    public required bool IsCurrent { get; init; }

    /// <summary>How much is already filed under it — what a delete would orphan.</summary>
    public required int InvoiceCount { get; init; }
    public required int JobCount { get; init; }
}

public class SaveFiscalYearRequest
{
    [Required, StringLength(20)]
    public string Code { get; set; } = "";

    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }

    public bool IsClosed { get; set; }
}

public record BranchDetailDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Address { get; init; }
    public required string Phone { get; init; }
    public required bool IsDefault { get; init; }
    public required bool IsActive { get; init; }
    public required DateTime CreatedAt { get; init; }
}

public class SaveBranchRequest
{
    [Required, StringLength(160, MinimumLength = 2)]
    public string Name { get; set; } = "";

    [StringLength(300)] public string Address { get; set; } = "";
    [StringLength(40)] public string Phone { get; set; } = "";

    /// <summary>
    /// Makes this the branch new sessions open on. The previous default gives it
    /// up — a company with two defaults has none.
    /// </summary>
    public bool IsDefault { get; set; }

    public bool IsActive { get; set; } = true;
}
