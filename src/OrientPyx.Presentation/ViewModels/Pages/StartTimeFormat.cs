using System.Globalization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// Shared formatting/parsing for the editable start-time text used by the participant day grid
/// (<see cref="ParticipantDayRowViewModel"/>) and the roster cells (<see cref="RosterDayCellViewModel"/>).
/// The display format is the full stopwatch shape <c>hh:mm:ss</c>; the parser is lenient (it pads a
/// missing minute/second group with zeros and clamps any out-of-range minute/second to 59) so manual
/// entry like "9:30" or "9:99" still resolves to a valid time (09:30:00 / 09:59:00).
/// </summary>
internal static class StartTimeFormat
{
    /// <summary>Formats a stored start time as "hh:mm:ss" (empty when unset).</summary>
    public static string Format(TimeSpan? value) =>
        value is { } t ? t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>
    /// Parses editable "hh[:mm[:ss]]" text into a start time. Returns <c>true</c> with a clamped,
    /// zero-padded result for any input that splits into 1–3 numeric groups (so partial entry works);
    /// returns <c>false</c> (leaving <paramref name="result"/> null) only for an unparseable shape, so
    /// the caller can revert the cell. Empty/whitespace returns <c>true</c> with a null result (clear).
    /// </summary>
    public static bool TryParse(string? text, out TimeSpan? result)
    {
        result = null;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return true;

        var groups = trimmed.Split(':');
        if (groups.Length is < 1 or > 3)
            return false;

        var parts = new int[3];
        for (var i = 0; i < groups.Length; i++)
        {
            // A trailing/empty group ("9:" mid-edit) counts as zero.
            if (groups[i].Length == 0)
            {
                parts[i] = 0;
                continue;
            }

            if (!int.TryParse(groups[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                return false;

            // Hours are open-ended; minutes/seconds clamp to 59 so "9:99" becomes 09:59.
            parts[i] = i == 0 ? n : Math.Min(n, 59);
        }

        result = new TimeSpan(parts[0], parts[1], parts[2]);
        return true;
    }
}
