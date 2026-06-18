using OrientDesk.BusinessLogic.Enums;

namespace OrientDesk.BusinessLogic.Models;

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
/// result (rogaine ranks by score then time, others by time); null otherwise. Ties share a place.</param>
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
}
