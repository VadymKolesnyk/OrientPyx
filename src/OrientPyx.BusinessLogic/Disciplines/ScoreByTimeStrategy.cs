using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>
/// Score by time (за вибором за часом): competitors pick from a list of allowed control points,
/// each worth points, and lose points per minute of finishing late.
/// </summary>
public sealed class ScoreByTimeStrategy : DisciplineStrategyBase
{
    private readonly ICourseDistanceCalculator _distance;

    public ScoreByTimeStrategy(ICourseDistanceCalculator distance) => _distance = distance;

    public override DisciplineId Id => DisciplineId.ScoreByTime;

    /// <summary>The scored layout: the allowed controls with points for the panel, and the passage in the order
    /// taken (leg, distance, pace) for the split slip. Late minutes cost points only when the group sets a rate.</summary>
    public override SplitsView BuildSplits(SplitsContext context) =>
        BuildScoredSplits(context, _distance, DefaultPenaltyPerMinute);

    public override bool UsesControlPointPoints => true;

    /// <summary>
    /// Like the rogaine: a read-out with a finish is OK (finishing late only costs points, never OVT), one without a
    /// finish is DNF. A judge's manual status still overrides it.
    /// </summary>
    public override FinishStatusResult EvaluateFinish(FinishContext context) =>
        FinishStatusResult.Of(context.FinishTime is null ? FinishStatus.Dnf : FinishStatus.Ok);

    public override bool UsesColumn(GroupColumn column) => column switch
    {
        GroupColumn.CourseOrder => true,           // the list of allowed control points
        GroupColumn.PenaltyPerMinute => true,      // points lost per minute late
        _ => base.UsesColumn(column)
    };
}
