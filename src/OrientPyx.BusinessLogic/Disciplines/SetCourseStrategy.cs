using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>
/// Set course (заданий напрямок): a prescribed control-point order. The control count is derived
/// from that order and shown read-only, and the finish is judged by visiting every prescribed control
/// in order (extra punches are ignored) and having a finish punch within the time limit.
/// </summary>
public sealed class SetCourseStrategy : DisciplineStrategyBase
{
    private readonly ICourseDistanceCalculator _distance;

    public SetCourseStrategy(ICourseDistanceCalculator distance) => _distance = distance;

    public override DisciplineType Type => DisciplineType.SetCourse;

    public override bool UsesColumn(GroupColumn column) => column switch
    {
        GroupColumn.CourseOrder => true,
        _ => base.UsesColumn(column)
    };

    /// <summary>
    /// Priority DNF &gt; MP &gt; OVT &gt; OK:
    /// <list type="number">
    ///   <item>no finish punch ⇒ <see cref="FinishStatus.Dnf"/>;</item>
    ///   <item>the prescribed controls are not all present in order (as a subsequence of the punches,
    ///   so extra/foreign punches are ignored) ⇒ <see cref="FinishStatus.Mp"/> with a detail of the
    ///   first missing control;</item>
    ///   <item>a time limit is set and (finish − start) exceeds it ⇒ <see cref="FinishStatus.Ovt"/>;</item>
    ///   <item>otherwise <see cref="FinishStatus.Ok"/>.</item>
    /// </list>
    /// </summary>
    public override FinishStatusResult EvaluateFinish(FinishContext context)
    {
        if (context.FinishTime is null)
            return FinishStatusResult.Of(FinishStatus.Dnf);

        // Order check: every expected control must appear, in order, within the punches. Extras between
        // them are skipped (subsequence match), so a foreign punch never breaks an otherwise-correct run.
        var p = 0;
        var punched = context.PunchedControls;
        foreach (var expected in context.ExpectedControls)
        {
            var found = false;
            while (p < punched.Count)
            {
                if (string.Equals(punched[p].Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    p++;
                    found = true;
                    break;
                }
                p++;
            }
            if (!found)
                return new FinishStatusResult(FinishStatus.Mp, expected.Trim());
        }

        if (context.TimeLimit is { } limit && context.StartTime is { } start
            && context.FinishTime.Value - start > limit)
            return FinishStatusResult.Of(FinishStatus.Ovt);

        return FinishStatusResult.Of(FinishStatus.Ok);
    }

    /// <summary>
    /// Ordered (set-course) splits as two parallel lists. The <b>passage</b> keeps every punch read from
    /// the chip in chip order — nothing dropped, including out-of-order, foreign and repeated punches.
    /// A punch is flagged <see cref="PassagePunch.OnCourse"/> when it advances the prescribed course (a
    /// greedy <b>subsequence</b> match, the same rule the status check uses): it may skip past a missed
    /// control or a foreign/repeated punch, so a single missing КП in the middle does <b>not</b> off-course
    /// every later control — they still map onto their columns. The <b>leg/pace</b> is filled only for a
    /// <i>contiguous</i> on-course control — one whose prescribed predecessor was itself taken — and is
    /// measured from that previous on-course control (any extra punches in between are treated as if they
    /// were not there), so the split is the true single-leg time. A control reached after a missed КП (its
    /// leg would span the gap), an off-course punch, and the finish after an incomplete course all carry
    /// <b>no</b> course leg, since that time spans more than one prescribed leg and is meaningless. On top of
    /// that, every row also carries a <b>display</b> leg/distance/pace measured from the immediately preceding
    /// punch in chip order (<see cref="PassagePunch.DisplayLeg"/> etc.) — filled for every control, extras and
    /// the finish included — so the read-out panel and the slip always show час перегону/довжина/швидкість and
    /// keep their full column structure. The <b>expected</b> list is the prescribed course in order, each
    /// flagged taken or missing.
    /// </summary>
    public override SplitsView BuildSplits(SplitsContext context) =>
        BuildOrderedSplits(context, context.ExpectedControls, _distance);
}
