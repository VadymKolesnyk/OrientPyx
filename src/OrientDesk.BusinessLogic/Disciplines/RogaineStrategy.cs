using OrientDesk.BusinessLogic.Enums;

namespace OrientDesk.BusinessLogic.Disciplines;

/// <summary>
/// Rogaine (рогейн): a long score format. Competitors pick from a list of allowed control points,
/// each worth points, within a (usually long) time limit and are ranked by points collected.
/// </summary>
public sealed class RogaineStrategy : DisciplineStrategyBase
{
    public override DisciplineType Type => DisciplineType.Rogaine;

    public override bool UsesControlPointPoints => true;

    public override bool UsesColumn(GroupColumn column) => column switch
    {
        GroupColumn.CourseOrder => true,           // the list of allowed control points
        _ => base.UsesColumn(column)
    };

    public override bool UsesParticipantColumn(ParticipantColumn column) => column switch
    {
        ParticipantColumn.Team => true,            // rogaine competitors run as teams
        _ => base.UsesParticipantColumn(column)
    };
}
