using System.Globalization;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

// «Варіанти за номерами»: the day's number → variation table (the maps to hand out).
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

        // Each group's variants: code → control order.
        var variants = snapshot.CourseVariants
            .GroupBy(v => v.GroupId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(v => v.Code.Trim(), TextKey.Comparer).ToDictionary(v => v.Key, v => v.First().CourseOrder, TextKey.Comparer));

        var participants = snapshot.Participants.ToDictionary(p => p.Id);
        var teamsByNumber = snapshot.RelayTeams
            .Where(t => t.Number is not null)
            .GroupBy(t => t.Number!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var legRunner = snapshot.Links
            .Where(l => l.RelayTeamId is not null && l.Leg is not null)
            .GroupBy(l => (l.RelayTeamId!.Value, l.Leg!.Value))
            .ToDictionary(g => g.Key, g => g.First().ParticipantId);
        // A scatter runner by bib: a day member (not on a relay team) whose number reads as that number.
        var runnerByNumber = new Dictionary<int, ParticipantDay>();
        foreach (var link in snapshot.Links.Where(l => l.RelayTeamId is null))
            if (participants.TryGetValue(link.ParticipantId, out var p)
                && int.TryParse(p.Number.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
                runnerByNumber.TryAdd(n, link);

        string NameOf(Guid participantId) =>
            participants.TryGetValue(participantId, out var p) ? p.FullName.Trim() : string.Empty;

        // The group of a row: the one it names (when on the day), else the team holding the number, else the runner's.
        Guid? GroupOf(LegCourseAssignment a) =>
            a.GroupId is { } g && settingsByGroup.ContainsKey(g) ? g
            : teamsByNumber.TryGetValue(a.TeamNumber, out var team) ? team.GroupId
            : runnerByNumber.TryGetValue(a.TeamNumber, out var link) ? link.GroupId
            : null;

        var legsOfNumber = snapshot.LegCourseAssignments
            .GroupBy(a => a.TeamNumber)
            .ToDictionary(g => g.Key, g => g.Count());

        NumberVariantRow Row(Guid? groupId, int number, int leg, string course)
        {
            var relay = groupId is { } g && IsRelayGroup(g);
            var team = relay && teamsByNumber.TryGetValue(number, out var t) && t.GroupId == groupId ? t : null;
            var runner = team is not null
                ? legRunner.TryGetValue((team.Id, leg), out var pid) ? NameOf(pid) : string.Empty
                : !relay && runnerByNumber.TryGetValue(number, out var link) ? NameOf(link.ParticipantId)
                : string.Empty;
            // A relay leg or a number with several legs prints as «101.2»; a scatter runner's single map as «101».
            var bib = relay || leg > 1 || legsOfNumber.GetValueOrDefault(number) > 1
                ? RelayBib.Format(number, leg)
                : number.ToString(CultureInfo.InvariantCulture);
            var code = course.Trim();
            var order = groupId is { } gid && variants.TryGetValue(gid, out var byCode) && byCode.TryGetValue(code, out var o)
                ? o
                : string.Empty;
            return new NumberVariantRow(
                groupId,
                groupId is { } id && groupNames.TryGetValue(id, out var name) ? name : string.Empty,
                number, leg, bib, code, order, team?.Name.Trim() ?? string.Empty, runner,
                Missing: code.Length == 0,
                UnknownCourse: code.Length > 0 && order.Length == 0);
        }

        var rows = new List<NumberVariantRow>();
        var seen = new HashSet<(int, int)>();
        foreach (var a in snapshot.LegCourseAssignments)
            if (a.CourseKey.Trim().Length > 0 && seen.Add((a.TeamNumber, a.Leg)))
                rows.Add(Row(GroupOf(a), a.TeamNumber, a.Leg, a.CourseKey));

        // «За номером етапу»: every leg of a numbered team must have a variation — list the ones without.
        foreach (var team in snapshot.RelayTeams.Where(t => t.Number is not null))
        {
            if (!settingsByGroup.TryGetValue(team.GroupId, out var s) || s.CourseAssignmentMode != RelayCourseMode.ByBib
                || !IsRelayGroup(team.GroupId))
                continue;
            for (var leg = 1; leg <= Math.Max(1, s.LegCount ?? 1); leg++)
                if (seen.Add((team.Number!.Value, leg)))
                    rows.Add(Row(team.GroupId, team.Number.Value, leg, string.Empty));
        }

        var groupOrder = snapshot.GroupDaySettings.ToDictionary(s => s.GroupId, s => s.Order);
        return new NumberVariantsData(rows
            .OrderBy(r => r.GroupId is { } g && groupOrder.TryGetValue(g, out var o) ? o : int.MaxValue)
            .ThenBy(r => r.GroupName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Number)
            .ThenBy(r => r.Leg)
            .ToList());
    }
}
