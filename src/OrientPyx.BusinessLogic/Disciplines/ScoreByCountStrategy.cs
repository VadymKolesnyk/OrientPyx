using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>
/// Score by control count (за вибором по кількості КП): competitors pick from a list of allowed
/// control points and must take at least a required minimum to avoid disqualification. There is no
/// prescribed order — the course order column is just the allowed set — so the run is judged purely on
/// how many distinct allowed controls the chip took, and the classified runners are ranked by time.
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

    /// <summary>
    /// Priority DNF &gt; MP &gt; OVT &gt; OK:
    /// <list type="number">
    ///   <item>no finish punch ⇒ <see cref="FinishStatus.Dnf"/>;</item>
    ///   <item>fewer distinct allowed controls taken than the group's «мін. к-сть КП»
    ///   (<see cref="FinishContext.RequiredControlCount"/>; unset ⇒ every allowed control is required)
    ///   ⇒ <see cref="FinishStatus.Mp"/>, detailed as "taken/required";</item>
    ///   <item>a time limit is set and (finish − start) exceeds it ⇒ <see cref="FinishStatus.Ovt"/>;</item>
    ///   <item>otherwise <see cref="FinishStatus.Ok"/> — ranked by time against the others who made the
    ///   minimum, since everyone classified ran the same qualifying distance.</item>
    /// </list>
    /// Order is never judged: any allowed control counts wherever it was punched. A control is counted
    /// once even if punched twice, and foreign punches (outside the allowed set) are ignored. Disabled
    /// («проблемні») controls are already stripped from <see cref="FinishContext.ExpectedControls"/> by
    /// the caller, so a broken box lowers the required-all fallback instead of costing a runner an MP.
    /// </summary>
    public override FinishStatusResult EvaluateFinish(FinishContext context)
    {
        if (context.FinishTime is null)
            return FinishStatusResult.Of(FinishStatus.Dnf);

        var allowed = new HashSet<string>(
            context.ExpectedControls.Select(c => c.Trim()).Where(c => c.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        // Distinct allowed controls actually taken — repeats and foreign punches don't add to the tally.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var punch in context.PunchedControls)
        {
            var code = punch.Trim();
            if (allowed.Contains(code))
                taken.Add(code);
        }

        // A group with no stated minimum requires the whole allowed set (the strict reading — the runner
        // has to clear the course), so a blank field never silently classifies an unfinished run.
        var required = context.RequiredControlCount ?? allowed.Count;
        if (taken.Count < required)
            return new FinishStatusResult(
                FinishStatus.Mp, $"{taken.Count}/{required}", FinishDetailKind.ControlCount);

        if (context.TimeLimit is { } limit && context.StartTime is { } start
            && context.FinishTime.Value - start > limit)
            return FinishStatusResult.Of(FinishStatus.Ovt);

        return FinishStatusResult.Of(FinishStatus.Ok);
    }
}
