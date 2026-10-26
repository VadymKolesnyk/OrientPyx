using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>
/// Encapsulates everything that varies by competition type (<see cref="DisciplineId"/>). Each
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
    DisciplineId Id { get; }

    /// <summary>True only for the placeholder that stands in for a discipline whose implementation is not
    /// available (a missing / disabled plugin, Р-2). Drives the «Потрібен плагін» banner.</summary>
    bool IsUnknown { get; }

    /// <summary>
    /// Who a result belongs to: one runner (<see cref="CompetitorModel.Individual"/>) or a team whose members'
    /// punches are scored together (<see cref="CompetitorModel.Team"/>, rogaine). Shared code asks this instead
    /// of comparing against a specific discipline.
    /// </summary>
    CompetitorModel Competitors { get; }

    /// <summary>True when the group's course is a set of alternative orders (variants) stored per (day, group)
    /// rather than the single course-order string — scatter.</summary>
    bool UsesCourseVariants { get; }

    /// <summary>True when the course order is a «mixed» pattern (<c>&lt;…&gt;</c>, <c>[N: …]</c>) that the
    /// groups page previews and validates — mixed only.</summary>
    bool UsesCoursePattern { get; }

    /// <summary>True when results of this discipline may earn a sports rank («виконаний розряд», Р-7).</summary>
    bool AwardsRanks { get; }

    /// <summary>
    /// Localization key for this discipline's display name, e.g. <c>"Discipline.Type.Rogaine"</c>.
    /// </summary>
    string NameKey { get; }

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

    /// <summary>
    /// The start a runner's time is measured from (Р-5). Default: the chip's start box, else the assigned
    /// start time paired with the finish date. Also used for the time-limit check.
    /// </summary>
    DateTimeOffset? ResolveStart(StartContext context);

    /// <summary>
    /// Adjusts an OK result's time before ranking (Р-6) — e.g. a penalty or a coefficient. Default: unchanged.
    /// A changed value is rounded to a whole second by the caller.
    /// </summary>
    TimeSpan AdjustTime(AdjustContext context);

    /// <summary>
    /// Places the group's competitors (Р-8): writes <see cref="ParticipantDayResult.Place"/> (and, for a team
    /// format, the team score) into <see cref="RankingContext.Results"/>. Default: score ↓ then time ↑, equal
    /// results share a place and the next place skips.
    /// </summary>
    void Rank(RankingContext context);
}
