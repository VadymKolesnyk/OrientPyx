namespace OrientDesk.BusinessLogic.Enums;

/// <summary>
/// Identifies a discipline-varying column in the groups grid. A discipline strategy declares which
/// of these it uses (<see cref="OrientDesk.BusinessLogic.Disciplines.IDisciplineStrategy.UsesColumn"/>),
/// so the UI can show/hide and enable/disable cells without knowing the competition rules itself.
/// Always-present columns (name, distance, discipline, actions) are intentionally not listed here.
/// </summary>
public enum GroupColumn
{
    /// <summary>Course order (set course) or the list of allowed control points (score formats).</summary>
    CourseOrder,

    /// <summary>Auto-computed count of control points, derived from the course order.</summary>
    ControlCount,

    /// <summary>Minimum number of control points required to avoid disqualification (score-by-count).</summary>
    RequiredControlCount,

    /// <summary>Points deducted per minute of finishing late (score-by-time).</summary>
    PenaltyPerMinute,

    /// <summary>Time limit (контрольний час). Used by every discipline.</summary>
    TimeLimit
}
