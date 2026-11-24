using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services.Iof;

/// <summary>
/// A runner's <c>&lt;SplitTime&gt;</c> list, read off the splits view the discipline built — nothing is recomputed
/// (plan §3.5). Times are seconds since the start (for a relay member, the leg start).
/// </summary>
internal static class IofSplitList
{
    /// <summary>
    /// A prescribed order (an ordered view of a format without points — set course, scatter, mixed, relay legs): one
    /// split per course control (disabled ones left out), OK with its time or <c>Missing</c>, then every off-course
    /// punch as <c>Additional</c> in chip order. Each on-course punch takes its course position (<c>CourseIndex</c>,
    /// else the next column of that code), so a course visiting a control twice keeps both. A free order (points, count
    /// of controls, rogaine — whose ordered view lists the whole catalogue): every punch in order taken, OK when it
    /// counted, else <c>Additional</c>; never <c>Missing</c>. No splits view — no splits.
    /// </summary>
    public static IReadOnlyList<IofSplit> From(SplitsView? splits, bool usesScore)
    {
        if (splits is null)
            return [];
        var punches = splits.Passage
            .Where(p => p.Kind == PassageKind.Control && p.Code.Trim().Length > 0)
            .ToList();

        if (splits.Layout != SplitsLayout.Ordered || usesScore)
            return punches
                .Select(p => new IofSplit(p.Code.Trim(), Seconds(p.Elapsed), p.OnCourse ? IofSplitStatus.OK : IofSplitStatus.Additional))
                .ToList();

        var columns = splits.Expected.Where(e => !e.Ignored).Select(e => e.Code.Trim()).ToList();
        var taken = new bool[columns.Count];
        var times = new int?[columns.Count];
        var extras = new List<IofSplit>();
        var next = 0;
        foreach (var punch in punches)
        {
            var code = punch.Code.Trim();
            var column = punch.OnCourse ? ColumnOf(punch, code, columns, next) : -1;
            if (column >= 0 && !taken[column])
            {
                taken[column] = true;
                times[column] = Seconds(punch.Elapsed);
                next = column + 1;
                continue;
            }
            extras.Add(new IofSplit(code, Seconds(punch.Elapsed), IofSplitStatus.Additional));
        }

        var result = new List<IofSplit>(columns.Count + extras.Count);
        for (var i = 0; i < columns.Count; i++)
            result.Add(taken[i]
                ? new IofSplit(columns[i], times[i], IofSplitStatus.OK)
                : new IofSplit(columns[i], null, IofSplitStatus.Missing));
        result.AddRange(extras);
        return result;
    }

    // The course column an on-course punch took: its course position when the view gives one, else the next column of
    // that code from where the last on-course punch left off (the HTML splits' rule); -1 when none.
    private static int ColumnOf(PassagePunch punch, string code, IReadOnlyList<string> columns, int next)
    {
        if (punch.CourseIndex is { } index && index >= 0 && index < columns.Count)
            return index;
        for (var j = next; j < columns.Count; j++)
            if (string.Equals(columns[j], code, StringComparison.OrdinalIgnoreCase))
                return j;
        return -1;
    }

    private static int? Seconds(TimeSpan? elapsed) =>
        elapsed is { } e && e >= TimeSpan.Zero ? IofClock.Seconds(e) : null;
}
