using System.Globalization;

namespace OrientPyx.Plugins.Sdk.Toolkit;

/// <summary>The time formats used by the protocols, splits and printouts.</summary>
public static class TimeFormat
{
    /// <summary>"m:ss" under an hour, "h:mm:ss" above; empty for null or negative (splits panel / slip).</summary>
    public static string Duration(TimeSpan? span) => span is { } s && s >= TimeSpan.Zero ? Short(s) : string.Empty;

    /// <summary>"m:ss" under an hour, "h:mm:ss" above; empty for null.</summary>
    public static string Span(TimeSpan? span) => span is { } s ? Short(s) : string.Empty;

    /// <summary>"h:mm:ss" — a result time in the protocols; empty for null or negative.</summary>
    public static string Result(TimeSpan? span) =>
        span is { } s && s >= TimeSpan.Zero ? s.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>"hh:mm:ss" — a time of day (start minute, clock); empty for null.</summary>
    public static string Clock(TimeSpan? span) =>
        span is { } s ? s.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>"00:10:30" — always two-digit total hours (the summary sheet).</summary>
    public static string TotalHours(TimeSpan span) =>
        $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";

    private static string Short(TimeSpan s) => s.TotalHours >= 1
        ? s.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : s.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
