using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Disciplines.Plugins;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services.Results;

/// <summary>
/// Computes every participant's result on one day from an <see cref="EventDaySnapshot"/> (links, read-outs,
/// group settings, control points, scatter variants — one consistent read) plus the app-level
/// <see cref="DayResultsAppData"/>. No database access. The pipeline runs in explicit stages (Р-4):
/// <list type="number">
///   <item>competitors — each link is an individual or a team member, by the group's <see cref="CompetitorModel"/>;</item>
///   <item>evaluate — start (<see cref="IDisciplineStrategy.ResolveStart"/>), finish status, actual time, score;</item>
///   <item>adjust — <see cref="IDisciplineStrategy.AdjustTime"/> on every OK result (Р-6);</item>
///   <item>rank — <see cref="IDisciplineStrategy.Rank"/> per group (Р-8);</item>
///   <item>points («Очки», Р-9) and sports ranks («розряд», Р-7).</item>
/// </list>
/// Results are keyed by participant-day link id. A chip read more than once on the day resolves to its
/// <b>latest</b> read-out (highest <c>Order</c>).
/// </summary>
internal sealed partial class DayResultsCalculator
{
    private readonly IDisciplineStrategyProvider _strategies;

    public DayResultsCalculator(IDisciplineStrategyProvider strategies) => _strategies = strategies;

    /// <summary>One link after the evaluate stage — what the later stages need about it.</summary>
    private sealed record Evaluated(
        ParticipantDay Link, IDisciplineStrategy Strategy, GroupDaySettings? Settings,
        FinishStatusResult Finish, bool HasReadout, TimeSpan? Elapsed, HashSet<string>? PunchedAllowed,
        FinishReadout? Readout = null);

    /// <param name="rankCalcs">When non-null, receives the per-group rank-award derivation built by the ranks
    /// pass — used only by the results protocol to print its explanatory line.</param>
    /// <param name="teamResults">When non-null, receives the day's relay team results keyed by team id (the same
    /// instances the runners' <see cref="ParticipantDayResult.Team"/> point to).</param>
    public Dictionary<Guid, ParticipantDayResult> Compute(
        EventDay day, EventDaySnapshot snapshot, DayResultsAppData app,
        Dictionary<Guid, GroupRankCalculation>? rankCalcs = null,
        Dictionary<Guid, TeamDayResult>? teamResults = null)
    {
        var links = snapshot.Links;
        if (links.Count == 0)
            return [];

        var settingsByGroup = snapshot.GroupDaySettings.ToDictionary(s => s.GroupId);
        var controlPoints = snapshot.ControlPoints;
        var startFinishCodes = CourseCodes.StartFinishCodesOf(controlPoints);
        var disabledCodes = CourseCodes.DisabledCodesOf(controlPoints);
        var dayDefault = day.DefaultDiscipline;
        var variantsByGroup = FinishEvaluator.BuildCourseVariants(
            _strategies, snapshot.CourseVariants, settingsByGroup, dayDefault, startFinishCodes, disabledCodes);

        // Per-control point values — needed only to tally «Бали».
        var pointsByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cp in controlPoints)
            if (cp.Points is { } pts)
                pointsByCode[cp.Code.Trim()] = pts;

        // Each discipline's effective settings on the day (Р-11), resolved once per discipline.
        var settingsByDiscipline = new Dictionary<DisciplineId, SettingValues>();
        SettingValues SettingsOf(IDisciplineStrategy strategy)
        {
            if (!settingsByDiscipline.TryGetValue(strategy.Id, out var values))
                settingsByDiscipline[strategy.Id] = values =
                    DisciplineSettings.Resolve(strategy, snapshot.DisciplineSettings, day.Id);
            return values;
        }
        // Team name per participant — the team lives on the participant (competition-level), not on the
        // per-day link, so resolve it once for the team ranking below.
        var participants = snapshot.Participants;
        var participantById = participants.ToDictionary(p => p.Id);
        var teamByParticipant = participants.ToDictionary(p => p.Id, p => p.Team.Trim());

        // Latest read-out per chip (the "last read-out" rule): highest Order wins on a repeated chip.
        var latestByChip = new Dictionary<string, FinishReadout>(TextKey.Comparer);
        foreach (var r in snapshot.FinishReadouts)
        {
            var key = r.ChipNumber.Trim();
            if (key.Length == 0)
                continue;
            if (!latestByChip.TryGetValue(key, out var cur) || r.Order > cur.Order)
                latestByChip[key] = r;
        }

        var results = new Dictionary<Guid, ParticipantDayResult>(links.Count);
        var relayTeamsById = snapshot.RelayTeams.ToDictionary(t => t.Id);
        var relayLinks = new List<ParticipantDay>();

        // (a) + (b): competitors and evaluation.
        var evaluated = new List<Evaluated>(links.Count);
        foreach (var link in links)
        {
            GroupDaySettings? gs = link.GroupId is { } gid && settingsByGroup.TryGetValue(gid, out var s) ? s : null;
            var strategy = _strategies.For(gs?.DisciplineOverride ?? dayDefault);
            // A relay runner (their team's group runs a relay, Р-22) or a runner of a relay group: evaluated by the
            // relay pass below, legs in order.
            if (RelayTeamOf(link, relayTeamsById, settingsByGroup, dayDefault) is not null
                || strategy.Competitors == CompetitorModel.TeamWithLegs)
            {
                relayLinks.Add(link);
                continue;
            }
            var isTeam = strategy.Competitors == CompetitorModel.Team;

            var chip = link.Chip.Trim();
            if (chip.Length == 0 || !latestByChip.TryGetValue(chip, out var readout))
            {
                // No read-out for this member's chip — but a manual override still shows a status. An "OK"
                // override is meaningless without a result (OK is the all-clear for someone who ran), so it
                // resolves to blank — matching how SetParticipantDayResultStatusAsync normalizes it.
                var noReadOverride = link.ResultStatusOverride == FinishStatus.Ok ? null : link.ResultStatusOverride;
                // No read-out ⇒ nothing to compute, so "auto" resolves to blank (FinishStatus.None).
                results[link.Id] = new ParticipantDayResult(
                    null, null, noReadOverride ?? FinishStatus.None, noReadOverride, FinishStatus.None,
                    null, null, null, HasReadout: false);
                evaluated.Add(new Evaluated(link, strategy, gs, FinishStatusResult.Of(FinishStatus.None),
                    HasReadout: false, Elapsed: null, PunchedAllowed: null));
                continue;
            }

            var (finish, resolvedStart) = FinishEvaluator.Evaluate(
                _strategies, readout, link, settingsByGroup, startFinishCodes, disabledCodes, dayDefault, variantsByGroup,
                SettingsOf);
            var computed = finish.Status;
            var status = link.ResultStatusOverride ?? computed;

            // Did this runner actually go out on course? A start box, a finish box, or at least one control
            // punch all say yes; only a wholly empty read-out says no. The start/finish marker codes are
            // dropped from the punch list so a chip carrying nothing but those isn't mistaken for a КП.
            var wasOnCourse = readout.StartTime is not null
                || readout.FinishTime is not null
                || CourseCodes.SplitCodes(readout.Punches).Any(c => !startFinishCodes.Contains(c));

            var resultTime = status == FinishStatus.Ok && resolvedStart is { } rs && readout.FinishTime is { } f
                ? f - rs
                : (TimeSpan?)null;

            HashSet<string>? punchedAllowed = null;
            int? score = null;
            if (strategy.UsesControlPointPoints)
            {
                var detail = ScoreDetail(strategy, readout, gs, startFinishCodes, disabledCodes, pointsByCode, resolvedStart);
                // Fold in the judge's points correction («бонус»): the personal score is the computed net
                // plus this member's own bonus. For a teamed runner the team pass replaces this Score with
                // the team net (which folds in the team-min bonus instead).
                score = detail.Net + (link.Bonus ?? 0);
                punchedAllowed = detail.Punched;
            }

            results[link.Id] = new ParticipantDayResult(
                readout.StartTime, readout.FinishTime, status, link.ResultStatusOverride, computed, resultTime, Place: null, score, HasReadout: true)
            {
                // Personal breakdown (the controls this chip scored). For a teamed runner the team pass
                // replaces this with the team's common controls so the tooltip matches the team Бали.
                ScoreBreakdown = punchedAllowed is { } pa ? CourseCodes.ScoreLines(pa, pointsByCode) : [],
                Bonus = link.Bonus,
                WasOnCourse = wasOnCourse,
                // Personal-discipline поза конкурсом ⇒ marked «П/К», never placed (and left out of the ranking
                // so it doesn't shift the others). Team поза конкурсом is the team pass's job.
                OutOfCompetition = !isTeam && link.OutOfCompetition,
                AdjustedTime = resultTime,
                Missed = finish.Missed
            };

            // The elapsed time (finish − start) feeds a team's over-time penalty (max member time) and
            // tie-break (last finisher).
            var elapsed = resolvedStart is { } rs2 && readout.FinishTime is { } f2 ? f2 - rs2 : (TimeSpan?)null;
            evaluated.Add(new Evaluated(link, strategy, gs, finish, HasReadout: true, elapsed, punchedAllowed, readout));
        }

        // (c) Adjust: the discipline may correct an OK result's time (Р-6). Built-ins leave it unchanged, in
        // which case AdjustedTime stays exactly ResultTime (no rounding).
        var dayDate = DayDate(day, snapshot.FinishReadouts);

        // Relay (b)+(c): every team's legs in order, adjusted per leg.
        var views = new ParticipantViews(snapshot);
        var relayInput = new RelayInputs(day, snapshot, settingsByGroup, variantsByGroup, startFinishCodes, disabledCodes,
            latestByChip, SettingsOf, views, dayDate);
        var teams = relayLinks.Count > 0 || snapshot.RelayTeams.Count > 0
            ? EvaluateRelay(relayInput, relayLinks, results)
            : [];
        foreach (var e in evaluated)
        {
            var result = results[e.Link.Id];
            if (result.Status != FinishStatus.Ok || result.ResultTime is not { } time)
                continue;

            var row = new ResultRow
            {
                Id = e.Link.Id,
                Participant = views.For(e.Link.ParticipantId),
                Status = result.Status,
                Time = time,
                AdjustedTime = time,
                Score = result.Score,
                OutOfCompetition = result.OutOfCompetition,
                Punches = CourseCodes.SplitCodes(e.Readout?.Punches).Where(c => !startFinishCodes.Contains(c)).ToList(),
                // Р-10: a judge's status override is final — the discipline sees no missed controls to penalise.
                Missed = result.Override is null ? result.Missed : []
            };
            var adjusted = e.Strategy.AdjustTime(new AdjustContext
            {
                Participant = row.Participant,
                Time = time,
                Date = dayDate,
                Row = row,
                Settings = SettingsOf(e.Strategy)
            });

            var changed = adjusted != time;
            if (!changed && row.Values.Count == 0 && row.UnrankedReason is null)
                continue;
            results[e.Link.Id] = result with
            {
                AdjustedTime = changed ? RoundToSecond(adjusted) : time,
                Extra = row.Values.Count > 0 ? new Dictionary<string, object?>(row.Values) : result.Extra,
                UnrankedReason = QualifiedReason(e.Strategy, row.UnrankedReason)
            };
        }

        // (d) Rank: per group, by the group's discipline. An individual discipline gets its OK, in-competition
        // results; a team discipline gets every member (the team pass decides who is placed).
        var byGroup = new Dictionary<Guid, (Guid? GroupId, IDisciplineStrategy Strategy, GroupDaySettings? Settings,
            List<RankEntry> Entries, List<TeamMemberEntry> Members)>();
        var participantsByLink = new Dictionary<Guid, ParticipantView>();
        var groupNames = snapshot.Groups.ToDictionary(g => g.Id, g => g.Name);
        foreach (var e in evaluated)
        {
            var key = e.Link.GroupId ?? Guid.Empty;
            if (!byGroup.TryGetValue(key, out var bucket))
                byGroup[key] = bucket = (e.Link.GroupId, e.Strategy, e.Settings, [], []);
            participantsByLink[e.Link.Id] = views.For(e.Link.ParticipantId);

            var link = e.Link;
            var result = results[link.Id];
            if (e.Strategy.Competitors == CompetitorModel.Team)
            {
                var teamKey = CourseCodes.TeamKeyFor(link, teamByParticipant);
                // A member with no read-out makes their team incomplete — the team earns no place until every
                // IN-COMPETITION member has finished. A поза конкурсом member who never read out does NOT drop
                // the team (the team pass excludes them from the OK check).
                bucket.Members.Add(e.HasReadout
                    ? new TeamMemberEntry(
                        link.Id, link.GroupId, teamKey, link.OutOfCompetition,
                        result.Status == FinishStatus.Ok, e.PunchedAllowed ?? [], e.Elapsed, e.Settings, link.Bonus)
                    : TeamMemberEntry.Incomplete(link.Id, link.GroupId, teamKey, link.OutOfCompetition, link.Bonus));
            }
            // Only OK results are placed; a non-positive result time can't be ranked by time. A поза
            // конкурсом runner is NOT placed and does NOT occupy a position — they are left out of the
            // ranking entirely so the in-competition runners are placed as if they were absent.
            else if (e.HasReadout && result.Status == FinishStatus.Ok && !link.OutOfCompetition
                     && result.UnrankedReason is null)
                bucket.Entries.Add(new RankEntry(link.Id, result.Score is not null, result.Score ?? 0,
                    result.AdjustedTime is { } t && t > TimeSpan.Zero ? t : TimeSpan.MaxValue));
        }

        foreach (var (_, bucket) in byGroup)
            bucket.Strategy.Rank(new GroupRankingContext
            {
                GroupId = bucket.GroupId,
                GroupName = bucket.GroupId is { } g && groupNames.TryGetValue(g, out var gn) ? gn : string.Empty,
                DisciplineSettings = SettingsOf(bucket.Strategy),
                Participants = participantsByLink,
                Settings = bucket.Settings,
                Entries = bucket.Entries,
                TeamMembers = bucket.Members,
                PointsByCode = pointsByCode,
                Results = results
            });
        if (teams.Count > 0)
        {
            foreach (var link in relayLinks)
                participantsByLink[link.Id] = views.For(link.ParticipantId);
            RankRelay(relayInput, teams, results, participantsByLink);
        }

        // (e) Points pass: award «Очки» from each group's effective points rule (its override, else the
        // competition default), keyed off the place/time/score just computed.
        AwardPoints(links, settingsByGroup, dayDefault, snapshot.Info, app, results);
        AwardRelayPoints(relayInput, app, teams, results);

        // Ranks pass: award «виконаний розряд» (Додаток 89) per group from its rank level, the course rank
        // (sum of the members' current-rank points) and the result-vs-leader percentage.
        AwardRanks(links, settingsByGroup, participants, dayDefault, app, results, rankCalcs);

        if (teamResults is not null)
            foreach (var (id, team) in teams)
                teamResults[id] = team;
        return results;
    }

    // Р-6: an adjusted time is rounded to a whole second, half up.
    // A plugin's unranked reason is one of its own string keys; qualify it with the plugin id so the protocol can
    // show it through the plugin's strings («K не визначено», Р-48).
    private static string? QualifiedReason(IDisciplineStrategy strategy, string? reason) =>
        reason is null || strategy.PluginId is not { } pluginId ? reason : PluginStrings.Key(pluginId, reason);

    private static TimeSpan RoundToSecond(TimeSpan time) =>
        TimeSpan.FromSeconds(Math.Round(time.TotalSeconds, MidpointRounding.AwayFromZero));

    // Р-46: the day's date, else the date of the earliest finish read-out, else today.
    private static DateOnly DayDate(EventDay day, IReadOnlyList<FinishReadout> readouts)
    {
        if (day.Date is { } date)
            return DateOnly.FromDateTime(date.Date);
        var earliest = readouts.Where(r => r.FinishTime is not null).Select(r => r.FinishTime!.Value).DefaultIfEmpty().Min();
        return earliest != default ? DateOnly.FromDateTime(earliest.Date) : DateOnly.FromDateTime(DateTime.Today);
    }

    /// <summary>
    /// The personal «Бали» for a read-out on a point-scoring day (the scored splits tally over the group's
    /// allowed controls) plus the set of allowed controls the chip actually punched (first punch each), used by
    /// the team tally (a control scores for the team only when every member's set has it).
    /// </summary>
    internal static (int Net, HashSet<string> Punched) ScoreDetail(
        IDisciplineStrategy strategy,
        FinishReadout readout,
        GroupDaySettings? gs,
        IReadOnlySet<string> startFinishCodes,
        IReadOnlySet<string> disabledCodes,
        IReadOnlyDictionary<string, int> pointsByCode,
        DateTimeOffset? resolvedStart)
    {
        var expected = CourseCodes.CourseControls(gs?.CourseOrder, startFinishCodes, disabledCodes);
        var punches = CourseCodes.DecodePunchTimes(readout.PunchTimes)
            .Where(p => !startFinishCodes.Contains(p.ControlCode.Trim()))
            .ToList();
        var context = new SplitsContext
        {
            ExpectedControls = expected,
            Punches = punches,
            PointsByCode = pointsByCode,
            // The resolved start — the same start the finish evaluation uses — so the over-time penalty
            // measures real elapsed.
            StartTime = resolvedStart,
            FinishTime = readout.FinishTime,
            TimeLimit = gs?.TimeLimitSeconds is { } secs ? TimeSpan.FromSeconds(secs) : null,
            PenaltyPerMinute = gs?.PenaltyPerMinute
        };
        var net = strategy.BuildSplits(context).TotalPoints;

        // The allowed controls this chip visited (intersection of punched and allowed), for the team tally.
        var allowed = new HashSet<string>(expected.Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase);
        var punched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in punches)
        {
            var code = p.ControlCode.Trim();
            if (code.Length > 0 && allowed.Contains(code))
                punched.Add(code);
        }
        return (net, punched);
    }

    /// <summary>
    /// Awards «Очки» (ranking points) for every member, per group, from the group's effective points rule
    /// (its per-day override, else the competition default). A group with no effective rule, a group whose
    /// discipline is unknown (Р-2), and any non-rankable member are left blank.
    ///
    /// A <see cref="PointsRuleKind.Table"/> rule keys off the member's place; a formula rule additionally
    /// references the group leader's time/score (the placed runner whose place is 1) and the group counts
    /// (registered / started / finished). The formula uses the adjusted time (Р-9).
    /// </summary>
    private void AwardPoints(
        IReadOnlyList<ParticipantDay> links,
        IReadOnlyDictionary<Guid, GroupDaySettings> settingsByGroup,
        DisciplineId dayDefault,
        CompetitionInfo? info,
        DayResultsAppData app,
        Dictionary<Guid, ParticipantDayResult> results)
    {
        var rules = app.PointsRules;
        if (rules.Count == 0)
            return;
        var ruleById = rules.ToDictionary(r => r.Id);
        var defaultRuleId = info?.DefaultPointsRuleId;

        // The members of each group, paired with their computed result, so points can be ranked per group.
        var membersByGroup = new Dictionary<Guid, List<(Guid LinkId, ParticipantDayResult Result)>>();
        foreach (var link in links)
        {
            if (link.GroupId is not { } gid || !results.TryGetValue(link.Id, out var result))
                continue;
            if (!membersByGroup.TryGetValue(gid, out var list))
                membersByGroup[gid] = list = [];
            list.Add((link.Id, result));
        }

        foreach (var (groupId, members) in membersByGroup)
        {
            // Effective rule: the group's override, else the competition default. A group override of
            // Guid.Empty is an explicit "no points" choice — score nothing, never fall back to the default.
            // Unknown ids likewise award nothing.
            var hasSettings = settingsByGroup.TryGetValue(groupId, out var gs);
            var ruleId = hasSettings && gs!.PointsRuleId is { } o
                ? o
                : defaultRuleId;
            if (ruleId is not { } id || id == Guid.Empty || !ruleById.TryGetValue(id, out var rule))
                continue;
            var pointsStrategy = _strategies.For(gs?.DisciplineOverride ?? dayDefault);
            // A relay scores the team's place instead (AwardRelayPoints, Р-37).
            if (pointsStrategy.IsUnknown || pointsStrategy.Competitors == CompetitorModel.TeamWithLegs)
                continue;

            // Group context the formula references: the leader (place 1) time/score and the three group
            // counts — N (everyone registered in the group), N_с (those who actually ran, i.e. whose chip
            // read-out shows a start box, a finish box or at least one control punch) and N_ф (those who
            // finished with a valid result: status OK). N_с deliberately does NOT key off ActualStart: on a
            // mass or assigned start there is no start punch at all, which would leave it at 0 for the whole
            // group. NONE of the three counts a поза конкурсом runner. A table rule keys off place only.
            var inCompetition = members.Where(m => !m.Result.OutOfCompetition).ToList();
            var placed = members.Where(m => m.Result.Place is not null).ToList();
            var groupSize = inCompetition.Count;
            var startedCount = inCompetition.Count(m => m.Result.WasOnCourse);
            var finishedCount = inCompetition.Count(m => m.Result.Status == FinishStatus.Ok);
            var leader = placed.FirstOrDefault(m => m.Result.Place == 1).Result;
            var leaderTime = (leader?.AdjustedTime ?? leader?.ResultTime)?.TotalSeconds;
            var leaderScore = leader?.Score;

            foreach (var (linkId, result) in members)
            {
                // Only a clean, in-competition finish earns points.
                if (result.Status != FinishStatus.Ok || result.OutOfCompetition || result.UnrankedReason is not null)
                    continue;

                var input = new PointsRuleInput(
                    Place: result.Place,
                    ResultTimeSeconds: (result.AdjustedTime ?? result.ResultTime)?.TotalSeconds,
                    Score: result.Score,
                    LeaderTimeSeconds: leaderTime,
                    LeaderScore: leaderScore,
                    GroupSize: groupSize,
                    StartedCount: startedCount,
                    FinishedCount: finishedCount);
                var points = PointsRuleEvaluator.Evaluate(rule, input);
                if (points is not null)
                    results[linkId] = results[linkId] with { Points = points };
            }
        }
    }

    /// <summary>
    /// Awards «виконаний розряд» (Додаток 89, simplified) for every member, per group. A group whose level
    /// is <see cref="GroupRankLevel.None"/> — or whose discipline does not award ranks (Р-7) — awards nothing;
    /// a group failing the validity conditions (fewer members than the configured minimum, or fewer distinct
    /// regions on the day) likewise awards nothing for anyone. Otherwise the course rank is the sum of the
    /// current-rank point values of the first N participants BY PLACE; the top <c>MasterCount</c> placed runners
    /// get «МСУ» (adult groups only); the rest are awarded from the editable qualification table by their
    /// (actual) result as a percentage of the group leader's.
    /// </summary>
    private void AwardRanks(
        IReadOnlyList<ParticipantDay> links,
        IReadOnlyDictionary<Guid, GroupDaySettings> settingsByGroup,
        IReadOnlyList<Participant> participants,
        DisciplineId dayDefault,
        DayResultsAppData app,
        Dictionary<Guid, ParticipantDayResult> results,
        Dictionary<Guid, GroupRankCalculation>? rankCalcs)
    {
        if (!app.HasRankData || !settingsByGroup.Values.Any(s => s.RankLevel != GroupRankLevel.None))
            return;

        var rows = app.RankQualification;
        var (minParticipants, minRegions, countForRank) = app.RankConditions;

        // Matched by TextKey so a participant's rank still finds the seeded rank when the two use look-alike
        // letters (the Roman numerals I/II/III are Latin in the seed but often arrive as Cyrillic «І» from a
        // UOF/XML import — visually identical, different code points — which otherwise scores 0).
        var pointsByRankName = new Dictionary<string, double>(TextKey.Comparer);
        foreach (var r in app.Ranks)
            pointsByRankName[r.Name] = r.Points;

        var rankByParticipant = participants.ToDictionary(p => p.Id, p => p.Rank ?? string.Empty);
        var regionByParticipant = participants.ToDictionary(p => p.Id, p => p.RegionId);

        // Distinct regions are counted across the WHOLE day (every participant running it), not per group —
        // the «не менше восьми областей» condition is competition-wide. When it fails, NO group awards a rank.
        var dayRegions = links
            .Select(l => regionByParticipant.TryGetValue(l.ParticipantId, out var rid) ? rid : null)
            .Where(rid => rid is not null)
            .Distinct()
            .Count();
        if (dayRegions < minRegions)
            return;

        var membersByGroup = new Dictionary<Guid, List<(ParticipantDay Link, ParticipantDayResult Result)>>();
        foreach (var link in links)
        {
            if (link.GroupId is not { } gid || !results.TryGetValue(link.Id, out var result))
                continue;
            if (!membersByGroup.TryGetValue(gid, out var list))
                membersByGroup[gid] = list = [];
            list.Add((link, result));
        }

        foreach (var (groupId, members) in membersByGroup)
        {
            if (!settingsByGroup.TryGetValue(groupId, out var gs) || gs.RankLevel == GroupRankLevel.None)
                continue;
            var strategy = _strategies.For(gs.DisciplineOverride ?? dayDefault);
            // Р-38: never a rank for a relay, whatever a plugin says.
            if (!strategy.AwardsRanks || strategy.Competitors == CompetitorModel.TeamWithLegs)
                continue;
            var level = gs.RankLevel;

            // Per-group validity (Додаток 89, п.7 — «не менше трьох»): too few participants ⇒ no ranks.
            if (members.Count < minParticipants)
                continue;

            // Course rank: the first N participants BY PLACE (unplaced sort last), summing their CURRENT-rank
            // point values — the protocol's «перші N за місцем» rule.
            var rank = (int)Math.Floor(members
                .OrderBy(m => m.Result.Place ?? int.MaxValue)
                .Take(countForRank)
                .Select(m => rankByParticipant.TryGetValue(m.Link.ParticipantId, out var rn)
                    && pointsByRankName.TryGetValue(rn, out var pts) ? pts : 0.0)
                .Sum());

            var pointsBased = strategy.UsesControlPointPoints;

            // Group leader (place 1) context the percentage is measured against.
            var placed = members.Where(m => m.Result.Place is not null).ToList();
            var leader = placed.FirstOrDefault(m => m.Result.Place == 1).Result;
            var leaderTime = leader?.ResultTime?.TotalSeconds;
            var leaderScore = leader?.Score;

            var masterCount = level == GroupRankLevel.Adult ? gs.MasterCount ?? 0 : 0;

            // The derivation line for the protocol: the applicable bracket's attainable ranks (highest first),
            // each with its % threshold and the cut-off it implies against the leader's result.
            if (rankCalcs is not null &&
                RankQualificationEvaluator.ApplicableRow(rows, rank, level) is { } bracket)
            {
                var leaderRef = pointsBased ? leaderScore : leaderTime;
                if (leaderRef is { } reference && reference > 0)
                {
                    var attainable = RankQualificationEvaluator.AttainableRanks(bracket, level, pointsBased);
                    if (attainable.Count > 0)
                    {
                        var entries = attainable
                            .Select(a => new RankCalculationEntry(
                                a.Name, a.Percent,
                                CutoffTimeSeconds: pointsBased ? null : reference * a.Percent / 100.0,
                                CutoffScore: pointsBased ? (int)Math.Round(reference * a.Percent / 100.0) : null))
                            .ToList();
                        rankCalcs[groupId] = new GroupRankCalculation(
                            CourseClass: attainable[0].Name, Rank: rank, Entries: entries);
                    }
                }
            }

            foreach (var (link, result) in members)
            {
                // Only a clean, in-competition finish can earn a rank.
                if (result.Status != FinishStatus.Ok || result.OutOfCompetition)
                    continue;

                // Master sporta: the top-N placed runners (adult groups only).
                if (masterCount > 0 && result.Place is { } place && place <= masterCount)
                {
                    results[link.Id] = results[link.Id] with { AwardedRank = RankQualificationEvaluator.Master };
                    continue;
                }

                // The rest: the qualification table, keyed off the result as a % of the leader's.
                double? percent = pointsBased
                    ? (leaderScore is { } ls && ls > 0 && result.Score is { } sc ? (double)sc / ls * 100.0 : null)
                    : (leaderTime is { } lt && lt > 0 && result.ResultTime is { } rt ? rt.TotalSeconds / lt * 100.0 : null);
                if (percent is not { } pct)
                    continue;

                var awarded = RankQualificationEvaluator.Award(rows, rank, level, pointsBased, pct);
                if (awarded is not null)
                    results[link.Id] = results[link.Id] with { AwardedRank = awarded };
            }
        }
    }
}
