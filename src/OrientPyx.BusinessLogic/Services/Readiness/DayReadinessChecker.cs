using System.Globalization;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services.Relay;
using OrientPyx.Plugins.Sdk.Toolkit.Patterns;

namespace OrientPyx.BusinessLogic.Services.Readiness;

/// <summary>What the readiness check reads: the day, its snapshot, the competition's additional fields (their
/// definitions are not in the snapshot) and whether chips are SportIdent cards (the local «Тип відмітки»).</summary>
public sealed record DayReadinessInput(
    EventDay Day, EventDaySnapshot Snapshot, IReadOnlyList<CustomField> Fields, bool SportIdentCards);

/// <summary>
/// «Перевірка готовності дня»: every problem of the day's data in one list. Most checks are the ones the cells already
/// tint (no group, a chip too small, an age breach, a course code the day has no КП for, a relay leg number without a
/// course…) and are computed by the same shared functions, so the list and the cells cannot disagree. Pure: no store,
/// no clock. Only groups the day has members in are checked — an empty group cannot give a wrong result.
/// </summary>
public sealed class DayReadinessChecker(IDisciplineStrategyProvider strategies)
{
    public DayReadiness Check(DayReadinessInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var issues = new Run(strategies, input).Issues();
        return new DayReadiness(input.Day.Id, input.Day.Number, Sort(issues));
    }

    // Errors first, then by where they are fixed (the day, the starts, groups, participants, teams, numbers), then by
    // the group's place on the day and by who.
    private static List<ReadinessIssue> Sort(IEnumerable<ReadinessIssue> issues) => issues
        .OrderBy(i => i.Severity)
        .ThenBy(i => i.Target)
        .ThenBy(i => i.GroupOrder)
        .ThenBy(i => i.GroupName, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(i => i.Subject, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(i => i.Kind)
        .ToList();

    private sealed class Run
    {
        // A finish to ask a format for its start with: only the date matters, and only when a start is given.
        private static readonly DateTimeOffset AnyFinish = new(2000, 1, 1, 12, 0, 0, TimeSpan.Zero);

        private readonly IDisciplineStrategyProvider _strategies;
        private readonly DayReadinessInput _input;
        private readonly EventDay _day;
        private readonly EventDaySnapshot _snapshot;
        private readonly List<ReadinessIssue> _issues = [];

        private readonly Dictionary<Guid, Group> _groups;
        // The day's groups (a settings row whose group still exists), by group id.
        private readonly Dictionary<Guid, GroupDaySettings> _settings;
        private readonly Dictionary<Guid, Participant> _participants;
        // The day's links of existing participants, one per participant (as every other per-day read).
        private readonly List<ParticipantDay> _links;
        private readonly Dictionary<Guid, RelayTeam> _teams;
        private readonly HashSet<Guid> _groupsWithMembers;
        private readonly Dictionary<Guid, (int Count, int MaxControls)> _variantStats;
        private readonly ILookup<Guid, GroupCourseVariant> _variants;
        private readonly Dictionary<DisciplineId, bool> _ownStart = [];
        // (number, leg) of the relay legs already reported for their course (№17), so «Варіанти за номерами» does not
        // report the same leg again.
        private readonly HashSet<(int Number, int Leg)> _legCourseReported = [];

        public Run(IDisciplineStrategyProvider strategies, DayReadinessInput input)
        {
            _strategies = strategies;
            _input = input;
            _day = input.Day;
            _snapshot = input.Snapshot;
            _groups = _snapshot.Groups.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First());
            _settings = _snapshot.GroupDaySettings
                .Where(s => _groups.ContainsKey(s.GroupId))
                .GroupBy(s => s.GroupId)
                .ToDictionary(g => g.Key, g => g.First());
            _participants = _snapshot.Participants.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
            var seen = new HashSet<Guid>();
            _links = _snapshot.Links
                .Where(l => l.EventDayId == _day.Id && _participants.ContainsKey(l.ParticipantId) && seen.Add(l.ParticipantId))
                .ToList();
            _teams = _snapshot.RelayTeams.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
            _groupsWithMembers = _links.Where(l => l.GroupId is not null).Select(l => l.GroupId!.Value)
                .Concat(_teams.Values.Select(t => t.GroupId))
                .ToHashSet();
            _variantStats = GroupDayRows.VariantStats(_snapshot.CourseVariants);
            _variants = _snapshot.CourseVariants.ToLookup(v => v.GroupId);
        }

        public List<ReadinessIssue> Issues()
        {
            CheckDay();
            CheckGroups();
            CheckParticipants();
            CheckStarts();
            CheckRelay();
            CheckNumberVariants();
            return _issues;
        }

        // ── Lookups

        private IDisciplineStrategy StrategyOf(GroupDaySettings gs) =>
            _strategies.For(gs.DisciplineOverride ?? _day.DefaultDiscipline);

        private bool IsRelay(DisciplineId id) => _strategies.For(id).Competitors == CompetitorModel.TeamWithLegs;

        private static bool IsRelay(IDisciplineStrategy strategy) => strategy.Competitors == CompetitorModel.TeamWithLegs;

        private string GroupName(Guid? groupId) =>
            groupId is { } id && _groups.TryGetValue(id, out var g) ? g.Name.Trim() : string.Empty;

        // The participant's group when it is one of the day's groups; null otherwise (none set, or not on the day).
        private GroupDaySettings? DayGroupOf(ParticipantDay link) =>
            link.GroupId is { } id && _settings.TryGetValue(id, out var gs) ? gs : null;

        private GroupDayRow RowOf(GroupDaySettings gs)
        {
            var stats = _variantStats.GetValueOrDefault(gs.GroupId);
            return GroupDayRows.Create(gs, _groups[gs.GroupId], _day.DefaultDiscipline, 0, stats.Count, stats.MaxControls);
        }

        // The format takes the start from somewhere of its own (not the chip's start box or the start protocol): asked
        // with neither, a built-in gives none. A plugin's call is guarded by the adapter.
        private bool GivesOwnStart(GroupDaySettings gs)
        {
            var id = gs.DisciplineOverride ?? _day.DefaultDiscipline;
            if (!_ownStart.TryGetValue(id, out var own))
            {
                own = _strategies.For(id).ResolveStart(new StartContext { FinishTime = AnyFinish }) is not null;
                _ownStart[id] = own;
            }
            return own;
        }

        private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

        private void Add(ReadinessIssue issue) => _issues.Add(issue);

        private ReadinessIssue ForDay(ReadinessKind kind, ReadinessTarget target, string key, params string[] args) =>
            new(kind, target, key, args) { DayId = _day.Id, Field = ReadinessField.Discipline };

        private ReadinessIssue ForGroup(ReadinessKind kind, GroupDaySettings gs, string key, params string[] args) =>
            new(kind, ReadinessTarget.Group, key, args)
            {
                Subject = GroupName(gs.GroupId),
                GroupName = GroupName(gs.GroupId),
                GroupOrder = gs.Order,
                DayId = _day.Id,
                GroupId = gs.GroupId,
                GroupSettingsId = gs.Id,
                Field = ReadinessField.CourseOrder
            };

        private ReadinessIssue ForParticipant(
            ReadinessKind kind, ParticipantDay link, ReadinessField field, string key, params string[] args)
        {
            var p = _participants[link.ParticipantId];
            var gs = DayGroupOf(link);
            return new ReadinessIssue(kind, ReadinessTarget.Participant, key, args)
            {
                Subject = p.FullName.Trim(),
                Number = p.Number.Trim(),
                GroupName = gs is null ? string.Empty : GroupName(gs.GroupId),
                GroupOrder = gs?.Order ?? int.MaxValue,
                DayId = _day.Id,
                LinkId = link.Id,
                ParticipantId = link.ParticipantId,
                GroupId = gs?.GroupId,
                GroupSettingsId = gs?.Id,
                Field = field
            };
        }

        private ReadinessIssue ForTeam(ReadinessKind kind, RelayTeam team, string key, params string[] args)
        {
            var gs = _settings.GetValueOrDefault(team.GroupId);
            return new ReadinessIssue(kind, ReadinessTarget.RelayTeam, key, args)
            {
                Subject = RelayTeamNumbers.Label(team.Number, team.Name.Trim()),
                Number = RelayTeamNumbers.Text(team.Number),
                GroupName = GroupName(team.GroupId),
                GroupOrder = gs?.Order ?? int.MaxValue,
                DayId = _day.Id,
                GroupId = team.GroupId,
                GroupSettingsId = gs?.Id,
                RelayTeamId = team.Id
            };
        }

        // ── №16: the day's own discipline has no strategy (its plugin is missing or switched off)

        private void CheckDay()
        {
            if (_strategies.For(_day.DefaultDiscipline).IsUnknown)
                Add(ForDay(ReadinessKind.UnknownDiscipline, ReadinessTarget.Day, "Banner.MissingPlugin",
                    _day.DefaultDiscipline.Value, PluginOf(_day.DefaultDiscipline)));
        }

        private static string PluginOf(DisciplineId id) => id.Value.Split('.')[0];

        // ── №11–14, 16: the groups' courses and own disciplines

        private void CheckGroups()
        {
            var dayCodes = GroupCourseChecks.DayCodes(_snapshot.ControlPoints);
            var startCode = _snapshot.ControlPoints.FirstOrDefault(c => c.Type == ControlPointType.Start)?.Code.Trim();
            var finishCode = _snapshot.ControlPoints.FirstOrDefault(c => c.Type == ControlPointType.Finish)?.Code.Trim();

            foreach (var gs in _settings.Values.OrderBy(s => s.Order))
            {
                if (!_groupsWithMembers.Contains(gs.GroupId))
                    continue;
                var strategy = StrategyOf(gs);
                if (gs.DisciplineOverride is { } own && strategy.IsUnknown)
                    Add(ForGroup(ReadinessKind.UnknownDiscipline, gs, "Banner.MissingPlugin", own.Value, PluginOf(own))
                        with { Field = ReadinessField.Discipline });
                if (!strategy.UsesColumn(GroupColumn.CourseOrder))
                    continue;

                if (!GroupCourseChecks.HasSeveralCourses(strategy, gs.CourseAssignmentMode))
                {
                    // A course that does not parse is reported as such; one with no control at all (blank, or only the
                    // start and finish) as no course.
                    if (CheckCourse(gs, GroupName(gs.GroupId), gs.CourseOrder, dayCodes, strategy.UsesCoursePattern, startCode, finishCode)
                        && strategy.ControlCount(gs.CourseOrder) <= 0)
                        Add(ForGroup(ReadinessKind.GroupWithoutCourse, gs, "Readiness.Issue.GroupWithoutCourse", GroupName(gs.GroupId)));
                    continue;
                }

                // Several courses (scatter variants, a relay's named / leg courses): each one is checked, and the group
                // needs at least one.
                var courses = _variants[gs.GroupId]
                    .Where(v => v.CourseOrder.Trim().Length > 0)
                    .OrderBy(v => v.Order)
                    .ToList();
                var anyCourse = false;
                foreach (var v in courses)
                {
                    var code = v.Code.Trim();
                    var subject = code.Length == 0 ? GroupName(gs.GroupId) : $"{GroupName(gs.GroupId)} · {code}";
                    if (CheckCourse(gs, subject, v.CourseOrder, dayCodes, pattern: true, startCode, finishCode)
                        && CourseOrder.CountCourseControls(v.CourseOrder) > 0)
                        anyCourse = true;
                }
                if (!anyCourse && courses.All(v => CoursePattern.Parse(v.CourseOrder, startCode, finishCode).IsValid))
                {
                    Add(ForGroup(ReadinessKind.GroupWithoutCourse, gs, "Readiness.Issue.GroupWithoutCourse", GroupName(gs.GroupId)));
                    continue;
                }

                // «Своя на кожен етап»: a leg no course is marked for runs «дистанцію не призначено».
                if (IsRelay(strategy) && gs.CourseAssignmentMode == RelayCourseMode.PerLeg)
                {
                    var covered = courses.Where(v => v.Leg is not null).Select(v => v.Leg!.Value).ToHashSet();
                    var missing = Enumerable.Range(1, Math.Max(1, gs.LegCount ?? 1)).Where(l => !covered.Contains(l)).ToList();
                    if (missing.Count > 0)
                        Add(ForGroup(ReadinessKind.RelayLegWithoutVariant, gs, "Readiness.Issue.RelayLegWithoutVariant",
                            string.Join(", ", missing.Select(Num))));
                }
            }
        }

        // №12 (a code no КП of the day has — only once the day has КП, as the cell) and №13 (a pattern that does not
        // parse). False when the pattern does not parse.
        private bool CheckCourse(
            GroupDaySettings gs, string subject, string courseOrder, IReadOnlySet<string> dayCodes, bool pattern,
            string? startCode, string? finishCode)
        {
            var unknown = GroupCourseChecks.UnknownCodes(courseOrder, dayCodes);
            if (unknown.Count > 0)
                Add(ForGroup(ReadinessKind.UnknownCourseCodes, gs, "Groups.UnknownCodes.Tooltip", string.Join(", ", unknown))
                    with { Subject = subject });

            if (!pattern || courseOrder.Trim().Length == 0)
                return true;
            var parsed = CoursePattern.Parse(courseOrder, startCode, finishCode);
            if (parsed.IsValid)
                return true;
            Add(ForGroup(ReadinessKind.InvalidCoursePattern, gs, CoursePatternErrors.KeyOf(parsed.Errors[0].Kind),
                parsed.Errors[0].Token ?? string.Empty) with { Subject = subject });
            return false;
        }

        // ── №1–8: the participants

        private void CheckParticipants()
        {
            var fields = _input.Fields;
            var values = _snapshot.FieldValues
                .GroupBy(v => (v.ParticipantId, Key: v.Key.ToUpperInvariant()))
                .ToDictionary(g => g.Key, g => g.First().Value);
            string ValueOf(Guid participantId, string key) =>
                values.GetValueOrDefault((participantId, key.ToUpperInvariant())) ?? string.Empty;

            foreach (var link in _links)
            {
                var p = _participants[link.ParticipantId];
                var gs = DayGroupOf(link);
                if (gs is null)
                    Add(ForParticipant(ReadinessKind.NoGroup, link, ReadinessField.Group, "Participants.Group.Missing.Tooltip"));

                var chip = link.Chip.Trim();
                if (chip.Length == 0)
                {
                    Add(ForParticipant(ReadinessKind.NoChip, link, ReadinessField.Chip, "Readiness.Issue.NoChip"));
                }
                else if (gs is not null && _input.SportIdentCards)
                {
                    var needed = ChipCapacity.NeededPunches(RowOf(gs), StrategyOf(gs));
                    if (SiCard.TooSmall(chip, needed) is { } card)
                        Add(ForParticipant(ReadinessKind.ChipTooSmall, link, ReadinessField.Chip, "Participants.ChipCapacity.TooSmall",
                            chip, card.Model, Num(card.Capacity), GroupName(gs.GroupId), Num(needed)));
                }

                if (gs is not null)
                {
                    var group = _groups[gs.GroupId];
                    if (Group.CheckAgeWindow(p.BirthDate?.Year, group.MinBirthYear, group.MaxBirthYear) is { } age)
                        Add(ForParticipant(ReadinessKind.BirthYearOutOfGroup, link, ReadinessField.BirthDate,
                            age.TooOld ? "Participants.AgeViolation.TooOld" : "Participants.AgeViolation.TooYoung",
                            GroupName(gs.GroupId), Num(age.BoundYear)));

                    // A field the group's discipline requires (a plugin's RequiredFields) — once the field exists, as the cell.
                    foreach (var key in StrategyOf(gs).RequiredFields.Distinct(StringComparer.OrdinalIgnoreCase))
                        if (fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)) is { } field
                            && ValueOf(p.Id, field.Key).Trim().Length == 0)
                            Add(ForParticipant(ReadinessKind.RequiredFieldEmpty, link, ReadinessField.CustomField,
                                "Readiness.Issue.RequiredFieldEmpty", field.Label, GroupName(gs.GroupId)) with { FieldKey = field.Key });
                }

                foreach (var field in fields)
                {
                    var value = ValueOf(p.Id, field.Key);
                    if (field.IsInvalid(value))
                        Add(ForParticipant(ReadinessKind.InvalidFieldValue, link, ReadinessField.CustomField,
                            "Readiness.Issue.InvalidFieldValue", field.Label, value.Trim()) with { FieldKey = field.Key });
                }
            }

            // №3: a chip is unique on the day — the read-out goes to its first holder only.
            ReportDuplicates(l => l.Chip.Trim(), ReadinessKind.DuplicateChip, ReadinessField.Chip, "Readiness.Issue.DuplicateChip");
            // №8: a number is unique (the edit refuses a taken one; an import does not).
            ReportDuplicates(l => _participants[l.ParticipantId].Number.Trim(), ReadinessKind.DuplicateNumber,
                ReadinessField.Number, "Readiness.Issue.DuplicateNumber");
        }

        private void ReportDuplicates(
            Func<ParticipantDay, string> valueOf, ReadinessKind kind, ReadinessField field, string key)
        {
            foreach (var same in _links
                         .Select(l => (Link: l, Value: valueOf(l)))
                         .Where(x => x.Value.Length > 0)
                         .GroupBy(x => x.Value, TextKey.Comparer)
                         .Where(g => g.Count() > 1))
            {
                foreach (var (link, value) in same)
                {
                    var others = same.Where(x => x.Link.Id != link.Id)
                        .Select(x => _participants[x.Link.ParticipantId].FullName.Trim());
                    Add(ForParticipant(kind, link, field, key, value, string.Join(", ", others)));
                }
            }
        }

        // ── №9, №10: start times

        private void CheckStarts()
        {
            // Who must have a start time: a member of a day group whose format takes it from the start protocol — in a
            // relay group only a team's first leg (the next ones start at the handover) — by the time it would get.
            var byGroup = new Dictionary<Guid, List<(ParticipantDay Link, bool HasStart)>>();
            foreach (var link in _links)
            {
                if (DayGroupOf(link) is not { } gs || GivesOwnStart(gs))
                    continue;
                TimeSpan? start;
                if (IsRelay(StrategyOf(gs)))
                {
                    if (link.RelayTeamId is not { } teamId || !_teams.ContainsKey(teamId) || link.Leg != 1)
                        continue;
                    start = link.StartTime ?? gs.MassStartTime;
                }
                else
                {
                    start = link.StartTime;
                }
                if (!byGroup.TryGetValue(gs.GroupId, out var list))
                    byGroup[gs.GroupId] = list = [];
                list.Add((link, start is not null));
            }

            var total = byGroup.Values.Sum(l => l.Count);
            if (total == 0)
                return;
            // Nobody on the day has one: the draw has not been run, or the start is punched at a «Старт» station — one
            // line for the day, not one per group.
            if (byGroup.Values.All(l => l.All(x => !x.HasStart)))
            {
                Add(ForDay(ReadinessKind.DayWithoutStarts, ReadinessTarget.Draw, "Readiness.Issue.DayWithoutStarts", Num(total))
                    with { Field = ReadinessField.None });
                return;
            }

            foreach (var (groupId, list) in byGroup)
            {
                var gs = _settings[groupId];
                if (list.All(x => !x.HasStart))
                {
                    var relay = IsRelay(StrategyOf(gs));
                    Add(ForGroup(relay ? ReadinessKind.RelayNoMassStart : ReadinessKind.GroupWithoutStarts, gs,
                            relay ? "Readiness.Issue.RelayNoMassStart" : "Readiness.Issue.GroupWithoutStarts",
                            GroupName(groupId), Num(list.Count))
                        with { Target = relay ? ReadinessTarget.MassStart : ReadinessTarget.Draw, Field = ReadinessField.None });
                    continue;
                }
                foreach (var (link, hasStart) in list)
                    if (!hasStart)
                        Add(ForParticipant(ReadinessKind.NoStartTime, link, ReadinessField.StartTime, "Readiness.Issue.NoStartTime"));
            }
        }

        // ── №17–20: relay teams and their runners

        private void CheckRelay()
        {
            if (_teams.Count == 0)
                return;
            var byBib = LegCourseCheck.ByBibGroups(_day, _settings.Values, IsRelay);
            var legCourses = LegCourseCheck.For(byBib, _snapshot.LegCourseAssignments, _snapshot.CourseVariants, _groups.Values);
            var places = RelayPlacements.Build(_day, _links, _teams.Values.ToList(), _settings.Values, legCourses);

            // №17: a leg number «за номером етапу» with no course the results can use — the participant's red group cell
            // (which shows «без групи» instead when the participant has no group of the day).
            foreach (var link in _links)
            {
                if (DayGroupOf(link) is null || !places.TryGetValue(link.Id, out var place) || place.CourseIssue is not { } issue)
                    continue;
                if (place.TeamNumber is { } number && place.Leg is { } leg)
                    _legCourseReported.Add((number, leg));
                Add(ForParticipant(ReadinessKind.RelayLegCourse, link, ReadinessField.Group, LegCourseKey(issue.Kind),
                    issue.GroupName, issue.Bib, issue.Detail) with { Number = place.Bib });
            }

            var legCounts = RelayPlacements.LegCounts(_settings.Values);
            var membersByTeam = _links.Where(l => l.RelayTeamId is not null)
                .GroupBy(l => l.RelayTeamId!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());
            foreach (var team in _teams.Values)
            {
                // A team in a group that is not a relay of the day (or not on it) runs nothing.
                if (!_settings.TryGetValue(team.GroupId, out var gs) || !IsRelay(StrategyOf(gs)))
                    continue;
                var legCount = legCounts.GetValueOrDefault(team.GroupId, 1);
                var members = membersByTeam.GetValueOrDefault(team.Id) ?? [];

                // №19: some leg has nobody — the team runs «поза конкурсом» (empty teams too).
                var filled = members.Where(m => m.Leg is { } l && l >= 1 && l <= legCount).Select(m => m.Leg!.Value).Distinct().Count();
                if (filled < legCount)
                    Add(ForTeam(ReadinessKind.RelayTeamIncomplete, team, "Participants.Relay.Incomplete", Num(filled), Num(legCount)));

                // №20: no number — not online, no «100.2»; in a group «за номером етапу» №17 already says so per leg.
                if (team.Number is null && !byBib.Contains(team.GroupId))
                    Add(ForTeam(ReadinessKind.RelayTeamWithoutNumber, team, "Readiness.Issue.RelayTeamWithoutNumber"));

                // №18: on the team but on no leg — evaluated alone, never placed.
                foreach (var m in members.Where(m => m.Leg is null))
                    Add(ForTeam(ReadinessKind.RelayRunnerWithoutLeg, team, "Readiness.Issue.RelayRunnerWithoutLeg",
                        RelayTeamNumbers.Label(team.Number, team.Name.Trim())) with
                    {
                        Subject = _participants[m.ParticipantId].FullName.Trim(),
                        LinkId = m.Id,
                        ParticipantId = m.ParticipantId
                    });
            }
        }

        private static string LegCourseKey(RelayLegCourseIssueKind kind) => kind switch
        {
            RelayLegCourseIssueKind.NoTeamNumber => "Participants.Relay.Course.NoTeamNumber",
            RelayLegCourseIssueKind.OtherGroup => "Participants.Relay.Course.OtherGroup",
            RelayLegCourseIssueKind.UnknownCourse => "Participants.Relay.Course.UnknownCourse",
            _ => "Participants.Relay.Course.NotAssigned"
        };

        // ── №22: «Варіанти за номерами»

        private void CheckNumberVariants()
        {
            if (_snapshot.LegCourseAssignments.Count == 0)
                return;
            var data = NumberVariantRows.Build(_day, _snapshot, IsRelay);
            // The page words its texts for scatter on a day without a relay group.
            var suffix = data.HasRelayGroups ? string.Empty : ".Scatter";

            // (а) The number's row names another group than the team / runner holding it — the results never apply it.
            foreach (var row in data.Rows.Where(r => r.GroupMismatch && !_legCourseReported.Contains((r.Number, r.Leg))))
                Add(ForNumber(ReadinessKind.NumberVariantWrongGroup, row, "NumberVariants.GroupMismatch" + suffix,
                    row.GroupName, row.HolderGroupName));

            // (б) A numbered scatter runner whose number has no map (relay legs without one are №17).
            foreach (var row in data.Unassigned.Where(r => !NumberVariantRows.IsRelayRow(r, _day, _snapshot, IsRelay)))
                Add(ForNumber(ReadinessKind.NumberWithoutVariant, row, "NumberVariants.Missing.Scatter"));
        }

        private ReadinessIssue ForNumber(ReadinessKind kind, NumberVariantRow row, string key, params string[] args)
        {
            var gs = row.GroupId is { } g ? _settings.GetValueOrDefault(g) : null;
            return new ReadinessIssue(kind, ReadinessTarget.NumberVariants, key, args)
            {
                Subject = row.Runner.Length > 0 ? row.Runner : row.TeamName,
                Number = row.Bib,
                GroupName = row.GroupName,
                GroupOrder = gs?.Order ?? int.MaxValue,
                DayId = _day.Id,
                GroupId = row.GroupId,
                GroupSettingsId = gs?.Id,
                TeamNumber = row.Number,
                Leg = row.Leg
            };
        }
    }
}
