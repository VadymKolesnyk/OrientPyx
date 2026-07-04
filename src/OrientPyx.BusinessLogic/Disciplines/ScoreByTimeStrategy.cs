using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>
/// Score by time (за вибором за часом): competitors pick from a list of allowed control points,
/// each worth points, and lose points per minute of finishing late.
/// </summary>
public sealed class ScoreByTimeStrategy : DisciplineStrategyBase
{
    public override DisciplineType Type => DisciplineType.ScoreByTime;

    public override bool UsesControlPointPoints => true;

    public override bool UsesColumn(GroupColumn column) => column switch
    {
        GroupColumn.CourseOrder => true,           // the list of allowed control points
        GroupColumn.PenaltyPerMinute => true,      // points lost per minute late
        _ => base.UsesColumn(column)
    };
}
