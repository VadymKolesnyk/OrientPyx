using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>
/// Shared base for the discipline strategies. Provides the common control-point counting and the
/// always-true <see cref="GroupColumn.TimeLimit"/> rule, so each concrete strategy only has to
/// declare the columns specific to it.
/// </summary>
public abstract class DisciplineStrategyBase : IDisciplineStrategy
{
    public abstract DisciplineType Type { get; }

    public string NameKey => "Discipline.Type." + Type;

    public virtual bool UsesControlPointPoints => false;

    /// <summary>No automatic over-time penalty by default; rogaine overrides this with 1 бал/min.</summary>
    public virtual decimal? DefaultPenaltyPerMinute => null;

    /// <summary>
    /// Every discipline shows the (auto-computed) control count and a time limit; concrete strategies
    /// extend this for their own columns.
    /// </summary>
    public virtual bool UsesColumn(GroupColumn column)
        => column is GroupColumn.ControlCount or GroupColumn.TimeLimit;

    /// <summary>By default no participant column is discipline-specific; concrete strategies opt in.</summary>
    public virtual bool UsesParticipantColumn(ParticipantColumn column) => false;

    /// <summary>
    /// Counts control points in a free-text course order, skipping start/finish markers. A token is
    /// treated as a start/finish marker when it begins with a start/finish letter — Latin S/F or the
    /// Cyrillic С(старт)/Ф(фініш), case-insensitive — and is not purely numeric (e.g. "S1", "F",
    /// "Start", "С1", "Ф1"); everything else counts as a control point. Markers in any position are
    /// skipped, so a start/finish that appears mid-order (physically not punched) is not counted.
    /// </summary>
    public virtual int ControlCount(string courseOrder)
    {
        if (string.IsNullOrWhiteSpace(courseOrder))
            return 0;

        var count = 0;
        foreach (var token in courseOrder.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsStartOrFinish(token))
                continue;
            count++;
        }
        return count;
    }

    private static bool IsStartOrFinish(string token)
    {
        // Start/finish letters: Latin S/F and the Cyrillic С (старт, U+0421) / Ф (фініш, U+0424) that
        // Ukrainian OCAD exports use (e.g. "С1"/"Ф1"). Note the Cyrillic С is a distinct code point from
        // the Latin C, so it is listed explicitly.
        var first = token[0];
        if (first is not ('S' or 's' or 'F' or 'f' or 'С' or 'с' or 'Ф' or 'ф'))
            return false;

        // "31" stays a control point; "S1"/"F"/"Start"/"С1"/"Ф1" are markers. Purely numeric tokens never
        // reach here (they don't start with a marker letter), so any non-numeric marker-lettered token is a marker.
        return !ulong.TryParse(token, out _);
    }

    /// <summary>
    /// No finish evaluation by default — the score formats don't judge by order/finish-punch and will
    /// get their own rules later. Set-course overrides this.
    /// </summary>
    public virtual FinishStatusResult EvaluateFinish(FinishContext context) => FinishStatusResult.Of(FinishStatus.None);

    /// <summary>
    /// Default splits layout is the scored one (score / choice / rogaine): the controls the chip
    /// punched, in passage order, each with its point value, the time and a running total — followed by
    /// the allowed controls that were not visited (greyed, no time). Order is just the passage; there is
    /// no "right/wrong order" for a free-choice format. Set course overrides this with the ordered layout.
    /// </summary>
    public virtual SplitsView BuildSplits(SplitsContext context) => BuildScoredSplits(context);

    /// <summary>
    /// Shared scored-layout builder used by every free-choice discipline. A control is counted once even
    /// if punched twice (the first punch scores); only controls in the allowed set count toward points.
    /// No over-time penalty by default; rogaine passes a non-null rate (see <see cref="BuildScoredSplits(SplitsContext, decimal?)"/>).
    /// </summary>
    protected static SplitsView BuildScoredSplits(SplitsContext context) => BuildScoredSplits(context, defaultPenaltyRate: null);

    /// <summary>
    /// Shared scored-layout builder, with an optional over-time penalty. <paramref name="defaultPenaltyRate"/>
    /// is the points-per-late-minute used when the group sets none (rogaine defaults to 1; the score formats
    /// pass null = no penalty unless the group set one). The gross points are tallied as before; the penalty
    /// is then subtracted (see <see cref="PenaltyFor"/>) into <see cref="SplitsView.TotalPoints"/> (the net),
    /// while <see cref="SplitsView.GrossPoints"/>/<see cref="SplitsView.Penalty"/> keep the "X − Y = Z" parts.
    /// </summary>
    protected static SplitsView BuildScoredSplits(SplitsContext context, decimal? defaultPenaltyRate)
    {
        var allowed = new HashSet<string>(
            context.ExpectedControls.Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase);

        var entries = new List<ScoreEntry>();
        var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = 0;

        // Visited controls in passage order. Skip foreign punches (not in the allowed set) and repeats.
        foreach (var punch in context.Punches)
        {
            var code = punch.ControlCode.Trim();
            if (code.Length == 0 || !allowed.Contains(code) || !counted.Add(code))
                continue;

            var points = context.PointsByCode.TryGetValue(code, out var p) ? p : 0;
            total += points;
            var elapsed = context.StartTime is { } s && punch.Time is { } t ? t - s : (TimeSpan?)null;
            entries.Add(new ScoreEntry(code, Visited: true, points, punch.Time, elapsed, total));
        }

        // Allowed controls that were never punched, in course order, greyed (no time, total unchanged).
        foreach (var expected in context.ExpectedControls)
        {
            var code = expected.Trim();
            if (code.Length == 0 || counted.Contains(code))
                continue;

            var points = context.PointsByCode.TryGetValue(code, out var p) ? p : 0;
            entries.Add(new ScoreEntry(code, Visited: false, points, PunchTime: null, Elapsed: null, total));
        }

        var penalty = PenaltyFor(context, total, defaultPenaltyRate);
        return new SplitsView
        {
            Layout = SplitsLayout.Scored,
            Entries = entries,
            GrossPoints = total,
            Penalty = penalty,
            TotalPoints = total - penalty,
            VisitedCount = counted.Count,
            ExpectedCount = allowed.Count
        };
    }

    /// <summary>
    /// Over-time points penalty for a scored result: minutes finished past the group's time limit
    /// (<see cref="SplitsContext.TimeLimit"/>) rounded <b>up</b> — so 30 s late is 1 minute, 1 min 1 s is
    /// 2 minutes — times the penalty rate (<see cref="SplitsContext.PenaltyPerMinute"/>, falling back to
    /// <paramref name="defaultRate"/> when the group set none). Rounded up to a whole point and never larger
    /// than <paramref name="gross"/> (the net result floors at 0). Zero when there is no limit, the finish
    /// is within it, the start/finish is unknown, or the effective rate is not positive.
    /// </summary>
    protected static int PenaltyFor(SplitsContext context, int gross, decimal? defaultRate)
    {
        var rate = context.PenaltyPerMinute ?? defaultRate ?? 0m;
        if (rate <= 0m
            || context.TimeLimit is not { } limit || limit <= TimeSpan.Zero
            || context.StartTime is not { } start || context.FinishTime is not { } finish)
            return 0;

        var over = finish - start - limit;
        if (over <= TimeSpan.Zero)
            return 0;

        var lateMinutes = (long)Math.Ceiling(over.TotalSeconds / 60d);
        var penalty = (int)Math.Min(gross, Math.Ceiling(lateMinutes * rate));
        return Math.Max(0, penalty);
    }

    /// <summary>
    /// Builds the ordered (set-course-style) splits view for a prescribed <paramref name="expected"/> control
    /// order — shared by the set-course discipline (which passes <c>context.ExpectedControls</c>) and the
    /// scatter discipline (which passes the auto-detected variant's controls). The <b>passage</b> keeps every
    /// punch in chip order, each flagged on-course by a greedy <b>subsequence</b> match against
    /// <paramref name="expected"/> (a missed КП in the middle does not off-course every later control). The
    /// <b>course leg/pace</b> is filled only for a contiguous on-course control (its prescribed predecessor was
    /// taken), measured from that previous on-course control; every row additionally carries a <b>display</b>
    /// leg/distance/pace from the immediately preceding punch so the panel/slip keep their columns. The
    /// <b>expected</b> list is the prescribed order flagged taken/missing, with the day's disabled controls
    /// appended (flagged «вимкнено»). This is a straight refactor of the original set-course builder.
    /// </summary>
    protected static SplitsView BuildOrderedSplits(
        SplitsContext context, IReadOnlyList<string> expected, ICourseDistanceCalculator distance,
        string variantCode = "")
    {
        var matched = new bool[expected.Count];
        var ei = 0;                  // next prescribed control still to be taken
        DateTimeOffset? prevOnCourse = context.StartTime;
        var prevOnCoursePoint = context.StartCoord;
        var prevOnCourseMap = context.StartMap;
        var lastOnCourseIndex = -1;  // prescribed index of the previous on-course control (-1 = the start)
        var taken = 0;

        DateTimeOffset? prevAny = context.StartTime;
        var prevAnyPoint = context.StartCoord;
        var prevAnyMap = context.StartMap;

        var passage = new List<PassagePunch>(context.Punches.Count + 2);
        var index = 0;               // 1-based control number; the start/finish markers don't consume one

        passage.Add(new PassagePunch(0, string.Empty, OnCourse: false,
            context.StartTime, Leg: null, Elapsed: TimeSpan.Zero, PassageKind.Start));

        foreach (var punch in context.Punches)
        {
            var code = punch.ControlCode.Trim();
            if (code.Length == 0)
                continue;

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
            var contiguous = onCourse && matchedIndex == lastOnCourseIndex + 1;
            var leg = contiguous && prevOnCourse is { } pr && punch.Time is { } t ? t - pr : (TimeSpan?)null;

            var toPoint = ResolveCoord(context, code);
            var toMap = ResolveMap(context, code);
            var legKm = contiguous
                ? LegDistanceKm(distance, context, prevOnCoursePoint, toPoint, prevOnCourseMap, toMap)
                : null;
            var pace = PaceSecondsPerKm(leg, legKm);

            var displayLeg = prevAny is { } pa && punch.Time is { } dt ? dt - pa : (TimeSpan?)null;
            var displayKm = LegDistanceKm(distance, context, prevAnyPoint, toPoint, prevAnyMap, toMap);
            var displayPace = PaceSecondsPerKm(displayLeg, displayKm);

            if (onCourse)
            {
                matched[matchedIndex] = true;
                ei = matchedIndex + 1;
                taken++;
            }

            passage.Add(new PassagePunch(++index, code, onCourse, punch.Time, leg, elapsed,
                PassageKind.Control, legKm, pace,
                DisplayLeg: displayLeg, DisplayLegKm: displayKm, DisplayPace: displayPace));

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

            if (punch.Time is not null)
                prevAny = punch.Time;
            if (toPoint.HasCoordinates)
                prevAnyPoint = toPoint;
            if (toMap.HasCoordinates)
                prevAnyMap = toMap;
        }

        var finishContiguous = lastOnCourseIndex == expected.Count - 1;
        var finishLeg = finishContiguous && prevOnCourse is { } fp && context.FinishTime is { } ft ? ft - fp : (TimeSpan?)null;
        var finishElapsed = context.StartTime is { } fs && context.FinishTime is { } ft2 ? ft2 - fs : (TimeSpan?)null;
        var finishKm = finishContiguous
            ? LegDistanceKm(distance, context, prevOnCoursePoint, context.FinishCoord, prevOnCourseMap, context.FinishMap)
            : null;
        var finishPace = PaceSecondsPerKm(finishLeg, finishKm);
        var finishDisplayLeg = prevAny is { } fdp && context.FinishTime is { } fdt ? fdt - fdp : (TimeSpan?)null;
        var finishDisplayKm = LegDistanceKm(distance, context, prevAnyPoint, context.FinishCoord, prevAnyMap, context.FinishMap);
        var finishDisplayPace = PaceSecondsPerKm(finishDisplayLeg, finishDisplayKm);
        passage.Add(new PassagePunch(0, string.Empty, OnCourse: false,
            context.FinishTime, finishLeg, finishElapsed, PassageKind.Finish, finishKm, finishPace,
            DisplayLeg: finishDisplayLeg, DisplayLegKm: finishDisplayKm, DisplayPace: finishDisplayPace));

        var prescribed = new List<ExpectedControl>(expected.Count + context.DisabledControls.Count);
        for (var i = 0; i < expected.Count; i++)
            prescribed.Add(new ExpectedControl(i + 1, expected[i].Trim(), matched[i]));

        foreach (var disabled in context.DisabledControls)
            prescribed.Add(new ExpectedControl(0, disabled.Trim(), Taken: false, Ignored: true));

        return new SplitsView
        {
            Layout = SplitsLayout.Ordered,
            Passage = passage,
            Expected = prescribed,
            DisabledControls = context.DisabledControls,
            VisitedCount = taken,
            ExpectedCount = expected.Count,
            VariantCode = variantCode
        };
    }

    // --- Shared leg geometry (used by the ordered set-course and rogaine layouts) ------------------

    /// <summary>The geographic coordinate of a control code, or a coordinate-less point when unknown.</summary>
    protected static GeoPoint ResolveCoord(SplitsContext context, string code) =>
        context.CoordsByCode.TryGetValue(code, out var p) ? p : default;

    /// <summary>The paper-map position of a control code, or a position-less point when unknown.</summary>
    protected static MapPoint ResolveMap(SplitsContext context, string code) =>
        context.MapByCode.TryGetValue(code, out var p) ? p : default;

    /// <summary>
    /// Straight-line distance (km) of the leg between two positions, via the shared course distance
    /// calculator. Prefers the paper-map source (map mm × scale, undistorted) when both endpoints are
    /// mapped and a scale is known; otherwise falls back to the geographic coordinates. Null when neither
    /// source covers both endpoints.
    /// </summary>
    protected static decimal? LegDistanceKm(
        ICourseDistanceCalculator distance, SplitsContext context,
        GeoPoint from, GeoPoint to, MapPoint fromMap, MapPoint toMap)
    {
        if (context.MapScale is > 0 && fromMap.HasCoordinates && toMap.HasCoordinates)
        {
            var mapKm = distance.TotalKilometresFromMap([fromMap, toMap], context.MapScale.Value);
            return mapKm > 0m ? mapKm : null;
        }

        if (!from.HasCoordinates || !to.HasCoordinates)
            return null;

        var km = distance.TotalKilometres([from, to]);
        return km > 0m ? km : null;
    }

    /// <summary>Pace in seconds per kilometre for one leg; null when the leg time or distance is missing/zero.</summary>
    protected static double? PaceSecondsPerKm(TimeSpan? leg, decimal? legKm)
    {
        if (leg is not { } t || t <= TimeSpan.Zero || legKm is not { } km || km <= 0m)
            return null;
        return t.TotalSeconds / (double)km;
    }
}
