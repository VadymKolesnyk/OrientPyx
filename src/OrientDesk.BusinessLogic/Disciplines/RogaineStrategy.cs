using OrientDesk.BusinessLogic.Enums;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Disciplines;

/// <summary>
/// Rogaine (рогейн): a long score format. Competitors pick from a list of allowed control points,
/// each worth points, within a (usually long) time limit and are ranked by points collected.
/// </summary>
public sealed class RogaineStrategy : DisciplineStrategyBase
{
    private readonly ICourseDistanceCalculator _distance;

    public RogaineStrategy(ICourseDistanceCalculator distance) => _distance = distance;

    public override DisciplineType Type => DisciplineType.Rogaine;

    public override bool UsesControlPointPoints => true;

    /// <summary>Rogaine deducts 1 бал per started minute over the time limit unless the group sets another rate.</summary>
    public override decimal? DefaultPenaltyPerMinute => 1m;

    public override bool UsesColumn(GroupColumn column) => column switch
    {
        GroupColumn.CourseOrder => true,           // the list of allowed control points
        GroupColumn.PenaltyPerMinute => true,      // points lost per (started) minute over the time limit
        _ => base.UsesColumn(column)
    };

    public override bool UsesParticipantColumn(ParticipantColumn column) => column switch
    {
        ParticipantColumn.Team => true,            // rogaine competitors run as teams
        _ => base.UsesParticipantColumn(column)
    };

    /// <summary>
    /// Rogaine is a free-choice score format: there is no prescribed order to break, so the only finish
    /// rule is whether the competitor finished. A finish punch ⇒ <see cref="FinishStatus.Ok"/> (which lets
    /// the result be placed, ranked by net «Бали»); no finish punch ⇒ <see cref="FinishStatus.Dnf"/>.
    /// Running over the time limit does <b>not</b> change the status — it only deducts points (see
    /// <see cref="BuildSplits"/>) — so an over-time competitor still finishes OK with a reduced score.
    /// </summary>
    public override FinishStatusResult EvaluateFinish(FinishContext context) =>
        FinishStatusResult.Of(context.FinishTime is null ? FinishStatus.Dnf : FinishStatus.Ok);

    /// <summary>
    /// Rogaine splits as two parallel lists, mirroring the set-course layout but scored. The <b>passage</b>
    /// is every punch the chip recorded, in punch order (the order the competitor visited controls),
    /// bracketed by start/finish markers and carrying each leg's split, distance and pace. A punch on an
    /// allowed control (taken for the first time) carries its point value and advances the running total;
    /// a foreign or repeated punch is shown but scores nothing. The <b>expected</b> list is every allowed
    /// control of the course, sorted by ascending control number, each flagged taken/not and carrying its
    /// point value — the catalogue of what could be punched, beside what was.
    /// </summary>
    public override SplitsView BuildSplits(SplitsContext context)
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
            var legKm = LegDistanceKm(_distance, context, prevPoint, toPoint, prevMap, toMap);
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

        // Over-time penalty (−1 бал per started late minute by default): deducted from the gross total to
        // give the net result. Shown on the finish marker as a negative "Points" with the net running total,
        // so the passage reads "… −Y" beside the finish and the summary/printout can spell out "X − Y = Z".
        var penalty = PenaltyFor(context, total, DefaultPenaltyPerMinute);
        var net = total - penalty;

        // Finish marker last: leg from the last punch (or start), elapsed = finish − start.
        var finishLeg = prev is { } fp && context.FinishTime is { } ft ? ft - fp : (TimeSpan?)null;
        var finishElapsed = context.StartTime is { } fs && context.FinishTime is { } ft2 ? ft2 - fs : (TimeSpan?)null;
        var finishKm = LegDistanceKm(_distance, context, prevPoint, context.FinishCoord, prevMap, context.FinishMap);
        var finishPace = PaceSecondsPerKm(finishLeg, finishKm);
        passage.Add(new PassagePunch(0, string.Empty, OnCourse: false,
            context.FinishTime, finishLeg, finishElapsed, PassageKind.Finish, finishKm, finishPace,
            Points: penalty > 0 ? -penalty : (int?)null,
            RunningTotal: penalty > 0 ? net : (int?)null));

        // Course controls: the allowed set sorted by ascending control number (numeric where possible,
        // then by text), each flagged taken/not with its point value.
        var courseControls = allowed
            .OrderBy(c => long.TryParse(c, out var n) ? n : long.MaxValue)
            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var expected = new List<ExpectedControl>(courseControls.Count);
        var seq = 0;
        foreach (var code in courseControls)
        {
            var points = context.PointsByCode.TryGetValue(code, out var p) ? p : 0;
            var countsForTeam = context.TeamCommonControls?.Contains(code) == true;
            expected.Add(new ExpectedControl(++seq, code, scored.Contains(code), points, countsForTeam));
        }

        return new SplitsView
        {
            Layout = SplitsLayout.Ordered,
            Passage = passage,
            Expected = expected,
            GrossPoints = total,
            Penalty = penalty,
            TotalPoints = net,
            HasPoints = true,
            VisitedCount = scored.Count,
            ExpectedCount = allowed.Count
        };
    }
}
