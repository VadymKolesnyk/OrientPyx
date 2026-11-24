using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services.Relay;

/// <summary>
/// The relay places of a day's links (team + leg, as stored, how many of the team's legs are filled, and the leg
/// number's course issue), shared by the participant tables' relay cells and the day readiness check.
/// </summary>
internal static class RelayPlacements
{
    /// <summary>Whether the day runs a relay at all — a relay group (the day default or an override) or a team.</summary>
    public static bool IsRelayDay(
        EventDay day, IReadOnlyCollection<RelayTeam> teams, IEnumerable<GroupDaySettings> settings,
        Func<DisciplineId, bool> isRelay) =>
        teams.Count > 0
        || isRelay(day.DefaultDiscipline)
        || settings.Any(s => isRelay(s.DisciplineOverride ?? day.DefaultDiscipline));

    /// <summary>
    /// Each relay runner's place, keyed by link id (the day's links on a team only). <paramref name="legCourses"/> null
    /// when no group of the day takes its courses «за номером етапу».
    /// </summary>
    public static Dictionary<Guid, RelayPlacement> Build(
        EventDay day,
        IEnumerable<ParticipantDay> links,
        IReadOnlyCollection<RelayTeam> teams,
        IEnumerable<GroupDaySettings> settings,
        LegCourseCheck? legCourses)
    {
        var byLink = new Dictionary<Guid, RelayPlacement>();
        if (teams.Count == 0)
            return byLink;
        var teamsById = teams.ToDictionary(t => t.Id);
        var legCounts = LegCounts(settings);
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
                FilledLegs = legsByTeam.TryGetValue(team.Id, out var legs) ? legs.Count(l => l >= 1 && l <= legCount) : 0,
                CourseIssue = link.Leg is { } leg && leg >= 1 && leg <= legCount ? legCourses?.IssueOf(team, leg) : null
            };
        }
        return byLink;
    }

    /// <summary>Each group's leg count on the day (at least 1).</summary>
    public static Dictionary<Guid, int> LegCounts(IEnumerable<GroupDaySettings> settings) =>
        settings.GroupBy(s => s.GroupId).ToDictionary(g => g.Key, g => Math.Max(1, g.First().LegCount ?? 1));
}
