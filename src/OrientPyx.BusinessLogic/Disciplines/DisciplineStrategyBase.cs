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
    public abstract DisciplineId Id { get; }

    public virtual string NameKey => "Discipline.Type." + Id.Value;

    public virtual bool IsUnknown => false;

    public virtual CompetitorModel Competitors => CompetitorModel.Individual;

    public virtual bool UsesCourseVariants => false;

    public virtual bool UsesCoursePattern => false;

    public virtual bool AwardsRanks => true;

    public virtual string? PluginId => null;

    public virtual IReadOnlyList<SettingDef> Settings => [];

    public virtual IReadOnlyList<ResultColumnDef> ResultColumns => [];

    public virtual IReadOnlyList<string> RequiredFields => [];

    public virtual bool UsesControlPointPoints => false;

    /// <summary>
    /// Free-order formats don't check start-control clashes by default; the fixed-order disciplines
    /// (set course, mixed, scatter) override this to opt in.
    /// </summary>
    public virtual bool ChecksStartControlClash => false;

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

    /// <summary>Flat count of the course order's control points (see <see cref="CourseOrder.CountControls"/>).</summary>
    public virtual int ControlCount(string courseOrder) => CourseOrder.CountControls(courseOrder);

    /// <summary>Chip start box, else the assigned start time paired with the finish's date (Р-5).</summary>
    public virtual DateTimeOffset? ResolveStart(StartContext context) =>
        context.ChipStart ?? CombineWithFinishDate(context.AssignedStart, context.FinishTime);

    /// <summary>Built-in disciplines leave the time as it is.</summary>
    public virtual TimeSpan AdjustTime(AdjustContext context) => context.Time;

    /// <summary>
    /// Places the group's OK, in-competition results (Р-8): a scoring format orders by score desc then time
    /// asc, every other format by time asc. Equal keys share a place; the next place skips the ties.
    /// </summary>
    public virtual void Rank(GroupRankingContext context)
    {
        var order = Ranking.ByScoreThenTime<RankEntry>(r => r.Scored ? r.Score : 0, r => r.Time);
        foreach (var (r, place) in Ranking.AssignPlaces(context.Entries, order))
            context.Results[r.LinkId] = context.Results[r.LinkId] with { Place = place };
    }

    // Pairs an assigned start time-of-day with the read-out finish's date, so the time-limit check
    // compares two same-day timestamps. Null when either part is missing.
    internal static DateTimeOffset? CombineWithFinishDate(TimeSpan? startOfDay, DateTimeOffset? finish) =>
        startOfDay is { } t && finish is { } f ? new DateTimeOffset(f.Date + t, f.Offset) : null;

    /// <summary>
    /// No finish evaluation by default — the score formats don't judge by order/finish-punch and will
    /// get their own rules later. Set-course overrides this.
    /// </summary>
    public virtual FinishStatusResult EvaluateFinish(FinishContext context) => FinishStatusResult.Of(FinishStatus.None);

    /// <summary>
    /// Every discipline states its own splits shape: the ordered layout (set course, mixed, scatter, rogaine)
    /// or the scored one (the score formats, see <see cref="BuildScoredSplits"/>).
    /// </summary>
    public abstract SplitsView BuildSplits(SplitsContext context);

    /// <summary>
    /// Shared scored-layout builder of the score formats («за вибором»). <see cref="SplitsView.Entries"/> (the
    /// splits panel) lists the controls the chip punched, in passage order, each with its point value, the
    /// time and a running total, followed by the allowed controls that were not visited (greyed, no time).
    /// <see cref="SplitsView.Passage"/> (the split slip and the HTML splits) is the free-order passage — every
    /// punch in the order taken, with its leg, distance and pace (see <see cref="BuildFreeOrderPassage"/>). A
    /// control is counted once even if punched twice (the first punch scores); only controls in the allowed
    /// set count toward points. <paramref name="defaultPenaltyRate"/> is the points-per-late-minute used when
    /// the group sets none (null = no penalty unless the group set one); the penalty is subtracted (see
    /// <see cref="PenaltyFor"/>) into <see cref="SplitsView.TotalPoints"/> (the net), while
    /// <see cref="SplitsView.GrossPoints"/>/<see cref="SplitsView.Penalty"/> keep the "X − Y = Z" parts.
    /// </summary>
    protected SplitsView BuildScoredSplits(
        SplitsContext context, ICourseDistanceCalculator distance, decimal? defaultPenaltyRate)
    {
        var run = BuildFreeOrderPassage(context, distance, defaultPenaltyRate);

        // Visited controls in passage order: the scoring punches (foreign punches and repeats score nothing).
        var entries = new List<ScoreEntry>();
        foreach (var p in run.Passage)
            if (p.Kind == PassageKind.Control && p.OnCourse)
                entries.Add(new ScoreEntry(p.Code, Visited: true, p.Points ?? 0, p.Time, p.Elapsed, p.RunningTotal ?? 0));

        // Allowed controls that were never punched, in course order, greyed (no time, total unchanged).
        foreach (var expected in context.ExpectedControls)
        {
            var code = expected.Trim();
            if (code.Length == 0 || run.Scored.Contains(code))
                continue;

            var points = context.PointsByCode.TryGetValue(code, out var p) ? p : 0;
            entries.Add(new ScoreEntry(code, Visited: false, points, PunchTime: null, Elapsed: null, run.Gross));
        }

        return new SplitsView
        {
            Layout = SplitsLayout.Scored,
            Entries = entries,
            Passage = run.Passage,
            GrossPoints = run.Gross,
            Penalty = run.Penalty,
            TotalPoints = run.Gross - run.Penalty,
            // Only a format that actually values its controls reports a points total; «за вибором по
            // кількості КП» scores none, so its summary stays a plain "taken / of".
            HasPoints = UsesControlPointPoints,
            VisitedCount = run.Scored.Count,
            ExpectedCount = run.AllowedCount
        };
    }

    /// <summary>
    /// The free-order passage shared by the rogaine and the score formats: every punch the chip recorded, in
    /// punch order (the order the competitor took the controls), bracketed by start/finish markers, each with
    /// its leg split, straight-line distance and pace measured from the previous punch. A punch on an allowed
    /// control taken for the first time scores its points (on course, running total); a foreign or repeated
    /// punch is listed but scores nothing. The over-time penalty (<see cref="PenaltyFor"/>, with
    /// <paramref name="defaultPenaltyRate"/> when the group sets no rate) shows on the finish marker as negative
    /// points beside the net running total. In a rogaine team a scoring punch on a control the whole team took
    /// is flagged <see cref="PassagePunch.CountsForTeam"/>.
    /// </summary>
    protected static FreeOrderPassage BuildFreeOrderPassage(
        SplitsContext context, ICourseDistanceCalculator distance, decimal? defaultPenaltyRate)
    {
        var allowed = new HashSet<string>(
            context.ExpectedControls.Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase);

        var scored = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // allowed controls already counted
        var total = 0;
        DateTimeOffset? prev = context.StartTime;
        var prevPoint = context.StartCoord;
        var prevMap = context.StartMap;

        var passage = new List<PassagePunch>(context.Punches.Count + 2);
        var index = 0;

        // Start marker first (its time is the reference point — no leg/elapsed).
        passage.Add(new PassagePunch(0, string.Empty, OnCourse: false,
            context.StartTime, Leg: null, Elapsed: TimeSpan.Zero, PassageKind.Start));

        foreach (var punch in context.Punches)
        {
            var code = punch.ControlCode.Trim();
            if (code.Length == 0)
                continue;

            var leg = prev is { } pr && punch.Time is { } t ? t - pr : (TimeSpan?)null;
            var elapsed = context.StartTime is { } s && punch.Time is { } t2 ? t2 - s : (TimeSpan?)null;

            var toPoint = ResolveCoord(context, code);
            var toMap = ResolveMap(context, code);
            var legKm = LegDistanceKm(distance, context, prevPoint, toPoint, prevMap, toMap);
            var pace = PaceSecondsPerKm(leg, legKm);

            // Scores only when it's an allowed control taken for the first time (a repeat counts once,
            // a foreign punch never). "On course" mirrors that: a scoring punch is the on-course glyph.
            var scores = allowed.Contains(code) && scored.Add(code);
            int? points = null;
            int? running = null;
            if (scores)
            {
                points = context.PointsByCode.TryGetValue(code, out var p) ? p : 0;
                total += points.Value;
                running = total;
            }

            // Team annotation (no effect on the personal total): a scoring punch on a control the whole
            // team also took counts toward the team result. Only meaningful in a team context.
            var countsForTeam = scores && context.TeamCommonControls?.Contains(code) == true;

            passage.Add(new PassagePunch(++index, code, OnCourse: scores, punch.Time, leg, elapsed,
                PassageKind.Control, legKm, pace, points, running, countsForTeam));

            if (punch.Time is not null)
                prev = punch.Time;
            if (toPoint.HasCoordinates)
                prevPoint = toPoint;
            if (toMap.HasCoordinates)
                prevMap = toMap;
        }

        var penalty = PenaltyFor(context, total, defaultPenaltyRate);
        var net = total - penalty;

        // Finish marker last: leg from the last punch (or start), elapsed = finish − start, and the over-time
        // penalty as a negative "Points" with the net running total, so the passage reads "… −Y" beside it.
        var finishLeg = prev is { } fp && context.FinishTime is { } ft ? ft - fp : (TimeSpan?)null;
        var finishElapsed = context.StartTime is { } fs && context.FinishTime is { } ft2 ? ft2 - fs : (TimeSpan?)null;
        var finishKm = LegDistanceKm(distance, context, prevPoint, context.FinishCoord, prevMap, context.FinishMap);
        var finishPace = PaceSecondsPerKm(finishLeg, finishKm);
        passage.Add(new PassagePunch(0, string.Empty, OnCourse: false,
            context.FinishTime, finishLeg, finishElapsed, PassageKind.Finish, finishKm, finishPace,
            Points: penalty > 0 ? -penalty : (int?)null,
            RunningTotal: penalty > 0 ? net : (int?)null));

        return new FreeOrderPassage(passage, scored, allowed.Count, total, penalty);
    }

    /// <summary>
    /// A built free-order passage: the rows, the allowed controls that scored, how many controls the course
    /// allows, the gross points and the over-time penalty (net = gross − penalty).
    /// </summary>
    protected sealed record FreeOrderPassage(
        IReadOnlyList<PassagePunch> Passage, IReadOnlySet<string> Scored, int AllowedCount, int Gross, int Penalty);

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
    /// punch in chip order, each flagged on-course by an optimal <b>subsequence</b> alignment against
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

        var assignment = CourseMatcher.AlignOrder(context.Punches.Select(p => p.ControlCode).ToList(), expected);

        for (var pi = 0; pi < context.Punches.Count; pi++)
        {
            var punch = context.Punches[pi];
            var code = punch.ControlCode.Trim();
            if (code.Length == 0)
                continue;

            var matchedIndex = assignment[pi];
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
                taken++;
            }

            passage.Add(new PassagePunch(++index, code, onCourse, punch.Time, leg, elapsed,
                PassageKind.Control, legKm, pace,
                DisplayLeg: displayLeg, DisplayLegKm: displayKm, DisplayPace: displayPace,
                // Which prescribed control this punch took — the splits table keys its columns on this, so a
                // course that visits one code twice (55 … 55) keeps a separate time under each occurrence.
                CourseIndex: onCourse ? matchedIndex : null));

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

    // --- Shared leg geometry (used by the ordered set-course and rogaine layouts)

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
