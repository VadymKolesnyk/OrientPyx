using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// The computed run result for one participant on one day, derived from their chip's finish read-out.
/// When the chip was read more than once on the day, the <b>latest</b> read-out (highest sequence) is
/// used. Surfaced read-only on the participant tables, except <see cref="Status"/>, which a judge may
/// override (persisted on <c>ParticipantDay.ResultStatusOverride</c>).
/// </summary>
/// <param name="ActualStart">The chip's own read-out start (<c>FinishReadout.StartTime</c>) only; null
/// when the chip recorded no start box. Distinct from the assigned start time.</param>
/// <param name="FinishTime">The latest read-out's finish time; null when none.</param>
/// <param name="Status">The effective status: the override when set, else the discipline's computed
/// finish status (<see cref="FinishStatus.None"/> when nothing applies).</param>
/// <param name="Override">The raw manual override (null when none) — lets the UI show the status
/// dropdown as "auto" vs an explicit pick, distinct from the effective <see cref="Status"/>.</param>
/// <param name="Computed">The discipline-computed status with NO override applied — what "auto" would
/// resolve to if the override were cleared. The status dropdown's "(… — автоматично)" sentinel reflects
/// THIS, not the effective <see cref="Status"/> (which already folds in the override).</param>
/// <param name="ResultTime">finish − resolved-start (chip start, else the assigned start paired with
/// the finish date), only when <see cref="Status"/> is <see cref="FinishStatus.Ok"/>.</param>
/// <param name="Place">1-based rank within the group on the day, only for an <see cref="FinishStatus.Ok"/>
/// result (rogaine ranks by score then time, others by time); null otherwise. Ties share a place. Always
/// null for an <see cref="OutOfCompetition"/> runner in a personal discipline (they show «П/К», not a rank).</param>
/// <param name="Score">Total points collected for a point-scoring (rogaine) discipline; null otherwise.</param>
/// <param name="HasReadout">True when a read-out on the day matched this participant's chip.</param>
public sealed record ParticipantDayResult(
    DateTimeOffset? ActualStart,
    DateTimeOffset? FinishTime,
    FinishStatus Status,
    FinishStatus? Override,
    FinishStatus Computed,
    TimeSpan? ResultTime,
    int? Place,
    int? Score,
    bool HasReadout)
{
    /// <summary>An empty result for a participant whose chip was never read on the day.</summary>
    public static readonly ParticipantDayResult Empty =
        new(null, null, FinishStatus.None, null, FinishStatus.None, null, null, null, HasReadout: false);

    /// <summary>
    /// True when the effective status is a "problem" code — anything other than OK (the all-clear) and
    /// the blank <see cref="FinishStatus.None"/>. Drives the red status text in the participant tables.
    /// </summary>
    public bool StatusIsProblem => Status is not (FinishStatus.Ok or FinishStatus.None);

    /// <summary>
    /// True when this is an «поза конкурсом» (out-of-competition) run in a PERSONAL (non-team) discipline.
    /// Such a runner is scored normally (their result time / Бали still compute) but is NOT placed and does
    /// NOT occupy a position — the others are ranked as if they were not there — so the place column shows the
    /// «П/К» marker (see <see cref="OutOfCompetitionMark"/>) instead of a rank. Team (rogaine) поза конкурсом is
    /// handled by the team pass and does not set this flag.
    /// </summary>
    public bool OutOfCompetition { get; init; }

    /// <summary>The language-neutral marker shown in the place column for an <see cref="OutOfCompetition"/>
    /// runner (Ukrainian «поза конкурсом» → «П/К»).</summary>
    public const string OutOfCompetitionMark = "П/К";

    /// <summary>
    /// The controls that make up <see cref="Score"/> (rogaine only), each with its point value, in
    /// ascending control order — for a per-control breakdown tooltip on the «Бали» column. For a teamed
    /// runner these are the team's common controls (the ones that scored for the team), so it matches the
    /// team Бали shown; for a teamless (поза конкурсом) runner they are that runner's own scored controls.
    /// Empty for a non-scoring discipline or when nothing scored.
    /// </summary>
    public IReadOnlyList<ScoreLine> ScoreBreakdown { get; init; } = [];

    /// <summary>Gross points before the over-time penalty (the "X" in "X − Y = Z"); null when no penalty
    /// applied (then <see cref="Score"/> alone is the total). Rogaine only.</summary>
    public int? ScoreGross { get; init; }

    /// <summary>Over-time penalty deducted from <see cref="ScoreGross"/> (the "Y"); null/0 when none. Rogaine only.</summary>
    public int? ScorePenalty { get; init; }

    /// <summary>
    /// The judge's points correction («бонус») applied to this result, or null when none was entered. For a
    /// teamed rogaine runner this is the team correction actually folded into <see cref="Score"/> (the
    /// smallest entered member bonus), so the «Бали» tooltip's "+бонус" line matches the team total; for
    /// everyone else it is this participant's own entered bonus. Folded into <see cref="Score"/> already.
    /// </summary>
    public int? Bonus { get; init; }

    /// <summary>
    /// The ranking points («Очки») awarded for this result by the group's effective points rule (the group's
    /// per-day override, else the competition default), or null when no rule applies / the result is not
    /// rankable. Decimal with two fractional digits. The rule is either a placement table (keyed by
    /// <see cref="Place"/>) or a formula over the runner's time/score relative to the group leader — see
    /// <c>PointsRuleEvaluator</c>. A non-OK or out-of-competition runner earns no points (null).
    /// </summary>
    public decimal? Points { get; init; }

    /// <summary>
    /// The awarded sports rank («виконаний розряд», Додаток 89) earned by this result, or null when none.
    /// Computed per group from the group's rank level and the course-rank-vs-result-percentage lookup in the
    /// editable qualification table (or «МСУ» for the top-N when the group sets a master count). Blank for a
    /// non-OK / out-of-competition runner, a group that awards no ranks, or a group failing the validity
    /// conditions (too few participants / regions). See <c>RankQualificationEvaluator</c>.
    /// </summary>
    public string? AwardedRank { get; init; }
}

/// <summary>One control's contribution to the «Бали» total: the control code and its point value.</summary>
public sealed record ScoreLine(string Code, int Points);
