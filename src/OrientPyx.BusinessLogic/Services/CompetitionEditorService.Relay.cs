using OrientPyx.BusinessLogic.Entities;
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

        var groups = snapshot.GroupDaySettings
            .Where(s => IsRelay(s.DisciplineOverride ?? day.DefaultDiscipline))
            .Select(s => ToRelaySettings(s, groupNames.TryGetValue(s.GroupId, out var n) ? n : string.Empty))
            .ToList();

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
                    link?.Chip ?? string.Empty, result?.Legs.FirstOrDefault(l => l.Leg == leg)));
            }
            teams.Add(new RelayTeamRow(team.Id, team.GroupId, team.Number, team.Name, team.ClubId, team.RegionId,
                team.OutOfCompetition, team.StatusOverride, legs, result));
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
        return ToRelaySettings(gs, groups.FirstOrDefault(g => g.Id == groupId)?.Name ?? string.Empty);
    }

    public async Task SaveRelayGroupSettingsAsync(RelayGroupSettings settings, CancellationToken cancellationToken = default)
    {
        await EnsureDayEditableAsync(CurrentDayId, cancellationToken);
        var restarts = settings.RestartTimes
            .Where(kv => kv.Key >= 2 && kv.Key <= settings.LegCount)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        await _eventStore.UpdateRelaySettingsAsync(FolderPath, settings.SettingsId, Math.Clamp(settings.LegCount, 1, 50),
            settings.MassStartTime, RelayRestartTimes.Format(restarts), settings.RestartRanking, cancellationToken);
    }

    public async Task<int> CreateRelayTeamsAsync(Guid groupId, int count, int firstNumber, CancellationToken cancellationToken = default)
    {
        if (count <= 0)
            return 0;
        var folder = FolderPath;
        var dayId = CurrentDayId;
        await EnsureDayEditableAsync(dayId, cancellationToken);

        // Numbers are unique within the day (Р-20): a taken number is skipped, the count is still honoured.
        var taken = (await _eventStore.GetRelayTeamsAsync(folder, dayId, cancellationToken)).Select(t => t.Number).ToHashSet();
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
        if (edit.Number <= 0 || teams.Any(t => t.Id != edit.Id && t.Number == edit.Number))
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
            StatusOverride = edit.StatusOverride
        }, cancellationToken);
        return true;
    }

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

    private static RelayGroupSettings ToRelaySettings(GroupDaySettings s, string groupName) => new(
        s.Id, s.GroupId, groupName, Math.Max(1, s.LegCount ?? 1), s.MassStartTime,
        RelayRestartTimes.Parse(s.RestartTimesJson), s.RestartRanking);
}
