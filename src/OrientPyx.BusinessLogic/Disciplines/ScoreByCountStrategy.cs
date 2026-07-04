using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>
/// Score by control count (за вибором по кількості КП): competitors pick from a list of allowed
/// control points and must take at least a required minimum to avoid disqualification.
/// </summary>
public sealed class ScoreByCountStrategy : DisciplineStrategyBase
{
    public override DisciplineType Type => DisciplineType.ScoreByCount;

    public override bool UsesColumn(GroupColumn column) => column switch
    {
        GroupColumn.CourseOrder => true,            // the list of allowed control points
        GroupColumn.RequiredControlCount => true,   // minimum to avoid disqualification
        _ => base.UsesColumn(column)
    };
}
