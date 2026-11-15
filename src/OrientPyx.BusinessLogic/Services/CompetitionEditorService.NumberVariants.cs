using System.Globalization;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

// «Варіанти за номерами»: the day's number → variation table (the maps to hand out), plus the numbers still without one.
public sealed partial class CompetitionEditorService
{
    public async Task<NumberVariantsData> GetNumberVariantsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || _session.CurrentDay is null)
            return NumberVariantsData.Empty;

        var day = _session.CurrentDay;
        var snapshot = await _eventStore.GetDaySnapshotAsync(FolderPath, day.Id, cancellationToken);

        var groupNames = snapshot.Groups.ToDictionary(g => g.Id, g => g.Name);
        var settingsByGroup = snapshot.GroupDaySettings.GroupBy(s => s.GroupId).ToDictionary(g => g.Key, g => g.First());
        bool IsRelayGroup(Guid groupId) =>
            settingsByGroup.TryGetValue(groupId, out var s) && IsRelay(s.DisciplineOverride ?? day.DefaultDiscipline);
        string GroupName(Guid? groupId) =>
            groupId is { } id && groupNames.TryGetValue(id, out var name) ? name : string.Empty;

        // Each group's variants in their order (one per code).
        var variants = snapshot.CourseVariants
            .GroupBy(v => v.GroupId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<NumberVariantCode>)g
                    .OrderBy(v => v.Order)
                    .Select(v => new NumberVariantCode(v.Code.Trim(), v.CourseOrder.Trim()))
                    .Where(v => v.Code.Length > 0)
                    .DistinctBy(v => v.Code, TextKey.Comparer)
                    .ToList());
        string OrderOf(Guid? groupId, string code) =>
            groupId is { } id && variants.TryGetValue(id, out var list)
                ? list.FirstOrDefault(v => TextKey.Comparer.Equals(v.Code, code))?.CourseOrder ?? string.Empty
                : string.Empty;

        var participants = snapshot.Participants.ToDictionary(p => p.Id);
        var teamsByNumber = snapshot.RelayTeams
            .Where(t => t.Number is not null)
            .GroupBy(t => t.Number!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var legRunner = snapshot.Links
            .Where(l => l.RelayTeamId is not null && l.Leg is not null)
            .GroupBy(l => (l.RelayTeamId!.Value, l.Leg!.Value))
            .ToDictionary(g => g.Key, g => g.First().ParticipantId);
        // A scatter runner by bib: a day member of a group that is not a relay (and on no team) whose number reads as that
        // number.
        var runnerByNumber = new Dictionary<int, ParticipantDay>();
        foreach (var link in snapshot.Links.Where(l => l.RelayTeamId is null && (l.GroupId is not { } g || !IsRelayGroup(g))))
            if (participants.TryGetValue(link.ParticipantId, out var p)
                && int.TryParse(p.Number.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
                runnerByNumber.TryAdd(n, link);

        string NameOf(Guid participantId) =>
            participants.TryGetValue(participantId, out var p) ? p.FullName.Trim() : string.Empty;

        // Who holds a number: in a relay group (or when the group is unknown) the team with that number, in any other
        // group the runner with that bib.
        (RelayTeam? Team, ParticipantDay? Runner) HolderOf(int number, Guid? groupId)
        {
            bool? relay = groupId is { } g && settingsByGroup.ContainsKey(g) ? IsRelayGroup(g) : null;
            if (relay != false && teamsByNumber.TryGetValue(number, out var team))
                return (team, null);
            if (relay != true && runnerByNumber.TryGetValue(number, out var link))
                return (null, link);
            return (null, null);
        }

        var legsOfNumber = snapshot.LegCourseAssignments
            .Where(a => a.CourseKey.Trim().Length > 0)
            .GroupBy(a => a.TeamNumber)
            .ToDictionary(g => g.Key, g => g.Count());

        // ownGroup: the group the assignment names (null when it names none or one that does not exist).
        NumberVariantRow Row(Guid? ownGroup, int number, int leg, string course)
        {
            var (team, runnerLink) = HolderOf(number, ownGroup);
            var holderGroup = team?.GroupId ?? runnerLink?.GroupId;
            var groupId = ownGroup ?? holderGroup;
            var runner = team is not null
                ? legRunner.TryGetValue((team.Id, leg), out var pid) ? NameOf(pid) : string.Empty
                : runnerLink is not null ? NameOf(runnerLink.ParticipantId) : string.Empty;
            // A relay leg or a number with several legs prints as «101.2»; a scatter runner's single map as «101».
            var relay = team is not null || (groupId is { } g && IsRelayGroup(g));
            var bib = relay || leg > 1 || legsOfNumber.GetValueOrDefault(number) > 1
                ? RelayBib.Format(number, leg)
                : number.ToString(CultureInfo.InvariantCulture);
            var code = course.Trim();
            var order = code.Length > 0 ? OrderOf(groupId, code) : string.Empty;
            return new NumberVariantRow(
                groupId, GroupName(groupId), holderGroup, GroupName(holderGroup),
                number, leg, bib, code, order, team?.Name.Trim() ?? string.Empty, runner,
                Missing: code.Length == 0,
                UnknownCourse: code.Length > 0 && order.Length == 0);
        }

        var rows = new List<NumberVariantRow>();
        var assigned = new HashSet<(int Number, int Leg)>();
        foreach (var a in snapshot.LegCourseAssignments)
            if (a.CourseKey.Trim().Length > 0 && assigned.Add((a.TeamNumber, a.Leg)))
                rows.Add(Row(a.GroupId is { } g && groupNames.ContainsKey(g) ? g : null, a.TeamNumber, a.Leg, a.CourseKey));
        var assignedNumbers = assigned.Select(k => k.Number).ToHashSet();
        var groupsWithRows = rows.Where(r => r.GroupId is not null).Select(r => r.GroupId!.Value).ToHashSet();

        // A relay team takes its courses by number «за номером етапу», or in «набір на команду» when it has no set of its
        // own and the group's default set does not stand in for it.
        bool TakesCoursesByNumber(RelayTeam team, GroupDaySettings s) => s.CourseAssignmentMode switch
        {
            RelayCourseMode.ByBib => true,
            RelayCourseMode.TeamSet => team.CourseSet.Trim().Length == 0
                                       && (s.TeamCourseSet.Trim().Length == 0 || assignedNumbers.Contains(team.Number!.Value)),
            _ => false
        };

        var unassigned = new List<NumberVariantRow>();
        foreach (var team in snapshot.RelayTeams.Where(t => t.Number is not null))
        {
            if (!settingsByGroup.TryGetValue(team.GroupId, out var s) || !IsRelayGroup(team.GroupId)
                || !TakesCoursesByNumber(team, s))
                continue;
            for (var leg = 1; leg <= Math.Max(1, s.LegCount ?? 1); leg++)
                if (!assigned.Contains((team.Number!.Value, leg)))
                    unassigned.Add(Row(team.GroupId, team.Number.Value, leg, string.Empty));
        }
        // A scatter handed out by number: the group's numbered runners whose number has no map.
        foreach (var (number, link) in runnerByNumber)
            if (link.GroupId is { } g && settingsByGroup.ContainsKey(g) && groupsWithRows.Contains(g)
                && !assignedNumbers.Contains(number))
                unassigned.Add(Row(g, number, 1, string.Empty));

        var groupOrder = snapshot.GroupDaySettings.ToDictionary(s => s.GroupId, s => s.Order);
        List<NumberVariantRow> Sorted(IEnumerable<NumberVariantRow> list) => list
            .OrderBy(r => r.GroupId is { } g && groupOrder.TryGetValue(g, out var o) ? o : int.MaxValue)
            .ThenBy(r => r.GroupName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Number)
            .ThenBy(r => r.Leg)
            .ToList();

        var groups = snapshot.GroupDaySettings
            .OrderBy(s => s.Order)
            .Select(s => new NumberVariantGroup(
                s.GroupId, GroupName(s.GroupId), variants.TryGetValue(s.GroupId, out var list) ? list : []))
            .ToList();
        return new NumberVariantsData(Sorted(rows), Sorted(unassigned), groups);
    }
}
