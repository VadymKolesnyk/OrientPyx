using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services.Results;

namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>
/// Rogaine (рогейн): a long score format. Competitors pick from a list of allowed control points,
/// each worth points, within a (usually long) time limit and are ranked by points collected.
/// </summary>
public sealed class RogaineStrategy : DisciplineStrategyBase
{
    private readonly ICourseDistanceCalculator _distance;

    public RogaineStrategy(ICourseDistanceCalculator distance) => _distance = distance;

    public override DisciplineId Id => DisciplineId.Rogaine;

    public override bool UsesControlPointPoints => true;

    /// <summary>Rogaine competitors run as teams (members sharing a team name in a group).</summary>
    public override CompetitorModel Competitors => CompetitorModel.Team;

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

    /// <summary>
    /// Ranks rogaine members by team and writes the team's net «Бали» and place onto every member.
    /// A control scores for the team only when every IN-COMPETITION member punched it (intersection); the
    /// over-time penalty is computed once from the team's largest member time (all members, поза конкурсом
    /// included); the team time (tie-break) is the last finisher. A member with no team is поза конкурсом
    /// (kept teamless, no place). A «поза конкурсом» member stays in the team and earns the team's Бали/Місце
    /// but does NOT count toward the team score or drop the team if non-OK; only when EVERY member is поза
    /// конкурсом does the whole team go unplaced (rules 1–5).
    /// </summary>
    public override void Rank(GroupRankingContext context)
    {
        var members = context.TeamMembers;
        if (members.Count == 0)
            return;

        var pointsByCode = context.PointsByCode;
        var results = context.Results;

        // Members with no team run поза конкурсом — keep their personal score, no place. (Done by simply
        // not ranking them; their result already has Place == null.) Members WITH a team stay in the team
        // even when поза конкурсом — they just don't count toward the score (filtered to `scoring` below).
        var teams = members
            .Where(m => m.TeamKey.Length > 0)
            // The same matching key the rest of the app uses for names (case, look-alike Latin/Cyrillic, spaces):
            // «Альфа» and «альфа» are one team here exactly as they are one block in the protocol.
            .GroupBy(m => (m.GroupId, TextKey.Of(m.TeamKey)));

        // Each ranked team carries its net score and tie-break time, grouped back by GroupId for placing.
        var ranked = new List<(Guid? GroupId, int Net, TimeSpan Time, List<Guid> LinkIds)>();
        foreach (var team in teams)
        {
            var roster = team.ToList();
            var linkIds = roster.Select(m => m.LinkId).ToList();

            // The members who actually count toward the team result. A «поза конкурсом» runner is on the
            // team (still shown, still gets the team place) but is excluded from the score/OK checks below.
            var scoring = roster.Where(m => !m.OutOfCompetition).ToList();

            // When EVERY member is поза конкурсом the whole team runs поза конкурсом — no place.
            if (scoring.Count == 0)
                continue;

            // Rule 1/3: a team with any unfinished or non-OK IN-COMPETITION member is not placed (still
            // keeps personal Бали). A поза конкурсом member's status never drops the team.
            if (scoring.Any(m => !m.Ok))
                continue;

            // Rule 2: a control scores only when every in-competition member punched it — the intersection
            // of their sets. A поза конкурсом member's punches do not count toward the team total.
            var common = new HashSet<string>(scoring[0].Punched, StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < scoring.Count; i++)
                common.IntersectWith(scoring[i].Punched);
            var gross = common.Sum(code => pointsByCode.TryGetValue(code, out var p) ? p : 0);

            // Rule 2: one over-time penalty per team, measured from the largest member time — including a
            // поза конкурсом member (their time still counts toward the team penalty / tie-break). Reuse the
            // splits penalty math by feeding it a synthetic read-out whose elapsed = the max time.
            var maxElapsed = roster.Select(m => m.Elapsed).Max();
            var settings = scoring.Select(m => m.Settings).FirstOrDefault(s => s is not null);
            var afterPenalty = ApplyTeamPenalty(gross, maxElapsed, settings);

            // Points correction («бонус»): the smallest bonus ENTERED by an in-competition team member (an
            // un-entered member does not drag it to 0). Null when nobody entered one ⇒ no correction.
            var teamBonus = scoring.Select(m => m.Bonus).Where(b => b.HasValue).Select(b => b!.Value).DefaultIfEmpty().Min();
            var teamBonusOrNull = scoring.Any(m => m.Bonus.HasValue) ? teamBonus : (int?)null;
            var net = afterPenalty + teamBonus;

            // Rule 5: tie-break by the last finisher's time (the largest member time).
            var time = maxElapsed is { } e && e > TimeSpan.Zero ? e : TimeSpan.MaxValue;
            ranked.Add((team.Key.GroupId, net, time, linkIds));

            // The team's net Бали shows on every member row, with the per-control breakdown (the common
            // controls), the over-time penalty and the points correction for the «Бали» tooltip.
            var breakdown = CourseCodes.ScoreLines(common, pointsByCode);
            var penalty = gross - afterPenalty;
            foreach (var id in linkIds)
                results[id] = results[id] with
                {
                    Score = net,
                    ScoreBreakdown = breakdown,
                    ScoreGross = penalty > 0 ? gross : null,
                    ScorePenalty = penalty > 0 ? penalty : null,
                    Bonus = teamBonusOrNull
                };
        }

        // Place teams within each group: net desc, then team time asc; ties share a place, next skips ties.
        foreach (var group in ranked.GroupBy(r => r.GroupId))
        {
            var ordered = group.OrderByDescending(r => r.Net).ThenBy(r => r.Time).ToList();
            var place = 0;
            var seen = 0;
            (int Net, TimeSpan Time)? prev = null;
            foreach (var team in ordered)
            {
                seen++;
                var key = (team.Net, team.Time);
                if (prev is null || prev.Value.Net != key.Net || prev.Value.Time != key.Time)
                    place = seen;
                prev = key;
                foreach (var id in team.LinkIds)
                    results[id] = results[id] with { Place = place };
            }
        }
    }

    // The team's net «Бали» after the over-time penalty, computed by the splits builder so the rounding/floor
    // rules stay in one place. Feeds it a synthetic context whose finish − start equals the team's largest
    // member time and whose gross is the intersection total (modelled as one control worth `gross`).
    private int ApplyTeamPenalty(int gross, TimeSpan? maxElapsed, GroupDaySettings? settings)
    {
        if (maxElapsed is not { } elapsed || settings?.TimeLimitSeconds is null)
            return gross; // no resolvable time or no limit ⇒ no penalty

        var start = DateTimeOffset.UnixEpoch;
        var context = new SplitsContext
        {
            ExpectedControls = ["__team__"],
            Punches = [new ChipPunch("__team__", start)],
            PointsByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["__team__"] = gross },
            StartTime = start,
            FinishTime = start + elapsed,
            TimeLimit = TimeSpan.FromSeconds(settings.TimeLimitSeconds.Value),
            PenaltyPerMinute = settings.PenaltyPerMinute
        };
        return BuildSplits(context).TotalPoints;
    }
}
