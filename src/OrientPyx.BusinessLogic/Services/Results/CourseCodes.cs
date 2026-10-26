using System.Globalization;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services.Results;

/// <summary>
/// Pure helpers over course orders, punch strings and control-point lists shared by the editor service and
/// the day-results calculator (no database access).
/// </summary>
internal static class CourseCodes
{
    // The day's start/finish control codes (trimmed), case-insensitive — dropped from punches and courses.
    internal static HashSet<string> StartFinishCodesOf(IReadOnlyList<ControlPoint> controlPoints) =>
        new(controlPoints
                .Where(c => c.Type is ControlPointType.Start or ControlPointType.Finish)
                .Select(c => c.Code.Trim()),
            StringComparer.OrdinalIgnoreCase);

    internal static IEnumerable<string> SplitCodes(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // The control codes referenced by a course order, in reading order (duplicates kept). For a plain order
    // ("S1 31 32 F") this is just the tokens; for a «mixed» order <b>pattern</b> ("&lt;41 42&gt; [2 45 46]")
    // it flattens the &lt;…&gt;/[N …] blocks to their leaf controls (the bracket/amount tokens are not codes).
    // Every course-order consumer routes through this so a pattern is never mistaken for literal tokens.
    internal static List<string> CourseOrderCodes(string? courseOrder) =>
        string.IsNullOrWhiteSpace(courseOrder)
            ? []
            : courseOrder.IndexOfAny(['<', '[']) < 0
                ? SplitCodes(courseOrder).ToList()
                : Disciplines.CoursePattern.CoursePattern.Parse(courseOrder).ControlCodes.ToList();

    // The day's disabled («проблемні») control codes (trimmed), case-insensitive. These are dropped from the
    // prescribed/allowed course wherever it is required, so a runner missing one is not penalised and a scored
    // control no longer counts.
    internal static HashSet<string> DisabledCodesOf(IReadOnlyList<ControlPoint> controlPoints) =>
        new(controlPoints.Where(c => c.IsDisabled).Select(c => c.Code.Trim()),
            StringComparer.OrdinalIgnoreCase);

    // The prescribed course codes minus the day's start/finish markers AND its disabled controls, in order —
    // the canonical "controls that must be visited / are allowed" list every scoring/status/splits path uses.
    internal static List<string> CourseControls(
        string? courseOrder, IReadOnlySet<string> startFinishCodes, IReadOnlySet<string> disabledCodes) =>
        CourseOrderCodes(courseOrder)
            .Where(c => !startFinishCodes.Contains(c) && !disabledCodes.Contains(c))
            .ToList();

    // The disabled («проблемні») controls that actually appear in a given group's course order (start/finish
    // markers aside), so the splits view lists only the ones relevant to that course as «вимкнено».
    internal static HashSet<string> DisabledInCourse(
        string? courseOrder, IReadOnlySet<string> startFinishCodes, IReadOnlySet<string> disabledCodes) =>
        new(CourseOrderCodes(courseOrder).Where(c => !startFinishCodes.Contains(c) && disabledCodes.Contains(c)),
            StringComparer.OrdinalIgnoreCase);

    // Pairs an assigned start time-of-day with the read-out finish's date, so the time-limit check
    // compares two same-day timestamps. Null when either part is missing.
    internal static DateTimeOffset? CombineWithFinishDate(TimeSpan? startOfDay, DateTimeOffset? finish) =>
        DisciplineStrategyBase.CombineWithFinishDate(startOfDay, finish);

    // Reverses EncodePunchTimes back into a punch list (codes + times) for the splits view. Tokens that
    // can't be parsed are skipped; a "-" time becomes null.
    internal static IReadOnlyList<ChipPunch> DecodePunchTimes(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
            return [];

        var list = new List<ChipPunch>();
        foreach (var token in encoded.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = token.LastIndexOf('@');
            if (at <= 0)
                continue;
            var code = token[..at].Trim();
            var timePart = token[(at + 1)..];
            DateTimeOffset? time = timePart != "-" && long.TryParse(timePart, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var ticks)
                // Ticks are UTC, so the reconstructed stamp must be converted back to local time rather
                // than left at offset 0 — otherwise it renders shifted by the local UTC offset wherever it
                // is shown as a bare time of day, and the edit modal would then save that shifted value.
                ? new DateTimeOffset(ticks, TimeSpan.Zero).ToLocalTime()
                : null;
            list.Add(new ChipPunch(code, time));
        }
        return list;
    }

    // Serializes punches as comma-separated "code@ticks" (UTC ticks, "-" for an unknown time), the
    // form stored in FinishReadout.PunchTimes and reused inside the content key.
    internal static string EncodePunchTimes(IEnumerable<ChipPunch> punches) =>
        string.Join(",", punches.Select(p =>
            $"{p.ControlCode.Trim()}@{p.Time?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "-"}"));

    // Turns a set of scored control codes into ordered ScoreLines (ascending control number, then text)
    // each carrying its point value — the per-control «Бали» breakdown shown in the column tooltip.
    internal static IReadOnlyList<ScoreLine> ScoreLines(
        IReadOnlySet<string> codes, IReadOnlyDictionary<string, int> pointsByCode)
        => codes
            .OrderBy(c => long.TryParse(c, out var n) ? n : long.MaxValue)
            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ScoreLine(c, pointsByCode.TryGetValue(c, out var p) ? p : 0))
            .ToList();

    internal static readonly IReadOnlyList<ScatterVariantData> NoScatterVariants = [];

    // The trimmed team name for a member's participant; empty when the participant has no team (поза
    // конкурсом for a team format).
    internal static string TeamKeyFor(ParticipantDay link, IReadOnlyDictionary<Guid, string> teamByParticipant)
        => teamByParticipant.TryGetValue(link.ParticipantId, out var t) ? t : string.Empty;
}
