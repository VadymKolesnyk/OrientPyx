using OrientDesk.BusinessLogic.Enums;

namespace OrientDesk.BusinessLogic.Disciplines;

/// <summary>
/// Set course (заданий напрямок): a prescribed control-point order. The control count is derived
/// from that order and shown read-only.
/// </summary>
public sealed class SetCourseStrategy : DisciplineStrategyBase
{
    public override DisciplineType Type => DisciplineType.SetCourse;

    public override bool UsesColumn(GroupColumn column) => column switch
    {
        GroupColumn.CourseOrder => true,
        _ => base.UsesColumn(column)
    };
}
