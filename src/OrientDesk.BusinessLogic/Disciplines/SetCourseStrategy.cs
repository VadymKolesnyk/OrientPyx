using OrientDesk.BusinessLogic.Enums;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Disciplines;

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
    /// the chip in chip order — nothing dropped, including out-of-order, foreign and repeated punches —
    /// each carrying its leg split (time since the previous punch; first from the start) and elapsed. A
    /// punch is flagged <see cref="PassagePunch.OnCourse"/> when, at the moment it was read, it is the
    /// next prescribed control still to be taken (greedy subsequence match, the same rule the status
    /// check uses), which also marks that expected control taken. The <b>expected</b> list is the
    /// prescribed course in order, each flagged taken or missing — the mapping to the actual route.
    /// </summary>
    public override SplitsView BuildSplits(SplitsContext context)
    {
        var expected = context.ExpectedControls;
        var matched = new bool[expected.Count];
        var ei = 0;                  // next prescribed control still to be taken
        DateTimeOffset? prev = context.StartTime;
        // Previous position (seeded at the start) for the leg distance — tracked in both representations,
        // preferring the undistorted paper-map mm when available and falling back to the geographic one.
        var prevPoint = context.StartCoord;
        var prevMap = context.StartMap;
        var taken = 0;

        var passage = new List<PassagePunch>(context.Punches.Count + 2);
        var index = 0;               // 1-based control number; the start/finish markers don't consume one

        // Start marker first: its time is the resolved start, no leg/elapsed (it is the reference point).
        // Code is left blank — the presentation layer supplies the localized "Start"/"Finish" label by Kind.
        passage.Add(new PassagePunch(0, string.Empty, OnCourse: false,
            context.StartTime, Leg: null, Elapsed: TimeSpan.Zero, PassageKind.Start));

        foreach (var punch in context.Punches)
        {
            var code = punch.ControlCode.Trim();
            if (code.Length == 0)
                continue;

            var leg = prev is { } pr && punch.Time is { } t ? t - pr : (TimeSpan?)null;
            var elapsed = context.StartTime is { } s && punch.Time is { } t2 ? t2 - s : (TimeSpan?)null;

            // Straight-line distance of this leg (from the previous position, the start for the first
            // control) and the resulting pace (leg time ÷ distance). Null when a coordinate or leg time
            // is missing. The "to" position comes from the control's code; the "from" is carried along.
            var toPoint = ResolveCoord(context, code);
            var toMap = ResolveMap(context, code);
            var legKm = LegDistanceKm(_distance, context, prevPoint, toPoint, prevMap, toMap);
            var pace = PaceSecondsPerKm(leg, legKm);

            var onCourse = ei < expected.Count
                && string.Equals(code, expected[ei].Trim(), StringComparison.OrdinalIgnoreCase);
            if (onCourse)
            {
                matched[ei] = true;
                ei++;
                taken++;
            }

            passage.Add(new PassagePunch(++index, code, onCourse, punch.Time, leg, elapsed,
                PassageKind.Control, legKm, pace));

            if (punch.Time is not null)
                prev = punch.Time;
            if (toPoint.HasCoordinates)
                prevPoint = toPoint;
            if (toMap.HasCoordinates)
                prevMap = toMap;
        }

        // Finish marker last: leg from the last punch (or start), elapsed = finish − start; the leg
        // distance/pace run from the last position to the finish point.
        var finishLeg = prev is { } fp && context.FinishTime is { } ft ? ft - fp : (TimeSpan?)null;
        var finishElapsed = context.StartTime is { } fs && context.FinishTime is { } ft2 ? ft2 - fs : (TimeSpan?)null;
        var finishKm = LegDistanceKm(_distance, context, prevPoint, context.FinishCoord, prevMap, context.FinishMap);
        var finishPace = PaceSecondsPerKm(finishLeg, finishKm);
        passage.Add(new PassagePunch(0, string.Empty, OnCourse: false,
            context.FinishTime, finishLeg, finishElapsed, PassageKind.Finish, finishKm, finishPace));

        var prescribed = new List<ExpectedControl>(expected.Count);
        for (var i = 0; i < expected.Count; i++)
            prescribed.Add(new ExpectedControl(i + 1, expected[i].Trim(), matched[i]));

        return new SplitsView
        {
            Layout = SplitsLayout.Ordered,
            Passage = passage,
            Expected = prescribed,
            VisitedCount = taken,
            ExpectedCount = expected.Count
        };
    }

}
