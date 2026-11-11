using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

// «Старт → Масовий старт»: one start time per group (every runner starts together); a relay group's leg 1 starts
// at it and the page also sets the restart time of each later leg (Р-27, Р-29).
public sealed partial class CompetitionEditorService
{
    public async Task<MassStartData> GetMassStartDataAsync(Guid dayId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return new MassStartData([]);

        // One transactional read: links, teams and group settings agree with each other.
        var snapshot = await _eventStore.GetDaySnapshotAsync(FolderPath, dayId, cancellationToken);
        var day = snapshot.Days.FirstOrDefault(d => d.Id == dayId);
        if (day is null)
            return new MassStartData([]);

        var groupName = snapshot.Groups.ToDictionary(g => g.Id, g => g.Name);
        var links = DistinctLinks(snapshot.Links);
        var teamGroup = snapshot.RelayTeams.ToDictionary(t => t.Id, t => t.GroupId);

        var groups = new List<MassStartGroup>(snapshot.GroupDaySettings.Count);
        foreach (var gs in snapshot.GroupDaySettings)
        {
            if (!groupName.TryGetValue(gs.GroupId, out var name))
                continue;
            var isRelay = IsRelay(gs.DisciplineOverride ?? day.DefaultDiscipline);
            var starting = MassStartLinks(links, teamGroup, gs.GroupId, isRelay);

            // A relay leg-1 runner without a start of its own starts at the group's mass start (Р-27).
            var starts = starting
                .Select(l => l.StartTime ?? (isRelay ? gs.MassStartTime : null))
                .Distinct()
                .ToList();
            var mixed = starts.Count > 1;
            var current = starts.Count == 1 ? starts[0] : isRelay && starts.Count == 0 ? gs.MassStartTime : null;

            groups.Add(new MassStartGroup(
                gs.GroupId,
                name,
                isRelay ? snapshot.RelayTeams.Count(t => t.GroupId == gs.GroupId) : starting.Count,
                isRelay,
                isRelay ? Math.Max(1, gs.LegCount ?? 1) : 1,
                mixed ? null : current,
                mixed,
                isRelay ? gs.MassStartTime : null,
                isRelay ? RelayRestartTimes.Parse(gs.RestartTimesJson) : new Dictionary<int, TimeSpan>()));
        }

        return new MassStartData(groups);
    }

    public async Task<MassStartResult> ApplyMassStartAsync(
        Guid dayId, IReadOnlyList<MassStartAssignment> groups, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (_session.CurrentEvent is null || groups.Count == 0)
            return new MassStartResult(0, 0);

        await EnsureDayEditableAsync(dayId, cancellationToken);

        var folder = FolderPath;
        var snapshot = await _eventStore.GetDaySnapshotAsync(folder, dayId, cancellationToken);
        var day = snapshot.Days.FirstOrDefault(d => d.Id == dayId);
        if (day is null)
            return new MassStartResult(0, 0);

        var settingsByGroup = new Dictionary<Guid, GroupDaySettings>();
        foreach (var gs in snapshot.GroupDaySettings)
            settingsByGroup.TryAdd(gs.GroupId, gs);
        var links = DistinctLinks(snapshot.Links);
        var teamGroup = snapshot.RelayTeams.ToDictionary(t => t.Id, t => t.GroupId);

        var starts = new List<(Guid LinkId, TimeSpan StartTime)>();
        var relayGroups = new List<(GroupDaySettings Settings, TimeSpan Start, string RestartsJson)>();
        foreach (var assignment in groups)
        {
            if (!settingsByGroup.TryGetValue(assignment.GroupId, out var gs))
                continue;   // the group no longer runs on the day
            var isRelay = IsRelay(gs.DisciplineOverride ?? day.DefaultDiscipline);
            foreach (var link in MassStartLinks(links, teamGroup, gs.GroupId, isRelay))
                starts.Add((link.Id, assignment.Start));

            if (isRelay)
            {
                var legCount = Math.Max(1, gs.LegCount ?? 1);
                var restarts = (assignment.RestartTimes ?? new Dictionary<int, TimeSpan>())
                    .Where(kv => kv.Key >= 2 && kv.Key <= legCount)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                relayGroups.Add((gs, assignment.Start, RelayRestartTimes.Format(restarts)));
            }
        }

        var written = starts.Count > 0
            ? await _eventStore.SetParticipantDayStartTimesBatchAsync(folder, starts, cancellationToken)
            : 0;
        foreach (var (gs, start, restartsJson) in relayGroups)
            await _eventStore.UpdateRelaySettingsAsync(folder, gs.Id, gs.LegCount, start, restartsJson,
                gs.RestartRanking, cancellationToken);

        return new MassStartResult(written, relayGroups.Count);
    }

    // The links that start at a group's mass start: every runner of an individual group; in a relay group the
    // leg-1 runners, plus the group's runners on no leg (judged alone from their own start, Р-22) — a later leg
    // starts at the handoff or its restart instead. A relay runner belongs to its team's group (like the
    // calculator), a runner on no team to its own.
    private static List<ParticipantDay> MassStartLinks(
        IReadOnlyList<ParticipantDay> links, IReadOnlyDictionary<Guid, Guid> teamGroup, Guid groupId, bool isRelay)
    {
        var result = new List<ParticipantDay>();
        foreach (var link in links)
        {
            var onTeam = link.RelayTeamId is { } tid && teamGroup.ContainsKey(tid);
            var group = onTeam ? teamGroup[link.RelayTeamId!.Value] : link.GroupId;
            if (group != groupId)
                continue;
            if (isRelay && onTeam && link.Leg is >= 2)
                continue;
            result.Add(link);
        }
        return result;
    }
}
