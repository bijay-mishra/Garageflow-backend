namespace GarageFlow.Api.Domain;

/// <summary>
/// A physical location of the workshop.
/// </summary>
/// <remarks>
/// Branches exist as records the session can point at, and the selection travels
/// in the token. What they do *not* yet do is partition data: no customer, job
/// card or invoice carries a branch, so switching branch changes what the server
/// is told rather than what it returns.
///
/// That is a deliberate half-step, not an oversight. Putting a branch on every
/// core table is a migration across the whole schema plus a branch picker on
/// every create form, and it is worth doing once the workshop actually runs two
/// sites rather than in anticipation of it. The plumbing being real now means
/// that change is additive — the token, the session and the refetch already work.
/// </remarks>
public class Branch
{
    /// <summary>Looks like <c>BR-001</c>.</summary>
    public string Id { get; set; } = default!;

    /// <summary>Tenant this branch belongs to.</summary>
    public string CompanyCode { get; set; } = default!;

    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";

    /// <summary>
    /// The branch a new session opens on, and the fallback when a selection can
    /// no longer be honoured — a plan downgrade, or a branch that was closed.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>A closed branch keeps its history but cannot be selected.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A fiscal year as one company keeps it.
/// </summary>
/// <remarks>
/// <para>
/// The national calendar in <see cref="FiscalCalendar"/> is the source of the
/// dates — Shrawan 1 is published, not chosen — and every company's list is
/// seeded from it. What a company owns is which years it actually uses: the one
/// it opened its books in, the ones it has since closed, and next year when it
/// wants to start recording against it early.
/// </para>
/// <para>
/// The window is editable, which looks odd for a national calendar and is there
/// for one real case: a workshop whose accountant runs its books to a slightly
/// different cut-off than the day the government changed the year. Changing it
/// moves which side of a year a bill falls on, so the screen says so.
/// </para>
/// </remarks>
public class FiscalYearRecord : ITenantOwned
{
    public int Id { get; set; }

    public string CompanyCode { get; set; } = default!;

    /// <summary>How it is written and spoken: <c>2082/83</c>.</summary>
    public string Code { get; set; } = default!;

    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }

    /// <summary>
    /// A closed year can still be read but nothing new lands in it.
    /// </summary>
    /// <remarks>
    /// Kept as a flag rather than inferred from the dates: books are closed when
    /// the accountant says so, which is usually months after the year ended.
    /// </remarks>
    public bool IsClosed { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// One Nepali accounting year, and the Gregorian dates it spans.
/// </summary>
/// <remarks>
/// The workshop's books run on the Bikram Sambat year — 2082/83, not 2025/26 —
/// while every date in this database is Gregorian. This type is the join between
/// the two, so the rest of the code can filter on a plain date range and never
/// has to know about BS at all.
/// </remarks>
/// <param name="Code">How it is written and spoken: <c>2082/83</c>.</param>
/// <param name="Start">First day, inclusive.</param>
/// <param name="End">Last day, inclusive.</param>
public record FiscalYear(string Code, DateOnly Start, DateOnly End)
{
    public bool Contains(DateOnly date) => date >= Start && date <= End;

    public bool Contains(DateTime moment) => Contains(DateOnly.FromDateTime(moment));
}

/// <summary>
/// The fiscal years this product knows about.
/// </summary>
/// <remarks>
/// <para>
/// A Nepali fiscal year begins on Shrawan 1 and ends on Ashadh's last day. In
/// Gregorian terms that is mid-July to mid-July, and BS year <c>Y</c> starts in
/// AD year <c>Y - 57</c>.
/// </para>
/// <para>
/// Shrawan 1 is <b>not</b> a fixed Gregorian date. It falls on 16 or 17 July
/// depending on the year, because BS month lengths vary and are published
/// annually rather than derived from a formula. This class therefore holds a
/// table of real dates rather than arithmetic.
/// </para>
/// <para>
/// The table was generated from <c>nepali-date-converter</c>, the same library
/// the dashboard uses to convert dates for display, so the two agree by
/// construction. An earlier version of this file assumed 16 July every year;
/// that was wrong for 2079, 2080, 2082, 2083, 2084, 2086, 2087 and 2088, which
/// start on the 17th — a day of takings landing in the wrong year's books.
/// </para>
/// </remarks>
public static class FiscalCalendar
{
    /// <summary>
    /// Shrawan 1 for each BS year, in Gregorian.
    /// </summary>
    /// <remarks>
    /// Extending this is a deliberate act: add the real published date, do not
    /// interpolate. The range is what the app offers in its dropdown, and a year
    /// outside it is refused rather than guessed — see <see cref="Find"/>.
    /// </remarks>
    private static readonly Dictionary<int, DateOnly> ShrawanFirst = new()
    {
        [2075] = new DateOnly(2018, 7, 17),
        [2076] = new DateOnly(2019, 7, 17),
        [2077] = new DateOnly(2020, 7, 16),
        [2078] = new DateOnly(2021, 7, 16),
        [2079] = new DateOnly(2022, 7, 17),
        [2080] = new DateOnly(2023, 7, 17),
        [2081] = new DateOnly(2024, 7, 16),
        [2082] = new DateOnly(2025, 7, 17),
        [2083] = new DateOnly(2026, 7, 17),
        [2084] = new DateOnly(2027, 7, 17),
        [2085] = new DateOnly(2028, 7, 16),
        [2086] = new DateOnly(2029, 7, 17),
        [2087] = new DateOnly(2030, 7, 17),
        [2088] = new DateOnly(2031, 7, 17),
        [2089] = new DateOnly(2032, 7, 16),
    };

    /// <summary>Earliest year offered, so the dropdown is not endless.</summary>
    private const int FirstBsYear = 2078;

    /// <summary>How far ahead to offer, for a shop opening next year's books.</summary>
    private const int YearsAhead = 1;

    /// <summary>Newest year the table can describe a full window for.</summary>
    private static int LastBsYear => ShrawanFirst.Keys.Max() - 1;

    /// <summary>The year a given date falls in.</summary>
    /// <remarks>
    /// A scan rather than arithmetic, because the boundary moves. Fifteen
    /// comparisons on a table this size is not worth optimising, and the
    /// alternative is the off-by-one this replaced.
    /// </remarks>
    public static FiscalYear For(DateOnly date)
    {
        for (var year = LastBsYear; year >= ShrawanFirst.Keys.Min(); year--)
        {
            if (date >= ShrawanFirst[year]) return Build(year);
        }

        // Older than the table. Clamped to the earliest year we can describe
        // rather than throwing: a date this old is seed data or a typo, and
        // neither deserves a 500.
        return Build(ShrawanFirst.Keys.Min());
    }

    public static FiscalYear Current(TimeProvider clock) =>
        For(DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));

    /// <summary>Every year a user may select, oldest first.</summary>
    public static IReadOnlyList<FiscalYear> All(TimeProvider clock)
    {
        // Capped at what the table can describe. Offering a year whose start
        // date is a guess would be worse than not offering it.
        var lastBsYear = Math.Min(
            int.Parse(Current(clock).Code[..4]) + YearsAhead,
            LastBsYear);

        return Enumerable.Range(FirstBsYear, lastBsYear - FirstBsYear + 1)
            .Select(Build)
            .ToList();
    }

    /// <summary>
    /// Looks up a year by its code, or null when the code is not one we offer.
    /// </summary>
    /// <remarks>
    /// Null is the answer a caller acts on: a selection that cannot be resolved
    /// must be refused rather than quietly replaced, or a mistyped year would
    /// silently show the wrong books.
    /// </remarks>
    public static FiscalYear? Find(string? code, TimeProvider clock) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : All(clock).FirstOrDefault(y => y.Code == code.Trim());

    /// <summary>Builds "2082/83" and its Gregorian window from a BS start year.</summary>
    private static FiscalYear Build(int bsStartYear)
    {
        var start = ShrawanFirst[bsStartYear];

        // One day short of the next year's opening, so consecutive years meet
        // exactly and no date belongs to both or neither. Read from the table
        // rather than adding 365, because the years are not all the same length.
        var end = ShrawanFirst.TryGetValue(bsStartYear + 1, out var next)
            ? next.AddDays(-1)
            : start.AddYears(1).AddDays(-1);

        return new FiscalYear(
            // The second half is two digits, as it is always written.
            $"{bsStartYear}/{(bsStartYear + 1) % 100:D2}",
            start,
            end);
    }
}
