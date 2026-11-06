using System.Globalization;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// The number formats of the split printout (distance, pace), shared by the real slip built in
/// <see cref="CompetitionEditorService.GetSplitPrintDocumentAsync"/> and the test slip
/// (<see cref="Models.SplitPrintSample"/>), so both print identically.
/// </summary>
internal static class SplitPrintFormat
{
    /// <summary>Distance in whole metres (the unit lives in the header); blank for null/zero.</summary>
    public static string DistanceMetres(decimal? km) => km is { } d && d > 0m
        ? Math.Round(d * 1000m).ToString("0", CultureInfo.InvariantCulture)
        : string.Empty;

    /// <summary>Pace "m:ss" from seconds-per-km (the unit /км lives in the header); blank for null/non-positive.</summary>
    public static string Pace(double? secondsPerKm)
    {
        if (secondsPerKm is not { } s || s <= 0 || double.IsInfinity(s))
            return string.Empty;
        var span = TimeSpan.FromSeconds(Math.Round(s));
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }

    /// <summary>Seconds per km for a time over a distance; null when either is missing or non-positive.</summary>
    public static double? SecondsPerKm(TimeSpan? time, decimal km) =>
        time is { } t && t > TimeSpan.Zero && km > 0m
            ? t.TotalSeconds / (double)km
            : null;
}
