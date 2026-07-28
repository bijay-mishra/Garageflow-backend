namespace GarageFlow.Api.Services;

/// <summary>
/// Human-readable sequential ids — <c>CUS-009</c>, <c>JOB-1043</c>. This is the
/// server-side twin of <c>nextId()</c> in the dashboard's src/api/client.ts, so
/// records created through either path look the same.
/// </summary>
public static class Ids
{
    /// <summary>
    /// Returns the next id for <paramref name="prefix"/>: the highest numeric
    /// suffix already in use, plus one, zero-padded to <paramref name="pad"/>.
    /// </summary>
    /// <remarks>
    /// Callers pass the full id list, which is fine at workshop scale but is a
    /// read-then-write: two simultaneous creates could race for the same number
    /// and the loser would fail on the primary key. Swap in a SQL sequence if
    /// this ever runs behind more than one process.
    /// </remarks>
    public static string Next(IEnumerable<string> existingIds, string prefix, int pad = 3)
    {
        var highest = 0;
        foreach (var id in existingIds)
        {
            var digits = new string(id.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var n) && n > highest) highest = n;
        }

        return $"{prefix}-{(highest + 1).ToString().PadLeft(pad, '0')}";
    }
}
