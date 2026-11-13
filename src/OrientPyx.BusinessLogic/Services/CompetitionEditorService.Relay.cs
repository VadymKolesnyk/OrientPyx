using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

// Relay teams (Р-20…Р-26): the «Команди» page and the relay group settings.
public sealed partial class CompetitionEditorService
{
    public async Task<bool> CurrentDayHasRelayAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || _session.CurrentDay is null)
            return false;
        var settings = await _eventStore.GetGroupDaySettingsAsync(FolderPath, CurrentDayId, cancellationToken);
        return IsRelay(CurrentDayDefaultDiscipline) || settings.Any(s => IsRelay(s.DisciplineOverride ?? CurrentDayDefaultDiscipline));
    }

    // The numbers of the days that run at least one relay group (the day default or a group override).
    private async Task<List<int>> RelayDayNumbersAsync(CancellationToken cancellationToken)
    {
        var folder = FolderPath;
        var days = await _eventStore.GetDaysAsync(folder, cancellationToken);
        var result = new List<int>();
        foreach (var day in days)
        {
            var settings = await _eventStore.GetGroupDaySettingsAsync(folder, day.Id, cancellationToken);
            if (IsRelay(day.DefaultDiscipline) || settings.Any(s => IsRelay(s.DisciplineOverride ?? day.DefaultDiscipline)))
                result.Add(day.Number);
        }
        return result;
    }

    // The relay days of the rows a UOF file of a relay event of its own leaves open (UofParticipant.RelayOnImportedDays):
    // the target day of a current-day import; in all-days mode the days the file lists for the row, else the
    // competition's relay days, else every day. The row is entered on those days only.
    private async Task<UofParticipantData> ResolveImportedRelayDaysAsync(
        UofParticipantData data, ParticipantImportScope scope, CancellationToken cancellationToken)
    {
        if (!data.Participants.Any(p => p.RelayOnImportedDays))
            return data;

        var allDays = scope.Mode == ParticipantImportMode.AllDays;
        List<int> fallback;
        if (allDays)
        {
            fallback = await RelayDayNumbersAsync(cancellationToken);
            if (fallback.Count == 0)
                fallback = (await _eventStore.GetDaysAsync(FolderPath, cancellationToken)).Select(d => d.Number).Order().ToList();
        }
        else
        {
            fallback = [scope.TargetDayNumber];
        }

        return data with
        {
            Participants = data.Participants.Select(p =>
            {
                if (!p.RelayOnImportedDays)
                    return p;
                var days = allDays && p.DayNumbers.Count > 0 ? p.DayNumbers : fallback;
                return p with { DayNumbers = allDays ? days : p.DayNumbers, RelayDayNumbers = days };
            }).ToList()
        };
    }

    public async Task<IReadOnlyList<RelayDaySwitch>> GetRelayDaySwitchesAsync(
        UofParticipantData data, ParticipantImportScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(scope);
        if (_session.CurrentEvent is null)
            return [];

        // The days the import put the file's relay rows on: a UOF row's relay days, a numbered row's days.
        var folder = FolderPath;
        var days = await _eventStore.GetDaysAsync(folder, cancellationToken);
        var allDays = scope.Mode == ParticipantImportMode.AllDays;
        var allNumbers = days.Select(d => d.Number).ToList();
        data = await ResolveImportedRelayDaysAsync(data, scope, cancellationToken);
        var touched = data.Participants.SelectMany(RelayDaysOf).ToHashSet();
        if (touched.Count == 0)
            return [];

        IEnumerable<int> RelayDaysOf(UofParticipant p)
        {
            if (p.RelayTeamNumber is not null)
                return !allDays ? [scope.TargetDayNumber] : p.DayNumbers.Count > 0 ? p.DayNumbers : allNumbers;
            return p.RelayTeamName.Length > 0 ? p.RelayDayNumbers.Where(d => allDays || d == scope.TargetDayNumber) : [];
        }

        var groups = (await _eventStore.GetGroupsAsync(folder, cancellationToken)).ToDictionary(g => g.Id, g => g.Name);
        var result = new List<RelayDaySwitch>();
        foreach (var day in days.Where(d => touched.Contains(d.Number)).OrderBy(d => d.Number))
        {
            if (day.IsLocked || IsRelay(day.DefaultDiscipline)
                || (await _eventStore.GetRelayTeamsAsync(folder, day.Id, cancellationToken)).Count == 0)
                continue;
            var settings = await _eventStore.GetGroupDaySettingsAsync(folder, day.Id, cancellationToken);
            var kept = settings
                .Where(s => !IsRelay(s.DisciplineOverride ?? day.DefaultDiscipline))
                .Select(s => groups.GetValueOrDefault(s.GroupId, string.Empty))
                .Where(n => n.Length > 0)
                .ToList();
            result.Add(new RelayDaySwitch(day.Id, day.Number, day.DefaultDiscipline, kept));
        }
        return result;
    }

    public async Task<EventDay?> SwitchDayToRelayAsync(Guid dayId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return null;
        var folder = FolderPath;
        var day = (await _eventStore.GetDaysAsync(folder, cancellationToken)).FirstOrDefault(d => d.Id == dayId);
        if (day is null || IsRelay(day.DefaultDiscipline))
            return day;
        await EnsureDayEditableAsync(dayId, cancellationToken);

        // Every group keeps the type it runs: one on the former day type gets it as its own first, then the day becomes
        // the relay, then a group that ran the relay by its own type follows the day.
        var former = day.DefaultDiscipline;
        var settings = await _eventStore.GetGroupDaySettingsAsync(folder, dayId, cancellationToken);
        foreach (var s in settings.Where(s => s.DisciplineOverride is null))
        {
            s.DisciplineOverride = former;
            await _eventStore.UpdateGroupDaySettingsAsync(folder, s, cancellationToken);
        }
        day.DefaultDiscipline = DisciplineId.Relay;
        await _eventStore.UpdateDayAsync(folder, day, cancellationToken);
        foreach (var s in settings.Where(s => s.DisciplineOverride == DisciplineId.Relay))
        {
            s.DisciplineOverride = null;
            await _eventStore.UpdateGroupDaySettingsAsync(folder, s, cancellationToken);
        }
        return day;
    }

    private bool IsRelay(Disciplines.DisciplineId id) =>
        _strategies.For(id).Competitors == CompetitorModel.TeamWithLegs;

    public async Task<RelayTeamsData> GetRelayTeamsDataAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || _session.CurrentDay is null)
            return new RelayTeamsData([], [], []);

        var folder = FolderPath;
        var day = _session.CurrentDay;
        var snapshot = await _eventStore.GetDaySnapshotAsync(folder, day.Id, cancellationToken);
        var groupNames = snapshot.Groups.ToDictionary(g => g.Id, g => g.Name);

        var codesByGroup = CourseCodesByGroup(snapshot.CourseVariants);
        var groups = snapshot.GroupDaySettings
            .Where(s => IsRelay(s.DisciplineOverride ?? day.DefaultDiscipline))
            .Select(s => ToRelaySettings(s, groupNames.TryGetValue(s.GroupId, out var n) ? n : string.Empty,
                codesByGroup.GetValueOrDefault(s.GroupId)))
            .ToList();
        var bibCourses = snapshot.LegCourseAssignments
            .GroupBy(a => (a.TeamNumber, a.Leg))
            .ToDictionary(g => g.Key, g => g.First().CourseKey);
        var assignedSets = LegCourseSets(snapshot.LegCourseAssignments);

        var teamResults = new Dictionary<Guid, TeamDayResult>();
        await ComputeDayResultsAsync(folder, day, cancellationToken, snapshot: snapshot, teamResults: teamResults);

        var participants = snapshot.Participants.ToDictionary(p => p.Id);
        var linksByTeam = snapshot.Links.Where(l => l.RelayTeamId is not null)
            .GroupBy(l => l.RelayTeamId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        var legCountByGroup = groups.ToDictionary(g => g.GroupId, g => g.LegCount);

        var teams = new List<RelayTeamRow>(snapshot.RelayTeams.Count);
        foreach (var team in snapshot.RelayTeams)
        {
            var legCount = legCountByGroup.TryGetValue(team.GroupId, out var lc) ? lc : 1;
            var members = linksByTeam.TryGetValue(team.Id, out var ms) ? ms : [];
            var result = teamResults.TryGetValue(team.Id, out var tr) ? tr : null;
            var maxLeg = Math.Max(legCount, members.Select(m => m.Leg ?? 0).DefaultIfEmpty(0).Max());
            var legs = new List<RelayLegRow>(maxLeg);
            for (var leg = 1; leg <= maxLeg; leg++)
            {
                var link = members.FirstOrDefault(m => m.Leg == leg);
                var person = link is not null && participants.TryGetValue(link.ParticipantId, out var p) ? p : null;
                legs.Add(new RelayLegRow(leg, link?.Id, link?.ParticipantId, person?.FullName ?? string.Empty,
                    link?.Chip ?? string.Empty, result?.Legs.FirstOrDefault(l => l.Leg == leg),
                    (team.Number is { } number ? bibCourses.GetValueOrDefault((number, leg)) : null) ?? string.Empty));
            }
            // «Без етапу»: the team's runners with no leg (an import brought more runners than legs) — to be fixed.
            var unplaced = members.Where(m => m.Leg is null)
                .OrderBy(m => m.Order)
                .Select(m => new RelayUnplacedRunner(m.Id, m.ParticipantId,
                    participants.TryGetValue(m.ParticipantId, out var who) ? who.FullName : string.Empty, m.Chip))
                .ToList();
            teams.Add(new RelayTeamRow(team.Id, team.GroupId, team.Number, team.Name, team.ClubId, team.RegionId,
                team.OutOfCompetition, team.StatusOverride, legs, result, team.CourseSet)
            {
                Unplaced = unplaced,
                AssignedCourseSet = team.Number is { } n && assignedSets.TryGetValue(n, out var set) ? set : string.Empty
            });
        }

        var clubNames = snapshot.Clubs.ToDictionary(c => c.Id, c => c.Name);
        var linkByParticipant = snapshot.Links.GroupBy(l => l.ParticipantId).ToDictionary(g => g.Key, g => g.First());
        var teamNumber = snapshot.RelayTeams.ToDictionary(t => t.Id, t => t.Number);
        var runners = snapshot.Participants.Select(p =>
        {
            var link = linkByParticipant.TryGetValue(p.Id, out var l) ? l : null;
            var tid = link?.RelayTeamId is { } id && teamNumber.ContainsKey(id) ? id : (Guid?)null;
            return new RelayRunner(p.Id, link?.Id, p.FullName, p.Number, p.BirthDate?.Year,
                p.ClubId is { } cid && clubNames.TryGetValue(cid, out var cn) ? cn : string.Empty,
                tid, tid is { } t ? teamNumber[t] : null, tid is null ? null : link!.Leg);
        }).ToList();

        return new RelayTeamsData(groups, teams, runners);
    }

    public async Task<RelayGroupSettings?> GetRelayGroupSettingsAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || _session.CurrentDay is null)
            return null;
        var settings = await _eventStore.GetGroupDaySettingsAsync(FolderPath, CurrentDayId, cancellationToken);
        var gs = settings.FirstOrDefault(s => s.GroupId == groupId);
        if (gs is null)
            return null;
        var groups = await _eventStore.GetGroupsAsync(FolderPath, cancellationToken);
        var variants = await _eventStore.GetCourseVariantsAsync(FolderPath, CurrentDayId, cancellationToken);
        return ToRelaySettings(gs, groups.FirstOrDefault(g => g.Id == groupId)?.Name ?? string.Empty,
            CourseCodesByGroup(variants).GetValueOrDefault(groupId));
    }

    public async Task SaveRelayGroupSettingsAsync(RelayGroupSettings settings, CancellationToken cancellationToken = default)
    {
        await EnsureDayEditableAsync(CurrentDayId, cancellationToken);
        var restarts = settings.RestartTimes
            .Where(kv => kv.Key >= 2 && kv.Key <= settings.LegCount)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        await _eventStore.UpdateRelaySettingsAsync(FolderPath, settings.SettingsId, Math.Clamp(settings.LegCount, 1, 50),
            settings.MassStartTime, RelayRestartTimes.Format(restarts), settings.RestartRanking, cancellationToken);
        await _eventStore.UpdateRelayCourseSettingsAsync(FolderPath, settings.SettingsId, settings.CourseMode,
            RelayLegCourses.FormatSet(RelayLegCourses.ParseSet(settings.TeamCourseSet)), cancellationToken);
    }

    public async Task<int> CreateRelayTeamsAsync(Guid groupId, int count, int firstNumber, CancellationToken cancellationToken = default)
    {
        if (count <= 0)
            return 0;
        var folder = FolderPath;
        var dayId = CurrentDayId;
        await EnsureDayEditableAsync(dayId, cancellationToken);

        // Numbers are unique within the day (Р-20): a taken number is skipped, the count is still honoured.
        var taken = (await _eventStore.GetRelayTeamsAsync(folder, dayId, cancellationToken))
            .Where(t => t.Number is not null).Select(t => t.Number!.Value).ToHashSet();
        var teams = new List<RelayTeam>(count);
        for (var number = Math.Max(1, firstNumber); teams.Count < Math.Min(count, 1000); number++)
        {
            if (!taken.Add(number))
                continue;
            teams.Add(new RelayTeam { EventDayId = dayId, GroupId = groupId, Number = number });
        }
        await _eventStore.AddRelayTeamsAsync(folder, teams, cancellationToken);
        return teams.Count;
    }

    public async Task<bool> UpdateRelayTeamAsync(RelayTeamEdit edit, CancellationToken cancellationToken = default)
    {
        var folder = FolderPath;
        var dayId = CurrentDayId;
        await EnsureDayEditableAsync(dayId, cancellationToken);
        var teams = await _eventStore.GetRelayTeamsAsync(folder, dayId, cancellationToken);
        // A blank number leaves the team without one (numbers can be handed out later); a given one is unique (Р-20).
        if (edit.Number is { } number && (number <= 0 || teams.Any(t => t.Id != edit.Id && t.Number == number)))
            return false;

        await _eventStore.UpdateRelayTeamAsync(folder, new RelayTeam
        {
            Id = edit.Id,
            EventDayId = dayId,
            GroupId = edit.GroupId,
            Number = edit.Number,
            Name = (edit.Name ?? string.Empty).Trim(),
            ClubId = edit.ClubId,
            RegionId = edit.RegionId,
            OutOfCompetition = edit.OutOfCompetition,
            StatusOverride = edit.StatusOverride,
            CourseSet = RelayLegCourses.FormatSet(RelayLegCourses.ParseSet(edit.CourseSet))
        }, cancellationToken);
        return true;
    }

    public async Task<bool> SetRelayTeamNumbersAsync(
        IReadOnlyList<RelayTeamNumberChange> numbers, CancellationToken cancellationToken = default)
    {
        if (numbers.Count == 0)
            return true;
        if (numbers.Any(n => n.Number is <= 0))
            return false;
        var dayId = CurrentDayId;
        await EnsureDayEditableAsync(dayId, cancellationToken);
        return await _eventStore.SetRelayTeamNumbersAsync(FolderPath, dayId, numbers, cancellationToken);
    }

    public async Task<int> SetLegCoursesAsync(IReadOnlyList<LegCourseEntry> entries, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || _session.CurrentDay is null || entries.Count == 0)
            return 0;
        var dayId = CurrentDayId;
        await EnsureDayEditableAsync(dayId, cancellationToken);
        // The group is the one of the team holding the number (none yet → the row names no group).
        var groupOfNumber = (await _eventStore.GetRelayTeamsAsync(FolderPath, dayId, cancellationToken))
            .Where(t => t.Number is not null)
            .GroupBy(t => t.Number!.Value)
            .ToDictionary(g => g.Key, g => g.First().GroupId);
        var rows = entries
            .Where(e => e.TeamNumber > 0 && e.Leg >= 1)
            .Select(e => new LegCourseAssignment
            {
                EventDayId = dayId,
                GroupId = groupOfNumber.TryGetValue(e.TeamNumber, out var g) ? g : null,
                TeamNumber = e.TeamNumber,
                Leg = e.Leg,
                CourseKey = (e.CourseKey ?? string.Empty).Trim()
            })
            .ToList();
        await _eventStore.UpsertLegCourseAssignmentsAsync(FolderPath, dayId, rows, cancellationToken);
        return rows.Count(r => r.CourseKey.Length > 0);
    }

    // The courses assigned to each number's legs, in leg order, as a «набір на команду» («1A 2B 3A»).
    internal static Dictionary<int, string> LegCourseSets(IEnumerable<LegCourseAssignment> assignments) =>
        assignments
            .Where(a => a.CourseKey.Trim().Length > 0)
            .GroupBy(a => a.TeamNumber)
            .ToDictionary(g => g.Key, g => RelayLegCourses.FormatSet(g.OrderBy(a => a.Leg).Select(a => a.CourseKey)));

    // The named course codes of each group on the day, in their order.
    private static Dictionary<Guid, IReadOnlyList<string>> CourseCodesByGroup(IReadOnlyList<GroupCourseVariant> variants) =>
        variants.GroupBy(v => v.GroupId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<string>)g.OrderBy(v => v.Order).Select(v => v.Code.Trim()).Where(c => c.Length > 0).ToList());

    public async Task DeleteRelayTeamAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        await EnsureDayEditableAsync(CurrentDayId, cancellationToken);
        await _eventStore.DeleteRelayTeamAsync(FolderPath, teamId, cancellationToken);
    }

    public async Task<bool> AssignRelayLegAsync(Guid teamId, int leg, Guid? participantId, CancellationToken cancellationToken = default)
    {
        var folder = FolderPath;
        var dayId = CurrentDayId;
        await EnsureDayEditableAsync(dayId, cancellationToken);
        var team = (await _eventStore.GetRelayTeamsAsync(folder, dayId, cancellationToken)).FirstOrDefault(t => t.Id == teamId);
        if (team is null || leg <= 0)
            return false;

        Guid? linkId = null;
        if (participantId is { } pid)
            // Puts the runner on the day in the team's group when they don't run it yet (one transaction, so no
            // duplicate link), else just moves their group to the team's (Р-22).
            linkId = await _eventStore.SetParticipantDayGroupAsync(folder, pid, dayId, team.GroupId, cancellationToken);
        return await _eventStore.AssignRelayLegAsync(folder, teamId, leg, linkId, cancellationToken);
    }

    // ── The participant tables' relay cells: «Команда» (the team's name, like a rogaine team), «Номер команди» and «Етап»
    // typed on a runner's row; «Номер етапу» «100.2» is read from them.

    // The relay places of the day's links (team + leg, as stored, and how many of the team's legs are filled), keyed by
    // link id, and whether the day runs a relay at all — a relay group (the day default or an override) or a team.
    private async Task<(Dictionary<Guid, RelayPlacement> ByLink, bool IsRelayDay)> LoadRelayPlacementsAsync(
        string folder, EventDay day, IReadOnlyList<ParticipantDay> links, CancellationToken cancellationToken)
    {
        var teams = await _eventStore.GetRelayTeamsAsync(folder, day.Id, cancellationToken);
        var settings = await _eventStore.GetGroupDaySettingsAsync(folder, day.Id, cancellationToken);
        var isRelayDay = teams.Count > 0
            || IsRelay(day.DefaultDiscipline)
            || settings.Any(s => IsRelay(s.DisciplineOverride ?? day.DefaultDiscipline));

        var byLink = new Dictionary<Guid, RelayPlacement>();
        if (teams.Count == 0)
            return (byLink, isRelayDay);
        var teamsById = teams.ToDictionary(t => t.Id);
        var legCounts = settings.ToDictionary(s => s.GroupId, s => Math.Max(1, s.LegCount ?? 1));
        var dayLinks = links.Where(l => l.EventDayId == day.Id && l.RelayTeamId is not null).ToList();
        var legsByTeam = dayLinks.Where(l => l.Leg is not null)
            .GroupBy(l => l.RelayTeamId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Leg!.Value).ToHashSet());
        foreach (var link in dayLinks)
        {
            if (!teamsById.TryGetValue(link.RelayTeamId!.Value, out var team))
                continue;
            var legCount = legCounts.TryGetValue(team.GroupId, out var count) ? count : 1;
            byLink[link.Id] = new RelayPlacement(team.Id, team.Number, team.Name, link.Leg, legCount)
            {
                FilledLegs = legsByTeam.TryGetValue(team.Id, out var legs) ? legs.Count(l => l >= 1 && l <= legCount) : 0
            };
        }
        return (byLink, isRelayDay);
    }

    public async Task<IReadOnlyDictionary<Guid, RelayPlacement>> GetRelayPlacementsAsync(
        Guid dayId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return new Dictionary<Guid, RelayPlacement>();
        var folder = FolderPath;
        var day = (await _eventStore.GetDaysAsync(folder, cancellationToken)).FirstOrDefault(d => d.Id == dayId);
        if (day is null)
            return new Dictionary<Guid, RelayPlacement>();
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var (byLink, _) = await LoadRelayPlacementsAsync(folder, day, links, cancellationToken);
        var byParticipant = new Dictionary<Guid, RelayPlacement>(byLink.Count);
        foreach (var link in links)
            if (byLink.TryGetValue(link.Id, out var place))
                byParticipant[link.ParticipantId] = place;
        return byParticipant;
    }

    // Everything a relay cell edit of one runner on one day reads, in one place.
    private sealed record RelayEdit(
        string Folder, EventDay Day, Guid ParticipantId, IReadOnlyList<ParticipantDay> Links, IReadOnlyList<RelayTeam> Teams,
        ParticipantDay? Own, RelayTeam? Current, IReadOnlyList<GroupDaySettings> Settings,
        IReadOnlyDictionary<Guid, GroupDaySettings> RelayGroups)
    {
        public int LegCount(RelayTeam team) =>
            Math.Max(1, Settings.FirstOrDefault(s => s.GroupId == team.GroupId)?.LegCount ?? 1);

        public int MemberCount(RelayTeam team) => Links.Count(l => l.RelayTeamId == team.Id);

        // Where a new team of the runner goes: their own group when it is a relay one, else the day's only relay group.
        public Guid? NewTeamGroup => Own?.GroupId is { } g && RelayGroups.ContainsKey(g) ? g
            : RelayGroups.Count == 1 ? RelayGroups.Keys.First() : null;
    }

    private async Task<RelayEdit?> LoadRelayEditAsync(Guid dayId, Guid participantId, CancellationToken cancellationToken)
    {
        if (_session.CurrentEvent is null)
            return null;
        var folder = FolderPath;
        await EnsureDayEditableAsync(dayId, cancellationToken);
        var day = (await _eventStore.GetDaysAsync(folder, cancellationToken)).FirstOrDefault(d => d.Id == dayId);
        if (day is null)
            return null;

        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var teams = await _eventStore.GetRelayTeamsAsync(folder, dayId, cancellationToken);
        var settings = await _eventStore.GetGroupDaySettingsAsync(folder, dayId, cancellationToken);
        var own = links.FirstOrDefault(l => l.ParticipantId == participantId);
        var current = own?.RelayTeamId is { } ownTeam ? teams.FirstOrDefault(t => t.Id == ownTeam) : null;
        var relayGroups = settings
            .Where(s => IsRelay(s.DisciplineOverride ?? day.DefaultDiscipline))
            .ToDictionary(s => s.GroupId);
        return new RelayEdit(folder, day, participantId, links, teams, own, current, settings, relayGroups);
    }

    // The runner's place as it now stands (after a write), with the team's filled legs.
    private async Task<RelayPlacement?> CurrentPlaceAsync(RelayEdit edit, CancellationToken cancellationToken)
    {
        var links = await _eventStore.GetParticipantDaysAsync(edit.Folder, edit.Day.Id, cancellationToken);
        if (links.FirstOrDefault(l => l.ParticipantId == edit.ParticipantId) is not { } own)
            return null;
        var (byLink, _) = await LoadRelayPlacementsAsync(edit.Folder, edit.Day, links, cancellationToken);
        return byLink.GetValueOrDefault(own.Id);
    }

    public async Task<RelayPlacementOutcome> PlaceRelayRunnerAsync(
        RelayPlacementRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await LoadRelayEditAsync(request.DayId, request.ParticipantId, cancellationToken) is not { } edit)
            return new RelayPlacementOutcome(RelayPlacementStatus.Unchanged);

        // Off the team (with or without a leg): the runner stays on the day in their group (Р-22).
        if (request.TeamId is null && request.TeamNumber is null)
            return await ReleaseRunnerAsync(edit, cancellationToken);

        // The team by id (a leg edit on the runner's own team — it may have no number), else by number; a number the day
        // has no team for creates the team, like the import (Р-25).
        var team = request.TeamId is { } teamId
            ? edit.Teams.FirstOrDefault(t => t.Id == teamId)
            : edit.Teams.FirstOrDefault(t => t.Number == request.TeamNumber);
        if (team is not null)
            return await PlaceOnTeamAsync(edit, team, request.Leg, request.ReplaceOccupant, created: false, cancellationToken);
        if (request.TeamId is not null || request.TeamNumber is not { } number || number <= 0)
            return new RelayPlacementOutcome(RelayPlacementStatus.Unchanged);
        return await PlaceOnNewTeamAsync(edit, number, string.Empty, groupId: null, request.Leg, cancellationToken);
    }

    public async Task<RelayPlacementOutcome> EditRunnerRelayTeamAsync(
        RelayTeamCellEdit change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (await LoadRelayEditAsync(change.DayId, change.ParticipantId, cancellationToken) is not { } edit)
            return new RelayPlacementOutcome(RelayPlacementStatus.Unchanged);
        return change.Field == RelayTeamField.Name
            ? await EditTeamNameAsync(edit, change, cancellationToken)
            : await EditTeamNumberAsync(edit, change, cancellationToken);
    }

    // «Команда»: like a rogaine team — the runner joins the team of that name (in their group first), a new name makes a
    // new team, an empty cell takes them off the team. A name no team has, typed for a runner already on a named team
    // with teammates, could also mean renaming that team: asked.
    private async Task<RelayPlacementOutcome> EditTeamNameAsync(
        RelayEdit edit, RelayTeamCellEdit change, CancellationToken cancellationToken)
    {
        var name = (change.Value ?? string.Empty).Trim();
        var current = edit.Current;
        if (name.Length == 0)
            return current is null
                ? new RelayPlacementOutcome(RelayPlacementStatus.Unchanged)
                : await ReleaseRunnerAsync(edit, cancellationToken);
        if (current is not null && TextKey.Same(current.Name.Trim(), name))
            return new RelayPlacementOutcome(RelayPlacementStatus.Unchanged);

        var named = edit.Teams.Where(t => TextKey.Same(t.Name.Trim(), name)).ToList();
        var group = current?.GroupId ?? edit.Own?.GroupId;
        var inGroup = named.Where(t => t.GroupId == group).ToList();
        var target = inGroup.Count == 1 ? inGroup[0] : named.Count == 1 ? named[0] : null;
        if (target is null && named.Count > 1)
            return new RelayPlacementOutcome(RelayPlacementStatus.AmbiguousTeam, TeamName: name) { MemberCount = named.Count };
        if (target is not null)
            return await PlaceOnTeamAsync(edit, target, leg: null, replace: false, created: false, cancellationToken);

        if (current is null)
            return await PlaceOnNewTeamAsync(edit, number: null, name, groupId: null, leg: null, cancellationToken);
        // An unnamed team gets the name, and a runner alone on their team renames it — nothing else can be meant.
        var members = edit.MemberCount(current);
        if (current.Name.Trim().Length == 0 || members <= 1 || change.Choice == RelayTeamChoice.ChangeTeam)
            return await ChangeTeamAsync(edit, current, current.Number, name, cancellationToken);
        if (change.Choice == RelayTeamChoice.Ask)
            return new RelayPlacementOutcome(RelayPlacementStatus.ConfirmTeamChange, current.Number, TeamName: current.Name)
            {
                NewValue = name, MemberCount = members, CanMoveRunner = true
            };
        return await PlaceOnNewTeamAsync(edit, number: null, name, current.GroupId, leg: null, cancellationToken);
    }

    // «Номер команди»: the number belongs to the team, so changing it on a runner's row is asked — renumber the team for
    // all its runners, or move this runner to the team with that number (a new one when the day has none). A number
    // another team has can only mean moving the runner (asked too). A runner on no team joins the team with the number.
    private async Task<RelayPlacementOutcome> EditTeamNumberAsync(
        RelayEdit edit, RelayTeamCellEdit change, CancellationToken cancellationToken)
    {
        var text = (change.Value ?? string.Empty).Trim();
        int? number = null;
        if (text.Length > 0)
        {
            if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed) || parsed <= 0)
                return new RelayPlacementOutcome(RelayPlacementStatus.Unchanged);
            number = parsed;
        }

        var current = edit.Current;
        if (current is null)
        {
            if (number is not { } wanted)
                return new RelayPlacementOutcome(RelayPlacementStatus.Unchanged);
            var existing = edit.Teams.FirstOrDefault(t => t.Number == wanted);
            return existing is not null
                ? await PlaceOnTeamAsync(edit, existing, leg: null, replace: false, created: false, cancellationToken)
                : await PlaceOnNewTeamAsync(edit, wanted, string.Empty, groupId: null, leg: null, cancellationToken);
        }
        if (current.Number == number)
            return new RelayPlacementOutcome(RelayPlacementStatus.Unchanged);

        var other = number is { } n ? edit.Teams.FirstOrDefault(t => t.Id != current.Id && t.Number == n) : null;
        if (other is not null)
            return change.Choice == RelayTeamChoice.MoveRunner
                ? await PlaceOnTeamAsync(edit, other, leg: null, replace: false, created: false, cancellationToken)
                : new RelayPlacementOutcome(RelayPlacementStatus.NumberTaken, number, TeamName: current.Name)
                {
                    OtherTeam = RelayTeamNumbers.Label(other.Number, other.Name)
                };

        switch (change.Choice)
        {
            case RelayTeamChoice.Ask:
                return new RelayPlacementOutcome(RelayPlacementStatus.ConfirmTeamChange, current.Number, TeamName: current.Name)
                {
                    NewValue = text, MemberCount = edit.MemberCount(current), CanMoveRunner = true
                };
            case RelayTeamChoice.ChangeTeam:
                return await ChangeTeamAsync(edit, current, number, current.Name, cancellationToken);
            default:
                // Only this runner: to a new team with the number in their group; a cleared number takes them off the team.
                return number is { } newNumber
                    ? await PlaceOnNewTeamAsync(edit, newNumber, string.Empty, current.GroupId, leg: null, cancellationToken)
                    : await ReleaseRunnerAsync(edit, cancellationToken);
        }
    }

    public async Task<RelayPlacementOutcome> ChangeRelayRunnerGroupAsync(
        RelayGroupEdit change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (await LoadRelayEditAsync(change.DayId, change.ParticipantId, cancellationToken) is not
                { Own: { } own, Current: { } team } edit
            || team.GroupId == change.GroupId
            || edit.Settings.All(s => s.GroupId != change.GroupId))
            return new RelayPlacementOutcome(RelayPlacementStatus.Unchanged);

        if (change.Choice == RelayTeamChoice.ChangeTeam)
        {
            // The whole team, into another relay group: its runners follow it there (Р-22).
            if (!edit.RelayGroups.ContainsKey(change.GroupId))
                return new RelayPlacementOutcome(RelayPlacementStatus.Unchanged);
            team.GroupId = change.GroupId;
            await _eventStore.UpdateRelayTeamAsync(edit.Folder, team, cancellationToken);
            return new RelayPlacementOutcome(RelayPlacementStatus.TeamChanged, team.Number, own.Leg ?? 0,
                LegCount: edit.LegCount(team), Place: await CurrentPlaceAsync(edit, cancellationToken), TeamName: team.Name)
            {
                MemberCount = edit.MemberCount(team)
            };
        }

        // Only this runner: off the team, then into the group — they stay on the day.
        await _eventStore.ReleaseRelayRunnerAsync(edit.Folder, own.Id, cancellationToken);
        await _eventStore.SetParticipantDayGroupAsync(edit.Folder, change.ParticipantId, change.DayId, change.GroupId,
            cancellationToken);
        return new RelayPlacementOutcome(RelayPlacementStatus.Released, team.Number, own.Leg ?? 0, TeamName: team.Name);
    }

    // Renames / renumbers the runner's team for all its runners (a taken number was ruled out by the caller, Р-20).
    private async Task<RelayPlacementOutcome> ChangeTeamAsync(
        RelayEdit edit, RelayTeam team, int? number, string name, CancellationToken cancellationToken)
    {
        team.Number = number;
        team.Name = name;
        await _eventStore.UpdateRelayTeamAsync(edit.Folder, team, cancellationToken);
        return new RelayPlacementOutcome(RelayPlacementStatus.TeamChanged, number, edit.Own?.Leg ?? 0,
            LegCount: edit.LegCount(team), Place: await CurrentPlaceAsync(edit, cancellationToken), TeamName: name)
        {
            MemberCount = edit.MemberCount(team)
        };
    }

    private async Task<RelayPlacementOutcome> ReleaseRunnerAsync(RelayEdit edit, CancellationToken cancellationToken)
    {
        if (edit.Own is null || edit.Current is null)
            return new RelayPlacementOutcome(RelayPlacementStatus.Unchanged);
        await _eventStore.ReleaseRelayRunnerAsync(edit.Folder, edit.Own.Id, cancellationToken);
        return new RelayPlacementOutcome(RelayPlacementStatus.Released, edit.Current.Number, edit.Own.Leg ?? 0,
            TeamName: edit.Current.Name);
    }

    // A new team (number and/or name) in the given group — by default the runner's relay group, else the day's only
    // one — with the runner on it.
    private async Task<RelayPlacementOutcome> PlaceOnNewTeamAsync(
        RelayEdit edit, int? number, string name, Guid? groupId, int? leg, CancellationToken cancellationToken)
    {
        if ((groupId ?? edit.NewTeamGroup) is not { } gid)
            return new RelayPlacementOutcome(edit.RelayGroups.Count == 0
                ? RelayPlacementStatus.NoRelayGroup : RelayPlacementStatus.AmbiguousGroup, number, TeamName: name);
        var team = new RelayTeam { EventDayId = edit.Day.Id, GroupId = gid, Number = number, Name = name };
        await _eventStore.AddRelayTeamsAsync(edit.Folder, [team], cancellationToken);
        return await PlaceOnTeamAsync(edit, team, leg, replace: false, created: true, cancellationToken);
    }

    // Puts the runner on a leg of the team: the named leg, else the leg they have when it is free there, else the first
    // free one. A teammate's leg swaps the two; another runner's leg needs <paramref name="replace"/>.
    private async Task<RelayPlacementOutcome> PlaceOnTeamAsync(
        RelayEdit edit, RelayTeam team, int? leg, bool replace, bool created, CancellationToken cancellationToken)
    {
        var own = edit.Own;
        var legCount = edit.LegCount(team);
        var sameTeam = own is not null && own.RelayTeamId == team.Id;
        var mates = edit.Links.Where(l => l.RelayTeamId == team.Id && l.ParticipantId != edit.ParticipantId).ToList();
        RelayPlacementOutcome Refused(RelayPlacementStatus status, int refusedLeg = 0) =>
            new(status, team.Number, refusedLeg, LegCount: legCount, TeamCreated: created, TeamName: team.Name);

        if (leg is null)
        {
            if (sameTeam && own!.Leg is not null)
                return Refused(RelayPlacementStatus.Unchanged, own.Leg.Value);
            // Keep the leg the runner had (moving between teams keeps «the 2nd leg»), else the first free one — also
            // for a runner the team has without a leg.
            bool Free(int l) => mates.All(m => m.Leg != l);
            leg = own?.Leg is { } kept && kept <= legCount && Free(kept)
                ? kept
                : Enumerable.Range(1, legCount).Cast<int?>().FirstOrDefault(l => Free(l!.Value));
            if (leg is null)
                return Refused(RelayPlacementStatus.TeamFull);
        }
        var target = leg.Value;
        if (target < 1 || target > legCount)
            return Refused(RelayPlacementStatus.LegOutOfRange, target);
        if (sameTeam && own!.Leg == target)
            return Refused(RelayPlacementStatus.Unchanged, target);

        var holder = mates.FirstOrDefault(m => m.Leg == target);
        var holderName = holder is null ? string.Empty : await ParticipantNameAsync(edit.Folder, holder.ParticipantId, cancellationToken);
        if (holder is not null && sameTeam && own!.Leg is { } previous)
        {
            // A teammate holds the leg: the two swap (reordering a team's legs is the usual edit here).
            await _eventStore.AssignRelayLegAsync(edit.Folder, team.Id, target, own.Id, cancellationToken);
            await _eventStore.AssignRelayLegAsync(edit.Folder, team.Id, previous, holder.Id, cancellationToken);
            return new RelayPlacementOutcome(RelayPlacementStatus.Swapped, team.Number, target, holderName, legCount,
                Place: await CurrentPlaceAsync(edit, cancellationToken), TeamName: team.Name);
        }
        if (holder is not null && !replace)
            return new RelayPlacementOutcome(RelayPlacementStatus.LegTaken, team.Number, target, holderName, legCount, created,
                TeamName: team.Name);

        // Puts the runner on the day in the team's group when they don't run it yet, else moves their group to the
        // team's (Р-22) — then onto the leg (the holder, if any, is left without one).
        var movedTo = string.Empty;
        if (own is not null && own.GroupId != team.GroupId)
            movedTo = (await _eventStore.GetGroupsAsync(edit.Folder, cancellationToken)).FirstOrDefault(g => g.Id == team.GroupId)?.Name
                ?? string.Empty;
        var linkId = await _eventStore.SetParticipantDayGroupAsync(
            edit.Folder, edit.ParticipantId, edit.Day.Id, team.GroupId, cancellationToken);
        await _eventStore.AssignRelayLegAsync(edit.Folder, team.Id, target, linkId, cancellationToken);
        return new RelayPlacementOutcome(RelayPlacementStatus.Placed, team.Number, target, holderName, legCount, created, movedTo,
            await CurrentPlaceAsync(edit, cancellationToken), team.Name);
    }

    private async Task<string> ParticipantNameAsync(string folder, Guid participantId, CancellationToken cancellationToken) =>
        (await _eventStore.GetParticipantsAsync(folder, cancellationToken)).FirstOrDefault(p => p.Id == participantId)?.FullName.Trim()
        ?? string.Empty;

    private static RelayGroupSettings ToRelaySettings(
        GroupDaySettings s, string groupName, IReadOnlyList<string>? courseCodes) => new(
        s.Id, s.GroupId, groupName, Math.Max(1, s.LegCount ?? 1), s.MassStartTime,
        RelayRestartTimes.Parse(s.RestartTimesJson), s.RestartRanking,
        s.CourseAssignmentMode, s.TeamCourseSet, courseCodes ?? []);
}
