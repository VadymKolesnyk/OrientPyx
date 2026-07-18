using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>
/// Encapsulates everything that varies by competition type (<see cref="DisciplineType"/>). Each
/// format is a separate implementation, resolved from <see cref="IDisciplineStrategyProvider"/>;
/// shared code asks the strategy instead of switching on the enum, so adding a new format means
/// adding one class and one DI registration — nothing else changes (Open/Closed).
///
/// Today the contract covers grid-column configuration and the control-point count. Result scoring,
/// ranking and disqualification rules will be added to this same interface later (see the project
/// roadmap) so the call sites keep working through it without reintroducing switch statements.
/// </summary>
public interface IDisciplineStrategy
{
    /// <summary>The competition type this strategy implements.</summary>
    DisciplineType Type { get; }

    /// <summary>
    /// Localization key for this discipline's display name, e.g. <c>"Discipline.Type.Rogaine"</c>.
    /// </summary>
    string NameKey { get; }

    /// <summary>True when the given group column is meaningful for this discipline.</summary>
    bool UsesColumn(GroupColumn column);

    /// <summary>True when the given participant column is meaningful for this discipline (e.g. the
    /// team column for rogaine).</summary>
    bool UsesParticipantColumn(ParticipantColumn column);

    /// <summary>
    /// True when control points on this discipline's days carry a per-point value (score / rogaine),
    /// which the control-points screen exposes as an editable "points" column.
    /// </summary>
    bool UsesControlPointPoints { get; }

    /// <summary>
    /// True when this discipline runs a prescribed control order (set course, mixed, scatter), so the start
    /// draw should warn when a group starts on the same minute as another group sharing its opening control —
    /// two runners would punch the same first control at once. False for the free-order formats (за вибором,
    /// score-by-time, rogaine), where each runner chooses their own route: there is no meaningful "first
    /// control", so the draw skips the shared-first-control / identical-course clash checks for them entirely.
    /// </summary>
    bool ChecksStartControlClash { get; }

    /// <summary>
    /// Default points deducted per (started) minute over the time limit when the group leaves the penalty
    /// blank, or null when the discipline applies no automatic over-time penalty. Rogaine returns 1; the
    /// other formats return null. Used both to fill the default in the groups grid and in the scoring math.
    /// </summary>
    decimal? DefaultPenaltyPerMinute { get; }

    /// <summary>
    /// Number of control points implied by the course-order text, excluding start/finish markers.
    /// Used for the read-only "control count" column on set-course days.
    /// </summary>
    int ControlCount(string courseOrder);

    /// <summary>
    /// Derives a participant's finish status from one read-out (see <see cref="FinishContext"/>). The
    /// rules vary by discipline — set-course checks the control order and the finish punch; score
    /// formats will judge by points/count later. Returns <see cref="Enums.FinishStatus.None"/> when the
    /// discipline does not yet evaluate finishes this way.
    /// </summary>
    FinishStatusResult EvaluateFinish(FinishContext context);

    /// <summary>
    /// Builds the passage/splits view shown under the finish-read log for a selected read-out. The shape
    /// is discipline-specific: a set course renders the prescribed order against the actual passage
    /// (<see cref="SplitsLayout.Ordered"/>); the score formats render the allowed controls with points
    /// and a running total (<see cref="SplitsLayout.Scored"/>).
    /// </summary>
    SplitsView BuildSplits(SplitsContext context);
}
