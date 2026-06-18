using System.Globalization;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// Shared formatting/parsing for the editable «бонус» (points-correction) text used by the participant
/// day grid (<see cref="ParticipantDayRowViewModel"/>) and the roster cells
/// (<see cref="RosterDayCellViewModel"/>). The value is an optional signed integer: empty (or a lone "-"
/// mid-edit) means "not entered" (no correction), otherwise a positive or negative whole number of points.
/// </summary>
internal static class BonusFormat
{
    /// <summary>Formats a stored bonus as a plain (optionally signed) integer; empty when unset.</summary>
    public static string Format(int? value) =>
        value is { } v ? v.ToString(CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>
    /// Parses editable signed-integer text into a bonus. Returns <c>true</c> with a null result for
    /// empty/whitespace (or a lone sign mid-edit) = clear; <c>true</c> with the value for a valid integer;
    /// <c>false</c> (leaving <paramref name="result"/> null) for any other shape so the caller can revert.
    /// </summary>
    public static bool TryParse(string? text, out int? result)
    {
        result = null;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed == "-")
            return true; // empty or a half-typed sign clears the correction

        if (int.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
        {
            result = n;
            return true;
        }
        return false;
    }
}
