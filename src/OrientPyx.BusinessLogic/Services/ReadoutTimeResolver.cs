using System.Globalization;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Shared time-of-day → timestamp logic for readout parsers. Readout files record a bare time of day
/// (and sometimes a weekday) per station, not a full timestamp, so a course that crosses midnight would
/// otherwise mis-date. This resolver dates each time by:
/// <list type="number">
///   <item>the recorded weekday relative to the read-out day's weekday, when known — the primary rule; or</item>
///   <item>a monotonic fallback: place it on the read-out day, then step a day back while it reads later
///   than the next-known-later event (the anchor), so the sequence stays ordered across midnight.</item>
/// </list>
/// Each parser supplies its own weekday parser (SPORTident uses English two-letter codes, Sport Time a
/// Ukrainian code in parentheses), so this type takes an already-parsed <see cref="DayOfWeek"/>.
/// </summary>
internal static class ReadoutTimeResolver
{
    /// <summary>Parses a bare "HH:mm:ss(.fff)" time of day; null when blank/unparseable.</summary>
    public static TimeSpan? ParseTimeOfDay(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var t) ? t : null;
    }

    /// <summary>
    /// Dates a time of day onto <paramref name="baseDate"/> (whose <see cref="DateTimeOffset.Date"/> and
    /// offset are used), honouring the recorded <paramref name="dow"/> when present, else stepping back a
    /// day when the time reads later than <paramref name="laterAnchor"/>. Null time or base → null.
    /// </summary>
    public static DateTimeOffset? Resolve(
        DateTimeOffset? baseDate,
        TimeSpan? time,
        DayOfWeek? dow,
        DateTimeOffset? laterAnchor)
    {
        if (time is null || baseDate is null)
            return null;

        var date = baseDate.Value.Date;
        var offset = baseDate.Value.Offset;

        // Primary: use the recorded weekday. Count back 0..6 days from the base day to that weekday.
        if (dow is { } wd)
        {
            var back = ((int)date.DayOfWeek - (int)wd + 7) % 7;
            return new DateTimeOffset(date.AddDays(-back) + time.Value, offset);
        }

        // Fallback: place on the base day, then step back a day only on a genuine midnight WRAP — when the
        // time reads more than half a day later than the next known event. A punch a few minutes past the
        // anchor (e.g. a finish-line control stamped just after the finish time) is the same day, not the
        // previous one; only a near-full-day apparent jump (anchor 00:20, this 23:55) is a real wrap.
        var stamp = new DateTimeOffset(date + time.Value, offset);
        if (laterAnchor is { } anchor && stamp - anchor > HalfDay)
            stamp = stamp.AddDays(-1);
        return stamp;
    }

    private static readonly TimeSpan HalfDay = TimeSpan.FromHours(12);

    /// <summary>
    /// Back-dates a course's punches (in file order) so each lands on the right side of midnight: walks
    /// from the last punch to the first, anchoring the monotonic fallback on <paramref name="finish"/> and
    /// then on each punch already dated. Returns timestamps aligned 1:1 with <paramref name="punches"/>.
    /// </summary>
    public static DateTimeOffset?[] ResolvePunches(
        DateTimeOffset? baseDate,
        IReadOnlyList<(TimeSpan? Time, DayOfWeek? Dow)> punches,
        DateTimeOffset? finish)
    {
        var stamps = new DateTimeOffset?[punches.Count];
        var laterAnchor = finish;
        for (var i = punches.Count - 1; i >= 0; i--)
        {
            var stamp = Resolve(baseDate, punches[i].Time, punches[i].Dow, laterAnchor);
            stamps[i] = stamp;
            if (stamp is not null)
                laterAnchor = stamp;
        }
        return stamps;
    }
}
