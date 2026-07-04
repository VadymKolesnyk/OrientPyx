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
    public override SplitsView BuildSplits(SplitsContext context)
    {
        var expected = context.ExpectedControls;
        var matched = new bool[expected.Count];
        var ei = 0;                  // next prescribed control still to be taken
        // The previous on-course control (time + position), seeded at the start, used to measure a leg only
        // when it is the prescribed predecessor of the current control. Position prefers the undistorted
        // paper-map mm and falls back to the geographic coordinate. Off-course/gap legs carry no split.
        DateTimeOffset? prevOnCourse = context.StartTime;
        var prevOnCoursePoint = context.StartCoord;
        var prevOnCourseMap = context.StartMap;
        var lastOnCourseIndex = -1;  // prescribed index of the previous on-course control (-1 = the start)
        var taken = 0;

        // The immediately preceding punch in chip order (time + position), seeded at the start. Unlike the
        // on-course chain above, this advances on EVERY punch — including extras/off-course ones — so the
        // display leg/distance/pace can be filled for every row (час перегону + довжина always shown).
        DateTimeOffset? prevAny = context.StartTime;
        var prevAnyPoint = context.StartCoord;
        var prevAnyMap = context.StartMap;

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

            // Subsequence match: scan the remaining prescribed controls from the pointer for this code, so a
            // punch can satisfy a later control even when an earlier one was missed (those stay flagged
            // missing). Advancing the pointer past them is what keeps the rest of the run on course.
            var matchedIndex = -1;
            for (var j = ei; j < expected.Count; j++)
            {
                if (string.Equals(code, expected[j].Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    matchedIndex = j;
                    break;
                }
            }
            var onCourse = matchedIndex >= 0;

            var elapsed = context.StartTime is { } s && punch.Time is { } t2 ? t2 - s : (TimeSpan?)null;

            // A leg time is only meaningful when this control's prescribed predecessor was actually taken,
            // so the split spans exactly one prescribed leg. For an on-course control that means the previous
            // on-course control sits immediately before it in the course (the start counts as index -1); when
            // a КП between them was missed the "leg" would span several legs, which is nonsense — drop it (no
            // time, distance, pace) so it isn't shown nor counted in the fastest-leg highlight. An off-course
            // punch never has a meaningful leg either.
            var contiguous = onCourse && matchedIndex == lastOnCourseIndex + 1;

            // The leg time: measure from the previous on-course control for a contiguous on-course leg.
            var leg = contiguous && prevOnCourse is { } pr && punch.Time is { } t ? t - pr : (TimeSpan?)null;

            // Straight-line distance of this leg and the resulting pace (leg time ÷ distance), only for a
            // contiguous on-course leg; null otherwise (a gap leg or an off-course punch).
            var toPoint = ResolveCoord(context, code);
            var toMap = ResolveMap(context, code);
            var legKm = contiguous
                ? LegDistanceKm(_distance, context, prevOnCoursePoint, toPoint, prevOnCourseMap, toMap)
                : null;
            var pace = PaceSecondsPerKm(leg, legKm);

            // Display leg: always measured from the immediately preceding punch (the row above), so every
            // control — extra/off-course included — carries a час перегону, довжина and швидкість and the
            // table keeps its full column structure. (The course leg above stays on-course for the totals.)
            var displayLeg = prevAny is { } pa && punch.Time is { } dt ? dt - pa : (TimeSpan?)null;
            var displayKm = LegDistanceKm(_distance, context, prevAnyPoint, toPoint, prevAnyMap, toMap);
            var displayPace = PaceSecondsPerKm(displayLeg, displayKm);

            if (onCourse)
            {
                // Mark this control taken and advance the pointer past it (and past any controls it skipped,
                // which remain flagged missing) so later punches match the controls that follow.
                matched[matchedIndex] = true;
                ei = matchedIndex + 1;
                taken++;
            }

            passage.Add(new PassagePunch(++index, code, onCourse, punch.Time, leg, elapsed,
                PassageKind.Control, legKm, pace,
                DisplayLeg: displayLeg, DisplayLegKm: displayKm, DisplayPace: displayPace));

            // Advance the on-course chain only on a correct control, so the next on-course leg/distance is
            // measured from here and ignores any extra punches that follow before the next correct control.
            if (onCourse)
            {
                if (punch.Time is not null)
                    prevOnCourse = punch.Time;
                if (toPoint.HasCoordinates)
                    prevOnCoursePoint = toPoint;
                if (toMap.HasCoordinates)
                    prevOnCourseMap = toMap;
                lastOnCourseIndex = matchedIndex;
            }

            // Advance the row-to-row chain on every punch, so the next display leg measures from this one.
            if (punch.Time is not null)
                prevAny = punch.Time;
            if (toPoint.HasCoordinates)
                prevAnyPoint = toPoint;
            if (toMap.HasCoordinates)
                prevAnyMap = toMap;
        }

        // Finish marker last: its leg is only meaningful when the whole course was completed (the last
        // prescribed control was taken last); a missed control before the finish makes the finish leg span
        // a gap, so drop it the same way as the other gap legs. Elapsed = finish − start always shows.
        var finishContiguous = lastOnCourseIndex == expected.Count - 1;
        var finishLeg = finishContiguous && prevOnCourse is { } fp && context.FinishTime is { } ft ? ft - fp : (TimeSpan?)null;
        var finishElapsed = context.StartTime is { } fs && context.FinishTime is { } ft2 ? ft2 - fs : (TimeSpan?)null;
        var finishKm = finishContiguous
            ? LegDistanceKm(_distance, context, prevOnCoursePoint, context.FinishCoord, prevOnCourseMap, context.FinishMap)
            : null;
        var finishPace = PaceSecondsPerKm(finishLeg, finishKm);
        // Display finish leg: from the last punch in chip order to the finish, always (so the F row keeps its
        // час перегону / довжина / швидкість even after an incomplete or out-of-order course).
        var finishDisplayLeg = prevAny is { } fdp && context.FinishTime is { } fdt ? fdt - fdp : (TimeSpan?)null;
        var finishDisplayKm = LegDistanceKm(_distance, context, prevAnyPoint, context.FinishCoord, prevAnyMap, context.FinishMap);
        var finishDisplayPace = PaceSecondsPerKm(finishDisplayLeg, finishDisplayKm);
        passage.Add(new PassagePunch(0, string.Empty, OnCourse: false,
            context.FinishTime, finishLeg, finishElapsed, PassageKind.Finish, finishKm, finishPace,
            DisplayLeg: finishDisplayLeg, DisplayLegKm: finishDisplayKm, DisplayPace: finishDisplayPace));

        var prescribed = new List<ExpectedControl>(expected.Count + context.DisabledControls.Count);
        for (var i = 0; i < expected.Count; i++)
            prescribed.Add(new ExpectedControl(i + 1, expected[i].Trim(), matched[i]));

        // Disabled («проблемні») controls were removed from the prescribed course (so missing them isn't an
        // MP), but still list them — flagged Ignored — so the operator sees the control was dropped for the day.
        foreach (var disabled in context.DisabledControls)
            prescribed.Add(new ExpectedControl(0, disabled.Trim(), Taken: false, Ignored: true));

        return new SplitsView
        {
            Layout = SplitsLayout.Ordered,
            Passage = passage,
            Expected = prescribed,
            DisabledControls = context.DisabledControls,
            VisitedCount = taken,
            ExpectedCount = expected.Count
        };
    }

}
