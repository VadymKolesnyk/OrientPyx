using System.Globalization;
using System.Linq;
using System.Text;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

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

    /// <summary>1-based place; «П/К» for an out-of-competition runner; blank when otherwise unplaced.</summary>
    public static string Place(ParticipantDayResult r) =>
        r.Place is { } p ? p.ToString()
        : r.OutOfCompetition ? ParticipantDayResult.OutOfCompetitionMark
        : string.Empty;

    /// <summary>Score / «Бали» (rogaine), blank for non-scoring disciplines.</summary>
    public static string Score(ParticipantDayResult r) => r.Score is { } s ? s.ToString() : string.Empty;

    /// <summary>Ranking points / «Очки» (two fractional digits), blank when no points were awarded.</summary>
    public static string Points(ParticipantDayResult r) => r.Points is { } p ? PointsTable.Format(p) : string.Empty;

    /// <summary>Awarded sports rank / «Виконаний розряд» (Додаток 89), blank when none.</summary>
    public static string AwardedRank(ParticipantDayResult r) => r.AwardedRank ?? string.Empty;

    /// <summary>
    /// A multi-line breakdown of how the «Бали» total was made up — one "КП {code}: +{points}" line per
    /// scoring control, then (when an over-time penalty applied) the "X − Y = Z" gross/penalty/net lines.
    /// Blank when there is no score or no breakdown (so the cell shows no tooltip). For a teamed rogaine
    /// runner the controls are the team's common ones, matching the team «Бали».
    /// </summary>
    public static string? ScoreTooltip(ParticipantDayResult r, ILocalizationService loc)
    {
        if (r.Score is not { } total || r.ScoreBreakdown.Count == 0)
            return null; // null ⇒ no tooltip popup (an empty string would still show an empty box)

        var sb = new StringBuilder();
        sb.Append(loc.Get("Participants.Score.Tooltip.Header"));
        foreach (var line in r.ScoreBreakdown)
            sb.Append('\n').Append(string.Format(loc.Get("Participants.Score.Tooltip.Control"), line.Code, line.Points));

        // Over-time penalty breakdown, when one was deducted (gross/penalty carried alongside the net).
        if (r.ScoreGross is { } gross && r.ScorePenalty is { } penalty && penalty > 0)
        {
            sb.Append('\n').Append(string.Format(loc.Get("Participants.Score.Tooltip.Gross"), gross));
            sb.Append('\n').Append(string.Format(loc.Get("Participants.Score.Tooltip.Penalty"), penalty));
        }
        // Points correction («бонус»), when one applied — signed so a negative correction reads clearly.
        if (r.Bonus is { } bonus && bonus != 0)
            sb.Append('\n').Append(string.Format(loc.Get("Participants.Score.Tooltip.Bonus"), bonus.ToString("+0;-0", CultureInfo.InvariantCulture)));
        sb.Append('\n').Append(string.Format(loc.Get("Participants.Score.Tooltip.Total"), total));
        return sb.ToString();
    }
}
