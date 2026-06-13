using OrientDesk.BusinessLogic.Enums;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Disciplines;

/// <summary>
/// Set course (заданий напрямок): a prescribed control-point order. The control count is derived
/// from that order and shown read-only, and the finish is judged by visiting every prescribed control
/// in order (extra punches are ignored) and having a finish punch within the time limit.
/// </summary>
public sealed class SetCourseStrategy : DisciplineStrategyBase
{
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
}
