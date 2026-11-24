using System.Globalization;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services.Iof;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Default <see cref="IIofExportBuilder"/> (plan: <c>docs/iof-xml-export-plan.md</c> §3). Writes the computed results
/// the protocols show — no judging of its own — in the IOF 3.0 shape the format of each group calls for: a person per
/// runner (individual formats), a patrol per team with every member's own result (rogaine, <c>Leg</c> = 1), a team per
/// relay team with a member per leg. Nothing is compared with a discipline id: only the group's
/// <see cref="IofClassInfo"/> answers. A file of several days writes one <c>Race</c> per day and a <c>raceNumber</c> on
/// every result / start; individual groups then carry the running «по сумі днів» total in <c>OverallResult</c>.
/// </summary>
public sealed class IofExportBuilder : IIofExportBuilder
{
    private const string ScoreType = "Score";
    private const string PenaltyType = "Penalty";
    private const string BonusType = "Bonus";
    private const string PointsType = "RankingPoints";

    private static readonly IofClassInfo UnknownClass = new(null, null, CompetitorModel.Individual, false, false);

    private readonly ISummaryProtocolBuilder _summary;

    public IofExportBuilder(ISummaryProtocolBuilder summary)
    {
        _summary = summary;
    }

    // ---------------------------------------------------------------------------------------------- ResultList

    public IofResultList BuildResultList(IofExportData data, IofExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(options);

        var multi = data.Days.Count > 1;
        var totals = multi ? RunningTotals(data) : new Dictionary<string, IofOverallResult>();
        var classes = new ClassBag<ResultClass>(name => new ResultClass(name));

        foreach (var day in data.Days)
        {
            if (day.Results is not { } results)
                continue;
            var race = multi ? day.Race.DayNumber : (int?)null;
            foreach (var group in results.Groups.OrderBy(g => g.Order))
            {
                if (!Included(options, group.Name))
                    continue;
                var info = ClassInfo(day, group.Name);
                var cls = classes.Get(group.Name);
                if (ClassCourse(info, group.Name, race) is { } course)
                    cls.Courses.Add(course);

                switch (info.Competitors)
                {
                    case CompetitorModel.TeamWithLegs:
                        AddRelay(cls, day, group, info, race, options);
                        break;
                    case CompetitorModel.Team:
                        AddPatrols(cls, day, group, info, race, options);
                        break;
                    default:
                        AddIndividuals(cls, day, group, info, race, options, totals);
                        break;
                }
            }
        }

        var status = data.Days.Count > 0 && data.Days.All(d => d.Race.IsLocked)
            ? IofResultListStatus.Complete
            : IofResultListStatus.Snapshot;
        return new IofResultList(
            new IofDocumentHeader(options.Creator, IofClock.Truncate(options.CreateTime)),
            Event(data, options),
            status,
            classes.All
                .Where(c => c.Persons.Count > 0 || c.Teams.Count > 0)
                .Select(c => new IofClassResult(
                    c.Name, c.Courses,
                    c.Persons.Select(p => new IofPersonResult(p.Person, p.Organisation, p.Results)).ToList(),
                    c.Teams))
                .ToList());
    }

    // Individual formats: a person per row — placed first by place, then the rest by name, as in the protocol.
    private static void AddIndividuals(
        ResultClass cls, IofDayExport day, ResultProtocolGroup group, IofClassInfo info, int? race,
        IofExportOptions options, IReadOnlyDictionary<string, IofOverallResult> totals)
    {
        var leader = LeaderTime(group.Rows);
        foreach (var row in OrderedByPlace(group.Rows))
        {
            var runner = day.Runners.GetValueOrDefault(row.ParticipantId);
            var result = PersonResult(day, info, group.Name, row, runner, race, leader) with
            {
                Overall = totals.GetValueOrDefault(TotalKey(day.Race.DayId, row.ParticipantId, group.Name))
            };
            cls.AddPerson(row.ParticipantId, Person(row.FullName, row.BirthDate, runner, options),
                Organisation(row.ClubName, row.RegionName, options), result);
        }
    }

    // Rogaine: a patrol per team (members on leg 1, each with their own result; the team result in OverallResult);
    // the teamless runners (поза конкурсом) as persons.
    private static void AddPatrols(
        ResultClass cls, IofDayExport day, ResultProtocolGroup group, IofClassInfo info, int? race, IofExportOptions options)
    {
        var teams = group.Rows
            .Where(r => r.Team.Length > 0)
            .GroupBy(r => r.Team, TextKey.Comparer)
            .OrderBy(g => g.Min(r => r.Result.Place) is { } p ? p : int.MaxValue)
            .ThenByDescending(g => g.Max(r => r.Result.Score) ?? 0)
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var team in teams)
        {
            var members = team.OrderBy(r => r.FullName, StringComparer.CurrentCultureIgnoreCase).ToList();
            var overall = PatrolResult(day, members);
            var memberResults = new List<IofTeamMemberResult>(members.Count);
            for (var i = 0; i < members.Count; i++)
            {
                var row = members[i];
                var runner = day.Runners.GetValueOrDefault(row.ParticipantId);
                var result = PersonResult(day, info, group.Name, row, runner, race, leader: null, teamScores: false) with
                {
                    Leg = 1,
                    LegOrder = i + 1,
                    Overall = overall
                };
                memberResults.Add(new IofTeamMemberResult(
                    Person(row.FullName, row.BirthDate, runner, options),
                    Organisation(row.ClubName, row.RegionName, options),
                    result));
            }
            cls.Teams.Add(new IofTeamResult(
                members[0].Team, Organisation(members[0].ClubName, members[0].RegionName, options), null, memberResults));
        }

        // A runner of a team format without a team is «поза конкурсом»: an OK run is NotCompeting.
        foreach (var row in group.Rows.Where(r => r.Team.Length == 0)
                     .OrderBy(r => r.FullName, StringComparer.CurrentCultureIgnoreCase))
        {
            var runner = day.Runners.GetValueOrDefault(row.ParticipantId);
            var result = PersonResult(day, info, group.Name, row, runner, race, leader: null);
            if (result.Status == IofResultStatus.OK)
                result = result with { Status = IofResultStatus.NotCompeting, Position = null };
            cls.AddPerson(row.ParticipantId, Person(row.FullName, row.BirthDate, runner, options),
                Organisation(row.ClubName, row.RegionName, options), result);
        }
    }

    // The patrol's result, stamped on every member: the time of its slowest in-competition member (what the protocol's
    // team row shows), its place, OK when placed — else the worst member status (all «П/К» ⇒ NotCompeting) — and its
    // points.
    private static IofOverallResult PatrolResult(IofDayExport day, IReadOnlyList<ResultProtocolRow> members)
    {
        var lead = members
            .Where(r => r.Result.ResultTime is not null && !r.Result.OutOfCompetition)
            .MaxBy(r => r.Result.ResultTime) ?? members[0];
        var place = members.Select(r => r.Result.Place).Where(p => p is not null).DefaultIfEmpty(null).Min();
        var statuses = members
            .Select(r => IofStatusMap.Of(r.Result.Status, r.Result.OutOfCompetition, r.Result.HasReadout, day.Race.IsLocked))
            .ToList();
        var status = place is not null ? IofResultStatus.OK
            : statuses.All(s => s == IofResultStatus.NotCompeting) ? IofResultStatus.NotCompeting
            : statuses.MaxBy(IofStatusMap.Severity);

        var r = lead.Result;
        var scores = new List<IofScore>();
        if (r.Score is { } score)
            scores.Add(new IofScore(ScoreType, score));
        if (r.ScorePenalty is > 0 and { } penalty)
            scores.Add(new IofScore(PenaltyType, penalty));
        if (r.Bonus is { } bonus && bonus != 0)
            scores.Add(new IofScore(BonusType, bonus));
        return new IofOverallResult(
            r.ResultTime is { } t ? IofClock.Seconds(t) : null,
            null,
            status == IofResultStatus.OK ? place : null,
            status,
            scores);
    }

    // Relay: a team per relay team (standing order), a member per leg with a runner — the leg's own start, finish, time,
    // status, course and splits, and in OverallResult the cumulative time and the team status (the team's place and
    // gap on its last run leg); the group's runners on no team as persons.
    private static void AddRelay(
        ResultClass cls, IofDayExport day, ResultProtocolGroup group, IofClassInfo info, int? race, IofExportOptions options)
    {
        var teams = group.RelayTeams ?? [];
        var best = teams
            .Where(t => t.Result is { Place: not null, Time: not null, OutOfCompetition: false })
            .Select(t => t.Result!.Time)
            .DefaultIfEmpty(null)
            .Min();

        foreach (var team in RelayTeamOrder.Sort(teams, t => t.Result, t => t.Number))
        {
            var runs = team.Legs.Where(l => l.Runner is not null).OrderBy(l => l.Leg).ToList();
            if (runs.Count == 0)
                continue;
            var tr = team.Result;
            var teamStatus = tr is null
                ? IofStatusMap.OpenTeam(runs.Any(l => l.Runner!.Result.HasReadout), day.Race.IsLocked)
                : tr.Status == FinishStatus.None
                    ? IofStatusMap.OpenTeam(runs.Any(l => l.Runner!.Result.HasReadout), day.Race.IsLocked)
                    : IofStatusMap.Of(tr.Status, tr.OutOfCompetition, hasReadout: true, day.Race.IsLocked);
            var lastLeg = runs[^1].Leg;

            var members = new List<IofTeamMemberResult>(runs.Count);
            foreach (var leg in runs)
            {
                var row = leg.Runner!;
                var runner = day.Runners.GetValueOrDefault(row.ParticipantId);
                var r = row.Result;
                var lr = r.Leg;
                var status = IofStatusMap.Of(lr?.Status ?? r.Status, outOfCompetition: false, r.HasReadout, day.Race.IsLocked);
                var (start, finish) = IofClock.Stamps(day.Race, lr?.Start, lr?.Finish ?? r.FinishTime);
                var legTime = lr?.AdjustedTime ?? lr?.LegTime;
                int? time = status == IofResultStatus.OK && legTime is { } lt ? IofClock.Seconds(lt)
                    : lr?.Start is { } ls && (lr.Finish ?? r.FinishTime) is { } lf ? IofClock.Seconds(lf - ls)
                    : null;

                var isLast = leg.Leg == lastLeg;
                var teamOk = teamStatus == IofResultStatus.OK;
                var overall = new IofOverallResult(
                    lr?.Cumulative is { } c ? IofClock.Seconds(c) : null,
                    isLast && teamOk && tr is { Place: not null, Time: { } tt } && best is { } b ? IofClock.Seconds(tt - b) : null,
                    isLast && teamOk ? tr?.Place : null,
                    teamStatus,
                    []);

                members.Add(new IofTeamMemberResult(
                    Person(row.FullName, row.BirthDate, runner, options),
                    Organisation(row.ClubName, row.RegionName, options),
                    new IofRaceResult
                    {
                        RaceNumber = race,
                        Leg = leg.Leg,
                        Bib = Blank(row.Number),
                        Start = start,
                        Finish = finish,
                        TimeSeconds = time,
                        Status = status,
                        Scores = Points(r),
                        Overall = overall,
                        Course = RunnerCourse(runner, group.Name),
                        Splits = IofSplitList.From(runner?.Splits, info.UsesScore),
                        Chip = Chip(runner)
                    }));
            }

            cls.Teams.Add(new IofTeamResult(
                team.Name.Trim(),
                Organisation(team.ClubName, team.RegionName, options),
                team.Number?.ToString(CultureInfo.InvariantCulture),
                members));
        }

        // A relay runner on no team (or on no leg) is judged alone and never placed: an OK run is NotCompeting.
        foreach (var row in group.Rows.Where(r => r.Result.Team is null)
                     .OrderBy(r => r.FullName, StringComparer.CurrentCultureIgnoreCase))
        {
            var runner = day.Runners.GetValueOrDefault(row.ParticipantId);
            var result = PersonResult(day, info, group.Name, row, runner, race, leader: null);
            if (result.Status == IofResultStatus.OK)
                result = result with { Status = IofResultStatus.NotCompeting, Position = null };
            cls.AddPerson(row.ParticipantId, Person(row.FullName, row.BirthDate, runner, options),
                Organisation(row.ClubName, row.RegionName, options), result);
        }
    }

    // One runner's own race result (individual, patrol member, relay runner off a team).
    private static IofRaceResult PersonResult(
        IofDayExport day, IofClassInfo info, string groupName, ResultProtocolRow row, IofRunnerData? runner,
        int? race, TimeSpan? leader, bool teamScores = true)
    {
        var r = row.Result;
        var status = IofStatusMap.Of(r.Status, r.OutOfCompetition, r.HasReadout, day.Race.IsLocked);

        DateTimeOffset? start;
        DateTimeOffset? finish;
        if (r.HasReadout)
            (start, finish) = IofClock.Stamps(day.Race, runner?.ResolvedStart, r.FinishTime);
        else
        {
            start = runner?.AssignedStart is { } assigned ? IofClock.OnDay(day.Race, assigned) : null;
            finish = null;
        }

        int? time = r.Status == FinishStatus.Ok && r.RankingTime is { } rt ? IofClock.Seconds(rt)
            : runner?.ResolvedStart is { } rs && r.FinishTime is { } ft ? IofClock.Seconds(ft - rs)
            : null;
        var ok = status == IofResultStatus.OK;
        int? behind = ok && !info.UsesScore && r.Place is not null && r.RankingTime is { } own && leader is { } lead
            ? IofClock.Seconds(own - lead)
            : null;

        var scores = new List<IofScore>();
        if (info.UsesScore && teamScores)
        {
            if (r.Score is { } score)
                scores.Add(new IofScore(ScoreType, score));
            if (r.ScorePenalty is > 0 and { } penalty)
                scores.Add(new IofScore(PenaltyType, penalty));
            if (r.Bonus is { } bonus && bonus != 0)
                scores.Add(new IofScore(BonusType, bonus));
        }
        scores.AddRange(Points(r));

        return new IofRaceResult
        {
            RaceNumber = race,
            Bib = Blank(row.Number),
            Start = start,
            Finish = finish,
            TimeSeconds = time,
            TimeBehindSeconds = behind,
            Position = ok ? r.Place : null,
            Status = status,
            Scores = scores,
            Course = RunnerCourse(runner, groupName),
            Splits = IofSplitList.From(runner?.Splits, info.UsesScore),
            Chip = Chip(runner)
        };
    }

    // «Очки» of the points rule, in any group.
    private static IReadOnlyList<IofScore> Points(ParticipantDayResult r) =>
        r.Points is { } points ? [new IofScore(PointsType, points)] : [];

    // The «по сумі днів» total after each day of a multi-day file (plan §3.10), for the individual groups: the
    // «Підсумковий протокол» settings unchanged, but only the counted days up to that one; a day the summary does not
    // count gets none; a priority day still to come falls back to the first counted day. Keyed by TotalKey.
    private Dictionary<string, IofOverallResult> RunningTotals(IofExportData data)
    {
        var totals = new Dictionary<string, IofOverallResult>(StringComparer.Ordinal);
        if (data.Summary is not { } summary)
            return totals;
        var settings = data.SummarySettings ?? new SummaryProtocolSettings();

        var numberOf = summary.Days.ToDictionary(d => d.Id, d => d.Number);
        var counted = settings.Days.Count > 0
            ? settings.Days.Where(d => d.Counted).Select(d => d.DayId).ToHashSet()
            : summary.Days.Select(d => d.Id).ToHashSet();
        var byPoints = settings.Mode == SummaryMode.ByPoints;

        foreach (var day in data.Days)
        {
            if (!counted.Contains(day.Race.DayId))
                continue;
            var upTo = day.Race.DayNumber;
            var cut = Clone(settings);
            cut.Days = (settings.Days.Count > 0
                    ? settings.Days.Select(d => (d.DayId, d.DayNumber))
                    : summary.Days.OrderBy(d => d.Number).Select(d => (DayId: d.Id, DayNumber: d.Number)))
                .Select(d => new SummaryDaySetting
                {
                    DayId = d.DayId,
                    DayNumber = d.DayNumber,
                    Counted = counted.Contains(d.DayId) && numberOf.TryGetValue(d.DayId, out var n) && n <= upTo
                })
                .ToList();
            if (cut.PriorityDayId is { } priority && numberOf.TryGetValue(priority, out var pn) && pn > upTo)
                cut.PriorityDayId = null;

            foreach (var group in _summary.RankGroups(summary, cut))
            {
                var leader = group.Entries.Where(e => e.Place is not null).Select(e => (TimeSpan?)e.TotalTime)
                    .DefaultIfEmpty(null).Min();
                foreach (var entry in group.Entries)
                {
                    if (entry.Place is not { } place)
                        continue;
                    totals[TotalKey(day.Race.DayId, entry.Member.ParticipantId, group.GroupName)] = byPoints
                        ? new IofOverallResult(null, null, place, IofResultStatus.OK, [new IofScore(PointsType, entry.TotalPoints)])
                        : new IofOverallResult(
                            IofClock.Seconds(entry.TotalTime),
                            leader is { } l ? IofClock.Seconds(entry.TotalTime - l) : null,
                            place, IofResultStatus.OK, []);
                }
            }
        }
        return totals;
    }

    private static string TotalKey(Guid dayId, Guid participantId, string groupName) =>
        $"{dayId:N}|{participantId:N}|{TextKey.Of(groupName)}";

    private static SummaryProtocolSettings Clone(SummaryProtocolSettings settings) =>
        System.Text.Json.JsonSerializer.Deserialize<SummaryProtocolSettings>(
            System.Text.Json.JsonSerializer.Serialize(settings)) ?? new SummaryProtocolSettings();

    // ---------------------------------------------------------------------------------------------- StartList

    public IofStartList BuildStartList(IofExportData data, IofExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(options);

        var multi = data.Days.Count > 1;
        var classes = new ClassBag<StartClass>(name => new StartClass(name));

        foreach (var day in data.Days)
        {
            if (day.Starts is not { } starts)
                continue;
            var race = multi ? day.Race.DayNumber : (int?)null;
            foreach (var group in starts.Groups.OrderBy(g => g.Order))
            {
                if (!Included(options, group.Name))
                    continue;
                var info = ClassInfo(day, group.Name);
                var cls = classes.Get(group.Name);
                if (ClassCourse(info, group.Name, race) is { } course)
                    cls.Courses.Add(course);

                if (info.Competitors == CompetitorModel.TeamWithLegs && group.RelayTeams is { } relayTeams)
                {
                    foreach (var team in relayTeams.OrderBy(t => RelayTeamNumbers.SortKey(t.Number))
                                 .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
                    {
                        var members = team.Legs.Where(l => l.Runner is not null).OrderBy(l => l.Leg)
                            .Select(l =>
                            {
                                var row = l.Runner!;
                                var runner = day.Runners.GetValueOrDefault(row.ParticipantId);
                                return new IofTeamMemberStart(
                                    Person(row.FullName, row.BirthDate, runner, options),
                                    Organisation(row.ClubName, row.RegionName, options),
                                    new IofRaceStart
                                    {
                                        RaceNumber = race,
                                        Leg = l.Leg,
                                        Bib = Blank(row.Number),
                                        // Only leg 1 has a start of its own (drawn, else the mass start); the others
                                        // start at the previous leg's finish.
                                        Start = l.Leg == 1 && team.StartTime is { } st ? IofClock.OnDay(day.Race, st) : null,
                                        Course = RunnerCourse(runner, group.Name),
                                        Chip = ChipOf(row.Chip)
                                    });
                            })
                            .ToList();
                        if (members.Count == 0)
                            continue;
                        cls.Teams.Add(new IofTeamStart(
                            team.Name.Trim(),
                            Organisation(team.ClubName, team.RegionName, options),
                            team.Number?.ToString(CultureInfo.InvariantCulture),
                            members));
                    }
                }
                else if (info.Competitors == CompetitorModel.Team)
                {
                    var teams = group.Rows.Where(r => r.Team.Length > 0)
                        .GroupBy(r => r.Team, TextKey.Comparer)
                        .OrderBy(g => g.Min(r => r.StartTime) ?? TimeSpan.MaxValue)
                        .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);
                    foreach (var team in teams)
                    {
                        var rows = team.OrderBy(r => r.FullName, StringComparer.CurrentCultureIgnoreCase).ToList();
                        var members = rows.Select((row, i) =>
                        {
                            var runner = day.Runners.GetValueOrDefault(row.ParticipantId);
                            return new IofTeamMemberStart(
                                Person(row.FullName, row.BirthDate, runner, options),
                                Organisation(row.ClubName, row.RegionName, options),
                                StartOf(day, row, runner, race, group.Name) with { Leg = 1, LegOrder = i + 1 });
                        }).ToList();
                        cls.Teams.Add(new IofTeamStart(
                            rows[0].Team, Organisation(rows[0].ClubName, rows[0].RegionName, options), null, members));
                    }
                }

                // Persons: everyone of an individual group; the runners off a team / patrol otherwise.
                var persons = info.Competitors switch
                {
                    CompetitorModel.Team => group.Rows.Where(r => r.Team.Length == 0),
                    _ => group.Rows
                };
                foreach (var row in persons
                             .OrderBy(r => r.StartTime ?? TimeSpan.MaxValue)
                             .ThenBy(r => r.FullName, StringComparer.CurrentCultureIgnoreCase))
                {
                    var runner = day.Runners.GetValueOrDefault(row.ParticipantId);
                    cls.AddPerson(row.ParticipantId, Person(row.FullName, row.BirthDate, runner, options),
                        Organisation(row.ClubName, row.RegionName, options),
                        StartOf(day, row, runner, race, group.Name));
                }
            }
        }

        return new IofStartList(
            new IofDocumentHeader(options.Creator, IofClock.Truncate(options.CreateTime)),
            Event(data, options),
            classes.All
                .Where(c => c.Persons.Count > 0 || c.Teams.Count > 0)
                .Select(c => new IofClassStart(
                    c.Name, c.Courses,
                    c.Persons.Select(p => new IofPersonStart(p.Person, p.Organisation, p.Results)).ToList(),
                    c.Teams))
                .ToList());
    }

    private static IofRaceStart StartOf(IofDayExport day, StartProtocolRow row, IofRunnerData? runner, int? race, string groupName) =>
        new()
        {
            RaceNumber = race,
            Bib = Blank(row.Number),
            Start = row.StartTime is { } st ? IofClock.OnDay(day.Race, st) : null,
            Course = RunnerCourse(runner, groupName),
            Chip = ChipOf(row.Chip)
        };

    // ---------------------------------------------------------------------------------------------- shared

    // <Event>: the name, the date (first … last day of a multi-day file), the organiser and the races.
    private static IofEvent Event(IofExportData data, IofExportOptions options)
    {
        var days = data.Days;
        var first = days.Count > 0 ? days[0].Race.Date : DateOnly.FromDateTime(DateTime.Today);
        var multi = days.Count > 1;
        return new IofEvent(
            options.EventName.Trim(),
            first,
            multi ? days[^1].Race.Date : null,
            data.Event.Organiser.Trim(),
            multi
                ? days.Select(d => new IofRace(
                        d.Race.DayNumber,
                        $"{options.DayLabel} {d.Race.DayNumber.ToString(CultureInfo.InvariantCulture)}".Trim(),
                        d.Race.Date))
                    .ToList()
                : []);
    }

    // The class course: the group's name, length and control count, when anything is known — none for a relay whose
    // legs run named courses (each member carries their own).
    private static IofSimpleCourse? ClassCourse(IofClassInfo info, string groupName, int? race)
    {
        if (info.HasLegCourses || (info.DistanceKm is null && info.ControlCount is null))
            return null;
        return new IofSimpleCourse(
            race,
            groupName.Trim(),
            null,
            info.DistanceKm is { } km ? (int)Math.Round(km * 1000m, MidpointRounding.AwayFromZero) : null,
            info.ControlCount);
    }

    // A runner's own course (scatter variant, relay leg course): the name and family the course file gave, else the
    // «група_код» / «група» the course import reads back into the same group and code.
    private static IofSimpleCourse? RunnerCourse(IofRunnerData? runner, string groupName)
    {
        if (runner?.Course is not { } course || course.Code.Trim().Length == 0)
            return null;
        var group = groupName.Trim();
        return new IofSimpleCourse(
            null,
            Blank(course.SourceName) ?? $"{group}_{course.Code.Trim()}",
            Blank(course.SourceFamily) ?? group,
            null,
            course.ControlCount);
    }

    private static IofPerson Person(string fullName, DateTimeOffset? birthDate, IofRunnerData? runner, IofExportOptions options)
    {
        var (family, given) = IofNames.Split(fullName);
        return new IofPerson(
            family,
            given,
            options.IncludeBirthDates && birthDate is { } bd ? DateOnly.FromDateTime(bd.Date) : null,
            Blank(runner?.FsouCode));
    }

    // The runner's single organisation: club or region per the option, the other one when that is blank.
    private static IofOrganisation? Organisation(string club, string region, IofExportOptions options)
    {
        var c = (club ?? string.Empty).Trim();
        var r = (region ?? string.Empty).Trim();
        if (options.Organisation == IofOrganisationSource.Region)
            return r.Length > 0 ? new IofOrganisation(r, IofOrganisationType.NationalRegion)
                : c.Length > 0 ? new IofOrganisation(c, IofOrganisationType.Club)
                : null;
        return c.Length > 0 ? new IofOrganisation(c, IofOrganisationType.Club)
            : r.Length > 0 ? new IofOrganisation(r, IofOrganisationType.NationalRegion)
            : null;
    }

    private static IofClassInfo ClassInfo(IofDayExport day, string groupName) =>
        day.Classes.TryGetValue(groupName, out var info) ? info
        : day.Classes.FirstOrDefault(kv => TextKey.Same(kv.Key, groupName)).Value ?? UnknownClass;

    private static bool Included(IofExportOptions options, string groupName) =>
        options.Groups is null || options.Groups.Any(g => TextKey.Same(g, groupName));

    private static IEnumerable<ResultProtocolRow> OrderedByPlace(IEnumerable<ResultProtocolRow> rows) =>
        rows.OrderBy(r => r.Result.Place is { } p ? p : int.MaxValue)
            .ThenBy(r => r.FullName, StringComparer.CurrentCultureIgnoreCase);

    // The best ranking time among the placed runners — the «Відставання» reference of the protocol.
    private static TimeSpan? LeaderTime(IReadOnlyList<ResultProtocolRow> rows) =>
        rows.Where(r => r.Result.Place is not null && r.Result.RankingTime is not null)
            .Select(r => r.Result.RankingTime)
            .DefaultIfEmpty(null)
            .Min();

    private static string? Chip(IofRunnerData? runner) => ChipOf(runner?.Chip);

    private static string? ChipOf(string? chip) =>
        Blank(chip) is { } c && c != "0" ? c : null;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // ---------------------------------------------------------------------------------------------- class bags

    // Classes by name in first-seen order (TextKey), so a group met again on a later day extends the same class.
    private sealed class ClassBag<T>(Func<string, T> create) where T : class
    {
        private readonly Dictionary<string, T> _byKey = new(StringComparer.Ordinal);
        private readonly List<T> _all = [];

        public IReadOnlyList<T> All => _all;

        public T Get(string name)
        {
            var key = TextKey.Of(name);
            if (!_byKey.TryGetValue(key, out var item))
            {
                item = create(name.Trim());
                _byKey[key] = item;
                _all.Add(item);
            }
            return item;
        }
    }

    // A person of a class with their results / starts, one per race, in first-seen order.
    private sealed class PersonEntry<TRace>(IofPerson person, IofOrganisation? organisation)
    {
        public IofPerson Person { get; } = person;
        public IofOrganisation? Organisation { get; } = organisation;
        public List<TRace> Results { get; } = [];
    }

    private abstract class ClassEntry<TRace, TTeam>(string name)
    {
        private readonly Dictionary<Guid, PersonEntry<TRace>> _byParticipant = [];

        public string Name { get; } = name;
        public List<IofSimpleCourse> Courses { get; } = [];
        public List<PersonEntry<TRace>> Persons { get; } = [];
        public List<TTeam> Teams { get; } = [];

        public void AddPerson(Guid participantId, IofPerson person, IofOrganisation? organisation, TRace race)
        {
            if (participantId == Guid.Empty || !_byParticipant.TryGetValue(participantId, out var entry))
            {
                entry = new PersonEntry<TRace>(person, organisation);
                if (participantId != Guid.Empty)
                    _byParticipant[participantId] = entry;
                Persons.Add(entry);
            }
            entry.Results.Add(race);
        }
    }

    private sealed class ResultClass(string name) : ClassEntry<IofRaceResult, IofTeamResult>(name);

    private sealed class StartClass(string name) : ClassEntry<IofRaceStart, IofTeamStart>(name);
}
