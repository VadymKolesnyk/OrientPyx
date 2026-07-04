using OrientPyx.BusinessLogic.Disciplines.CoursePattern;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>
/// Mixed course (змішаний): the course order is a <b>pattern</b> that mixes a prescribed order with
/// free-choice sections — an ordered run <c>&lt;41 42&gt;</c>, an "any N of" block <c>[2 45 46 47]</c>,
/// and nesting (see <see cref="CoursePattern.CoursePattern"/>). It is judged like a set course but against
/// the pattern: every required control must be present in a valid order, extra/foreign punches ignored.
/// The control count is the number of punches the pattern requires; the splits render the actual passage
/// against the prescribed pattern, each punch flagged on/off course by whether the pattern consumed it.
/// </summary>
public sealed class MixedStrategy : DisciplineStrategyBase
{
    private readonly ICourseDistanceCalculator _distance;

    public MixedStrategy(ICourseDistanceCalculator distance) => _distance = distance;

    public override DisciplineType Type => DisciplineType.Mixed;

    public override bool UsesColumn(GroupColumn column) => column switch
    {
        GroupColumn.CourseOrder => true,           // the order pattern
        _ => base.UsesColumn(column)
    };

    /// <summary>The control count is what the pattern actually requires — every control in an ordered
    /// section, plus N for each <c>[N …]</c> block — not the raw token count.</summary>
    public override int ControlCount(string courseOrder) => CoursePattern.CoursePattern.Parse(courseOrder).RequiredCount;

    /// <summary>
    /// Same priority as a set course — DNF &gt; MP &gt; OVT &gt; OK:
    /// <list type="number">
    ///   <item>no finish punch ⇒ <see cref="FinishStatus.Dnf"/>;</item>
    ///   <item>the pattern is not satisfied by the punches (in a valid order) ⇒ <see cref="FinishStatus.Mp"/>
    ///   with the first control it could not place as the detail;</item>
    ///   <item>a time limit is set and (finish − start) exceeds it ⇒ <see cref="FinishStatus.Ovt"/>;</item>
    ///   <item>otherwise <see cref="FinishStatus.Ok"/>.</item>
    /// </list>
    /// Disabled («проблемні») controls are treated as satisfied wherever the pattern requires them.
    /// </summary>
    public override FinishStatusResult EvaluateFinish(FinishContext context)
    {
        if (context.FinishTime is null)
            return FinishStatusResult.Of(FinishStatus.Dnf);

        var pattern = CoursePattern.CoursePattern.Parse(context.CourseOrderText);
        var ignored = IgnoredCodes(context.ExpectedControls, pattern);
        var ok = pattern.Match(context.PunchedControls, ignored, out _, out var firstMissing);
        if (!ok)
            return new FinishStatusResult(FinishStatus.Mp, firstMissing);

        if (context.TimeLimit is { } limit && context.StartTime is { } start
            && context.FinishTime.Value - start > limit)
            return FinishStatusResult.Of(FinishStatus.Ovt);

        return FinishStatusResult.Of(FinishStatus.Ok);
    }

    /// <summary>
    /// Ordered (set-course-style) splits driven by the pattern. The <b>passage</b> keeps every punch in chip
    /// order; a punch is flagged <see cref="PassagePunch.OnCourse"/> when the pattern consumed it (a required
    /// control taken where the order allows). Every row carries a <b>display</b> leg/distance/pace measured
    /// from the preceding punch, so the read-out panel and the slip always show час перегону/довжина/швидкість.
    /// The <b>expected</b> list is the pattern's controls, each flagged taken/missing. Because a free-choice
    /// block has no single "next" control, this layout does not compute per-leg course splits (only the
    /// row-to-row display legs) — the ordering is judged, the leg times are shown against the actual passage.
    /// </summary>
    public override SplitsView BuildSplits(SplitsContext context)
    {
        var pattern = CoursePattern.CoursePattern.Parse(context.CourseOrderText);
        var ignoredExpected = IgnoredCodes(context.ExpectedControls, pattern);
        var ignored = context.DisabledControls.Count > 0 || ignoredExpected.Count > 0
            ? new HashSet<string>(context.DisabledControls, StringComparer.OrdinalIgnoreCase)
            : (IReadOnlySet<string>)ignoredExpected;

        // Punch codes (trimmed, non-empty) in chip order, mapped back to their original punch for times/coords.
        var punches = context.Punches.Where(p => p.ControlCode.Trim().Length > 0).ToList();
        var codes = punches.Select(p => p.ControlCode.Trim()).ToList();
        pattern.Match(codes, ignored, out var onCourse, out _);

        DateTimeOffset? prevAny = context.StartTime;
        var prevAnyPoint = context.StartCoord;
        var prevAnyMap = context.StartMap;

        var passage = new List<PassagePunch>(punches.Count + 2)
        {
            // Start marker (reference point — no leg/elapsed).
            new(0, string.Empty, OnCourse: false, context.StartTime, Leg: null, Elapsed: TimeSpan.Zero, PassageKind.Start)
        };

        var index = 0;
        for (var i = 0; i < punches.Count; i++)
        {
            var punch = punches[i];
            var code = codes[i];
            var elapsed = context.StartTime is { } s && punch.Time is { } t ? t - s : (TimeSpan?)null;

            var toPoint = ResolveCoord(context, code);
            var toMap = ResolveMap(context, code);
            var displayLeg = prevAny is { } pa && punch.Time is { } dt ? dt - pa : (TimeSpan?)null;
            var displayKm = LegDistanceKm(_distance, context, prevAnyPoint, toPoint, prevAnyMap, toMap);
            var displayPace = PaceSecondsPerKm(displayLeg, displayKm);

            passage.Add(new PassagePunch(++index, code, onCourse[i], punch.Time, Leg: null, elapsed,
                PassageKind.Control, LegKm: null, PaceSecondsPerKm: null,
                DisplayLeg: displayLeg, DisplayLegKm: displayKm, DisplayPace: displayPace));

            if (punch.Time is not null)
                prevAny = punch.Time;
            if (toPoint.HasCoordinates)
                prevAnyPoint = toPoint;
            if (toMap.HasCoordinates)
                prevAnyMap = toMap;
        }

        // Finish marker (elapsed = finish − start; display leg from the last punch to the finish).
        var finishElapsed = context.StartTime is { } fs && context.FinishTime is { } ft ? ft - fs : (TimeSpan?)null;
        var finishDisplayLeg = prevAny is { } fdp && context.FinishTime is { } fdt ? fdt - fdp : (TimeSpan?)null;
        var finishDisplayKm = LegDistanceKm(_distance, context, prevAnyPoint, context.FinishCoord, prevAnyMap, context.FinishMap);
        var finishDisplayPace = PaceSecondsPerKm(finishDisplayLeg, finishDisplayKm);
        passage.Add(new PassagePunch(0, string.Empty, OnCourse: false,
            context.FinishTime, Leg: null, finishElapsed, PassageKind.Finish, LegKm: null, PaceSecondsPerKm: null,
            DisplayLeg: finishDisplayLeg, DisplayLegKm: finishDisplayKm, DisplayPace: finishDisplayPace));

        // Expected = the pattern's controls (in reading order, de-duplicated), each flagged taken when the
        // chip punched it (an ignored/disabled control is listed «вимкнено»). This is the catalogue of the
        // pattern's controls beside the passage; the ordering itself is judged by EvaluateFinish.
        var punchedSet = new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);
        var expected = new List<ExpectedControl>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seq = 0;
        foreach (var code in pattern.ControlCodes)
        {
            if (!seen.Add(code))
                continue;
            var isIgnored = ignored.Contains(code);
            expected.Add(new ExpectedControl(
                isIgnored ? 0 : ++seq, code, Taken: punchedSet.Contains(code), Ignored: isIgnored));
        }

        return new SplitsView
        {
            Layout = SplitsLayout.Ordered,
            Passage = passage,
            Expected = expected,
            DisabledControls = context.DisabledControls,
            VisitedCount = onCourse.Count(x => x),
            ExpectedCount = pattern.RequiredCount
        };
    }

    // The pattern's controls that the caller dropped from ExpectedControls (the flattened allowed list) —
    // i.e. the day's disabled («проблемні») controls that appear in this course. Comparing the pattern's
    // codes against the flattened list recovers them without the strategy needing the day's full disabled set.
    private static IReadOnlySet<string> IgnoredCodes(
        IReadOnlyList<string> expectedControls, CoursePattern.CoursePattern pattern)
    {
        var kept = new HashSet<string>(expectedControls.Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase);
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in pattern.ControlCodes)
            if (!kept.Contains(code))
                ignored.Add(code);
        return ignored;
    }
}
