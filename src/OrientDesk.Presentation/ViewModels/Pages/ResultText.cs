using OrientDesk.BusinessLogic.Enums;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// Shared display formatting for a <see cref="ParticipantDayResult"/>, so the day grid and the roster
/// render the result columns identically. All values are read-only text; the status code reuses
/// <see cref="FinishStatusOptions.ShortCode"/>. Times are shown in local time of day.
/// </summary>
public static class ResultText
{
    /// <summary>Actual start (chip start only) as "HH:mm:ss", blank when none.</summary>
    public static string ActualStart(ParticipantDayResult r) =>
        r.ActualStart is { } t ? t.ToLocalTime().ToString("HH:mm:ss") : string.Empty;

    /// <summary>Finish time as "HH:mm:ss", blank when none.</summary>
    public static string Finish(ParticipantDayResult r) =>
        r.FinishTime is { } t ? t.ToLocalTime().ToString("HH:mm:ss") : string.Empty;

    /// <summary>Result time (finish − start) as "H:mm:ss", blank when no valid OK result.</summary>
    public static string Result(ParticipantDayResult r) =>
        r.ResultTime is { } e && e >= TimeSpan.Zero ? e.ToString("h\\:mm\\:ss") : string.Empty;

    /// <summary>The standard short status code (OK / MP / …), blank for None.</summary>
    public static string Status(ParticipantDayResult r) => FinishStatusOptions.ShortCode(r.Status);

    /// <summary>1-based place, blank when unplaced.</summary>
    public static string Place(ParticipantDayResult r) => r.Place is { } p ? p.ToString() : string.Empty;

    /// <summary>Score / «Бали» (rogaine), blank for non-scoring disciplines.</summary>
    public static string Score(ParticipantDayResult r) => r.Score is { } s ? s.ToString() : string.Empty;
}
