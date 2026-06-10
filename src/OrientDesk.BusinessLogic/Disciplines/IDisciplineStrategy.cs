using OrientDesk.BusinessLogic.Enums;

namespace OrientDesk.BusinessLogic.Disciplines;

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

    /// <summary>
    /// True when control points on this discipline's days carry a per-point value (score / rogaine),
    /// which the control-points screen exposes as an editable "points" column.
    /// </summary>
    bool UsesControlPointPoints { get; }

    /// <summary>
    /// Number of control points implied by the course-order text, excluding start/finish markers.
    /// Used for the read-only "control count" column on set-course days.
    /// </summary>
    int ControlCount(string courseOrder);
}
