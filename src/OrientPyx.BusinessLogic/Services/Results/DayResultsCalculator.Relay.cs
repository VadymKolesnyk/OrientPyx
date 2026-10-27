using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services.Results;

/// <summary>
/// The relay (<see cref="CompetitorModel.TeamWithLegs"/>) stages of the day computation: the legs of each team are
/// evaluated one after another (Р-27…Р-30), adjusted (Р-6, Р-49), summed into the team result (Р-31…Р-34), ranked by
/// the discipline (Р-35) and given the team's points (Р-37).
/// </summary>
internal sealed partial class DayResultsCalculator
{
    /// <summary>Everything the relay stages need from the shared part of <see cref="Compute"/>.</summary>
    private sealed record RelayInputs(
        EventDay Day,
        EventDaySnapshot Snapshot,
        IReadOnlyDictionary<Guid, GroupDaySettings> SettingsByGroup,
        IReadOnlyDictionary<Guid, IReadOnlyList<ScatterVariantData>> VariantsByGroup,
        IReadOnlySet<string> StartFinishCodes,
        IReadOnlySet<string> DisabledCodes,
        IReadOnlyDictionary<string, FinishReadout> LatestByChip,
        Func<IDisciplineStrategy, SettingValues> SettingsOf,
        ParticipantViews Views,
        DateOnly DayDate);

    /// <summary>The relay team a link runs for, when that team's group runs a relay discipline on the day.</summary>
    private RelayTeam? RelayTeamOf(
        ParticipantDay link, IReadOnlyDictionary<Guid, RelayTeam> teamsById,
        IReadOnlyDictionary<Guid, GroupDaySettings> settingsByGroup, DisciplineId dayDefault) =>
        link.RelayTeamId is { } tid && teamsById.TryGetValue(tid, out var team)
        && _strategies.For(settingsByGroup.TryGetValue(team.GroupId, out var gs) ? gs.DisciplineOverride ?? dayDefault : dayDefault)
            .Competitors == CompetitorModel.TeamWithLegs
            ? team
            : null;

    /// <summary>
    /// Stages (b)–(c) for every relay link: legs in order per team, runners of a relay group without a (valid) team
    /// on their own — shown, never placed (Р-22). Fills <paramref name="results"/> and returns the team results.
    /// </summary>
    private Dictionary<Guid, TeamDayResult> EvaluateRelay(
        RelayInputs input, IReadOnlyList<ParticipantDay> relayLinks, Dictionary<Guid, ParticipantDayResult> results)
    {
        var teams = new Dictionary<Guid, TeamDayResult>();
        var teamsById = input.Snapshot.RelayTeams.ToDictionary(t => t.Id);
        var dayDefault = input.Day.DefaultDiscipline;

        var byTeam = relayLinks
            .Where(l => l.RelayTeamId is { } tid && teamsById.ContainsKey(tid))
            .GroupBy(l => l.RelayTeamId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Every team of a relay group on the day, even one nobody is assigned to yet.
        foreach (var team in input.Snapshot.RelayTeams)
        {
            var gs = input.SettingsByGroup.TryGetValue(team.GroupId, out var s) ? s : null;
            var strategy = _strategies.For(gs?.DisciplineOverride ?? dayDefault);
            if (strategy.Competitors != CompetitorModel.TeamWithLegs)
                continue;
            var members = byTeam.TryGetValue(team.Id, out var m) ? m : [];
            teams[team.Id] = EvaluateTeam(input, team, gs, strategy, members, results);
        }

        // Runners of a relay group with no team (or on a leg outside 1…LegCount, or sharing a leg): evaluated as a
        // lone leg 1, never placed.
        foreach (var link in relayLinks)
        {
            if (results.ContainsKey(link.Id))
                continue;
            var groupId = link.RelayTeamId is { } tid && teamsById.TryGetValue(tid, out var team) ? team.GroupId : link.GroupId;
            var gs = groupId is { } gid && input.SettingsByGroup.TryGetValue(gid, out var s) ? s : null;
            var strategy = _strategies.For(gs?.DisciplineOverride ?? dayDefault);
            var (result, _) = EvaluateLeg(input, link, gs, groupId, strategy, legNumber: 1, start: null, useLegStart: false);
            results[link.Id] = result with { UnrankedReason = RelayNoTeamReason };
        }
        return teams;
    }

    /// <summary>The <see cref="ParticipantDayResult.UnrankedReason"/> of a relay runner without a team leg (Р-22).</summary>
    public const string RelayNoTeamReason = "relay.noTeam";

    private TeamDayResult EvaluateTeam(
        RelayInputs input, RelayTeam team, GroupDaySettings? gs, IDisciplineStrategy strategy,
        List<ParticipantDay> members, Dictionary<Guid, ParticipantDayResult> results)
    {
        var legCount = Math.Max(1, gs?.LegCount ?? 1);   // the same default the groups page shows
        var restarts = RelayRestartTimes.Parse(gs?.RestartTimesJson);

        // One runner per leg (Р-21); a second runner on the same leg (or a leg out of range) is left to the
        // no-team pass.
        var byLeg = new Dictionary<int, ParticipantDay>();
        foreach (var link in members.OrderBy(l => l.Order))
            if (link.Leg is { } leg && leg >= 1 && leg <= legCount)
                byLeg.TryAdd(leg, link);

        var legs = new List<RelayLegResult>(legCount);
        DateTimeOffset? previousFinish = null;
        DateTimeOffset? anyDate = byLeg.Values
            .Select(l => input.LatestByChip.TryGetValue(l.Chip.Trim(), out var r) ? r.FinishTime : null)
            .FirstOrDefault(f => f is not null);
        TimeSpan? cumulative = TimeSpan.Zero;
        string? unranked = null;
        var anyOutOfCompetitionMember = false;

        for (var leg = 1; leg <= legCount; leg++)
        {
            if (!byLeg.TryGetValue(leg, out var link))
            {
                legs.Add(new RelayLegResult(leg, null, null, null, null, FinishStatus.None,
                    null, null, null, Restarted: false, Pending: false));
                previousFinish = null;
                cumulative = null;
                continue;
            }
            anyOutOfCompetitionMember |= link.OutOfCompetition;
            var readout = input.LatestByChip.TryGetValue(link.Chip.Trim(), out var r) ? r : null;

            // Р-27 (leg 1) / Р-28…Р-30 (leg N): where this leg's time starts.
            DateTimeOffset? start;
            bool restarted = false, pending = false;
            if (leg == 1)
                start = strategy.ResolveStart(new StartContext
                {
                    ChipStart = readout?.StartTime,
                    AssignedStart = link.StartTime ?? gs?.MassStartTime,
                    FinishTime = readout?.FinishTime ?? anyDate
                });
            else
            {
                var restartAt = restarts.TryGetValue(leg, out var rt)
                    ? DisciplineStrategyBase.CombineWithFinishDate(rt, previousFinish ?? readout?.FinishTime ?? anyDate)
                    : null;
                if (previousFinish is { } pf)
                {
                    restarted = restartAt is { } ra && ra < pf;
                    start = restarted ? restartAt : pf;
                }
                else if (restartAt is not null)
                {
                    start = restartAt;
                    restarted = true;
                }
                else
                {
                    start = null;
                    pending = true;   // Р-30: no handoff and no restart — the leg waits
                }
            }

            var (result, row) = EvaluateLeg(input, link, gs, team.GroupId, strategy, leg, start, useLegStart: true,
                pending: pending);
            if (row?.UnrankedReason is { } reason)
                unranked ??= reason;

            cumulative = cumulative is { } c && result.AdjustedTime is { } at ? c + at : null;
            var legResult = new RelayLegResult(leg, link.Id, link.ParticipantId, start, readout?.FinishTime,
                result.Status, result.ResultTime, result.AdjustedTime, cumulative, restarted, pending);
            legs.Add(legResult);
            results[link.Id] = result with { Leg = legResult };
            previousFinish = readout?.FinishTime;
        }

        // Р-33: OK only when every leg is OK; else the worst leg status. Р-31: the limit applies to the team's
        // actual time (finish of the last leg − start of leg 1).
        var assigned = legs.Where(l => l.LinkId is not null).ToList();
        var actual = legs[0].Start is { } firstStart && legs[^1].Finish is { } lastFinish && legs[^1].LinkId is not null
            ? lastFinish - firstStart
            : (TimeSpan?)null;
        FinishStatus computed;
        if (assigned.Count > 0 && assigned.All(l => l.Status == FinishStatus.Ok))
            computed = gs?.TimeLimitSeconds is { } limit && actual is { } a && a > TimeSpan.FromSeconds(limit)
                ? FinishStatus.Ovt
                : FinishStatus.Ok;
        else
            computed = assigned.Count == 0
                ? FinishStatus.None
                : assigned.Select(l => l.Status).MaxBy(Severity);
        var status = team.StatusOverride ?? computed;

        // Р-32: the team time is the sum of the (adjusted) leg times, once every assigned leg has one.
        TimeSpan? time = assigned.Count > 0 && assigned.All(l => l.AdjustedTime is not null)
            ? assigned.Aggregate(TimeSpan.Zero, (sum, l) => sum + l.AdjustedTime!.Value)
            : null;
        var incomplete = assigned.Count < legCount;

        return new TeamDayResult(team.Id, team.Number, team.Name, team.GroupId, status, computed, team.StatusOverride,
            time, actual, legs)
        {
            Incomplete = incomplete,
            // Р-26: an incomplete team runs «поза конкурсом»; so does a team flagged as such or with a member who is.
            OutOfCompetition = team.OutOfCompetition || incomplete || anyOutOfCompetitionMember,
            Restarted = legs.Any(l => l.Restarted),
            UnrankedReason = unranked
        };
    }

    // Р-33: DSQ > MP > OVT > DNF > DNS > «очікує» (None) > OK.
    private static int Severity(FinishStatus status) => status switch
    {
        FinishStatus.Dsq => 6,
        FinishStatus.Mp => 5,
        FinishStatus.Ovt => 4,
        FinishStatus.Dnf => 3,
        FinishStatus.Dns => 2,
        FinishStatus.None => 1,
        _ => 0
    };

    /// <summary>
    /// One leg: judged against the leg course from the sequence's start (Р-31), adjusted when OK (Р-6). A pending
    /// leg (Р-30) gets no time and no status even when read; the judge's override still wins (Р-10).
    /// </summary>
    private (ParticipantDayResult Result, ResultRow? Row) EvaluateLeg(
        RelayInputs input, ParticipantDay link, GroupDaySettings? gs, Guid? groupId, IDisciplineStrategy strategy,
        int legNumber, DateTimeOffset? start, bool useLegStart, bool pending = false)
    {
        var readout = input.LatestByChip.TryGetValue(link.Chip.Trim(), out var r) ? r : null;
        if (readout is null)
        {
            var noReadOverride = link.ResultStatusOverride == FinishStatus.Ok ? null : link.ResultStatusOverride;
            return (new ParticipantDayResult(null, null, noReadOverride ?? FinishStatus.None, noReadOverride,
                FinishStatus.None, null, null, null, HasReadout: false), null);
        }

        var wasOnCourse = readout.StartTime is not null || readout.FinishTime is not null
            || CourseCodes.SplitCodes(readout.Punches).Any(c => !input.StartFinishCodes.Contains(c));

        FinishStatusResult finish;
        DateTimeOffset? resolvedStart;
        if (pending)
        {
            finish = FinishStatusResult.Of(FinishStatus.None);
            resolvedStart = null;
        }
        else
            (finish, resolvedStart) = FinishEvaluator.Evaluate(
                _strategies, readout, link, input.SettingsByGroup, input.StartFinishCodes, input.DisabledCodes,
                input.Day.DefaultDiscipline, input.VariantsByGroup, input.SettingsOf,
                useLegStart ? new FinishEvaluator.RelayLegStart(start) : null, groupId);

        var computed = finish.Status;
        var status = link.ResultStatusOverride ?? computed;
        var legTime = status == FinishStatus.Ok && resolvedStart is { } rs && readout.FinishTime is { } f && f > rs
            ? f - rs
            : (TimeSpan?)null;

        var result = new ParticipantDayResult(
            readout.StartTime, readout.FinishTime, status, link.ResultStatusOverride, computed, legTime,
            Place: null, Score: null, HasReadout: true)
        {
            WasOnCourse = wasOnCourse,
            AdjustedTime = legTime,
            Missed = finish.Missed
        };
        if (legTime is not { } time)
            return (result, null);

        // (c) Adjust the leg time (Р-6, Р-49: a changed leg is rounded on its own, the team sums the rounded legs).
        var row = new ResultRow
        {
            Id = link.Id,
            Participant = input.Views.For(link.ParticipantId),
            Status = status,
            Time = time,
            AdjustedTime = time,
            Punches = CourseCodes.SplitCodes(readout.Punches).Where(c => !input.StartFinishCodes.Contains(c)).ToList(),
            Missed = result.Missed
        };
        var adjusted = strategy.AdjustTime(new AdjustContext
        {
            Participant = row.Participant,
            Time = time,
            Date = input.DayDate,
            Leg = legNumber,
            Row = row,
            Settings = input.SettingsOf(strategy)
        });
        var changed = adjusted != time;
        return (result with
        {
            AdjustedTime = changed ? RoundToSecond(adjusted) : time,
            Extra = row.Values.Count > 0 ? new Dictionary<string, object?>(row.Values) : result.Extra,
            UnrankedReason = row.UnrankedReason
        }, row);
    }

    /// <summary>(d) Ranks every relay group by its discipline, then copies each team's place/«П/К» to its runners.</summary>
    private void RankRelay(
        RelayInputs input, Dictionary<Guid, TeamDayResult> teams, Dictionary<Guid, ParticipantDayResult> results,
        IReadOnlyDictionary<Guid, ParticipantView> participantsByLink)
    {
        var groupNames = input.Snapshot.Groups.ToDictionary(g => g.Id, g => g.Name);
        foreach (var byGroup in teams.Values.GroupBy(t => t.GroupId).ToList())
        {
            var gs = input.SettingsByGroup.TryGetValue(byGroup.Key, out var s) ? s : null;
            var strategy = _strategies.For(gs?.DisciplineOverride ?? input.Day.DefaultDiscipline);
            strategy.Rank(new GroupRankingContext
            {
                GroupId = byGroup.Key,
                GroupName = groupNames.TryGetValue(byGroup.Key, out var gn) ? gn : string.Empty,
                DisciplineSettings = input.SettingsOf(strategy),
                Participants = participantsByLink,
                Settings = gs,
                RelayTeams = byGroup.ToList(),
                TeamResults = teams,
                Results = results
            });
        }
        CopyTeamsToMembers(teams, results);
    }

    /// <summary>Each runner shows the team's place, «П/К», points and the team itself (Р-37).</summary>
    private static void CopyTeamsToMembers(Dictionary<Guid, TeamDayResult> teams, Dictionary<Guid, ParticipantDayResult> results)
    {
        foreach (var team in teams.Values)
            foreach (var leg in team.Legs)
                if (leg.LinkId is { } linkId && results.TryGetValue(linkId, out var result))
                    results[linkId] = result with
                    {
                        Place = team.Place,
                        OutOfCompetition = team.OutOfCompetition,
                        Points = team.Points,
                        Team = team,
                        Leg = leg
                    };
    }

    /// <summary>
    /// (e) Р-37: the group's points rule scores the team's place (a formula — the team time against the winner's);
    /// every runner of the team gets the same points. «П/К» and non-OK teams earn none.
    /// </summary>
    private void AwardRelayPoints(
        RelayInputs input, DayResultsAppData app, Dictionary<Guid, TeamDayResult> teams,
        Dictionary<Guid, ParticipantDayResult> results)
    {
        if (app.PointsRules.Count == 0 || teams.Count == 0)
            return;
        var ruleById = app.PointsRules.ToDictionary(r => r.Id);
        var defaultRuleId = input.Snapshot.Info?.DefaultPointsRuleId;

        foreach (var byGroup in teams.Values.GroupBy(t => t.GroupId).ToList())
        {
            var gs = input.SettingsByGroup.TryGetValue(byGroup.Key, out var s) ? s : null;
            var ruleId = gs?.PointsRuleId ?? defaultRuleId;
            if (ruleId is not { } id || id == Guid.Empty || !ruleById.TryGetValue(id, out var rule))
                continue;

            var group = byGroup.ToList();
            var inCompetition = group.Where(t => !t.OutOfCompetition).ToList();
            var leader = group.FirstOrDefault(t => t.Place == 1);
            foreach (var team in group)
            {
                if (team.Status != FinishStatus.Ok || team.OutOfCompetition || team.UnrankedReason is not null)
                    continue;
                var points = PointsRuleEvaluator.Evaluate(rule, new PointsRuleInput(
                    Place: team.Place,
                    ResultTimeSeconds: team.Time?.TotalSeconds,
                    Score: null,
                    LeaderTimeSeconds: leader?.Time?.TotalSeconds,
                    LeaderScore: null,
                    GroupSize: inCompetition.Count,
                    StartedCount: inCompetition.Count(t => t.Legs.Any(l =>
                        l.LinkId is { } lid && results.TryGetValue(lid, out var r) && r.WasOnCourse)),
                    FinishedCount: inCompetition.Count(t => t.Status == FinishStatus.Ok)));
                if (points is not null)
                    teams[team.TeamId] = teams[team.TeamId] with { Points = points };
            }
        }
        CopyTeamsToMembers(teams, results);
    }
}
