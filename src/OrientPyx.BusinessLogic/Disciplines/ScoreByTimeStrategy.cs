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

    /// <summary>A finish up to 30 min after the time limit still counts (losing points); later is OVT.</summary>
    public override TimeSpan? DefaultMaxLate => TimeSpan.FromMinutes(30);

    /// <summary>
    /// Like the rogaine: a read-out with a finish is OK — finishing late only costs points — unless it came more than
    /// «Макс. запізнення» after the time limit, which is OVT; one without a finish is DNF. A judge's manual status
    /// still overrides it.
    /// </summary>
    public override FinishStatusResult EvaluateFinish(FinishContext context) =>
        FinishStatusResult.Of(context.FinishTime is null ? FinishStatus.Dnf
            : IsPastMaxLate(context) ? FinishStatus.Ovt
            : FinishStatus.Ok);

    public override bool UsesColumn(GroupColumn column) => column switch
    {
        GroupColumn.CourseOrder => true,           // the list of allowed control points
        GroupColumn.PenaltyPerMinute => true,      // points lost per minute late
        GroupColumn.MaxLate => true,               // how late a finish still counts; later is OVT
        _ => base.UsesColumn(column)
    };
}
