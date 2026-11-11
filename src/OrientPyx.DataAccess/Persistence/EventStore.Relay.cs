using Microsoft.EntityFrameworkCore;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.DataAccess.Persistence;

/// <summary>Relay teams (Р-20…Р-22): teams of a day and the runners on their legs.</summary>
public sealed partial class EventStore
{
    public async Task<IReadOnlyList<RelayTeam>> GetRelayTeamsAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return OrderTeams(await db.RelayTeams.AsNoTracking()
            .Where(t => t.EventDayId == dayId)
            .ToListAsync(cancellationToken));
    }

    // Numbered teams by number, then the teams without a number in the order they were made (an import's file order);
    // in memory, because SQLite sorts NULL first and cannot order by DateTimeOffset.
    private static List<RelayTeam> OrderTeams(List<RelayTeam> teams) => teams
        .OrderBy(t => t.Number is null)
        .ThenBy(t => t.Number)
        .ThenBy(t => t.CreatedAt)
        .ToList();

    public async Task AddRelayTeamsAsync(string eventFolderPath, IReadOnlyList<RelayTeam> teams, CancellationToken cancellationToken = default)
    {
        if (teams.Count == 0)
            return;
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        db.RelayTeams.AddRange(teams);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateRelayTeamAsync(string eventFolderPath, RelayTeam team, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        var existing = await db.RelayTeams.FirstOrDefaultAsync(t => t.Id == team.Id, cancellationToken);
        if (existing is null)
            return;

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        existing.Number = team.Number;
        existing.Name = team.Name;
        existing.ClubId = team.ClubId;
        existing.RegionId = team.RegionId;
        existing.OutOfCompetition = team.OutOfCompetition;
        existing.StatusOverride = team.StatusOverride;
        existing.CourseSet = team.CourseSet;
        if (existing.GroupId != team.GroupId)
        {
            existing.GroupId = team.GroupId;
            // Р-22: the runners' group on the day follows the team's.
            var members = await db.ParticipantDays.Where(p => p.RelayTeamId == team.Id).ToListAsync(cancellationToken);
            foreach (var member in members)
                member.GroupId = team.GroupId;
        }
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<bool> SetRelayTeamNumbersAsync(
        string eventFolderPath, Guid dayId, IReadOnlyList<RelayTeamNumberChange> numbers, CancellationToken cancellationToken = default)
    {
        if (numbers.Count == 0)
            return true;
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        var teams = await db.RelayTeams.Where(t => t.EventDayId == dayId).ToListAsync(cancellationToken);
        var byId = teams.ToDictionary(t => t.Id);
        var wanted = new Dictionary<Guid, int?>();
        foreach (var change in numbers)
            if (byId.ContainsKey(change.TeamId))
                wanted[change.TeamId] = change.Number;
        if (wanted.Count == 0)
            return true;

        var after = teams.Select(t => wanted.TryGetValue(t.Id, out var n) ? n : t.Number).OfType<int>().ToList();
        if (after.Count != after.Distinct().Count())
            return false;

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        // Every touched team gives its number up first, so a number can pass to another team past the unique index.
        foreach (var id in wanted.Keys)
            byId[id].Number = null;
        await db.SaveChangesAsync(cancellationToken);
        foreach (var (id, number) in wanted)
            byId[id].Number = number;
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task DeleteRelayTeamAsync(string eventFolderPath, Guid teamId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await ReleaseLegsAsync(db, p => p.RelayTeamId == teamId, cancellationToken);
        await db.RelayTeams.Where(t => t.Id == teamId).ExecuteDeleteAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LegCourseAssignment>> GetLegCourseAssignmentsAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.LegCourseAssignments.AsNoTracking()
            .Where(a => a.EventDayId == dayId)
            .OrderBy(a => a.TeamNumber).ThenBy(a => a.Leg)
            .ToListAsync(cancellationToken);
    }

    public async Task UpsertLegCourseAssignmentsAsync(string eventFolderPath, Guid dayId, IReadOnlyList<LegCourseAssignment> assignments, CancellationToken cancellationToken = default)
    {
        if (assignments.Count == 0)
            return;
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var existing = (await db.LegCourseAssignments.Where(a => a.EventDayId == dayId).ToListAsync(cancellationToken))
            .ToDictionary(a => (a.TeamNumber, a.Leg));
        ApplyLegCourseAssignments(db, dayId, existing, assignments);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task ReplaceGroupLegCourseAssignmentsAsync(
        string eventFolderPath, Guid dayId, Guid groupId, IReadOnlyList<LegCourseAssignment> assignments,
        CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await db.LegCourseAssignments.Where(a => a.EventDayId == dayId).ToListAsync(cancellationToken);
        var existing = new Dictionary<(int, int), LegCourseAssignment>();
        foreach (var row in rows)
        {
            if (row.GroupId == groupId)
                db.LegCourseAssignments.Remove(row);
            else
                existing[(row.TeamNumber, row.Leg)] = row;
        }
        // Rows deleted above must be gone before a new row reuses their (number, leg) key — the index is unique.
        await db.SaveChangesAsync(cancellationToken);
        ApplyLegCourseAssignments(db, dayId, existing, assignments.Select(a => new LegCourseAssignment
        {
            EventDayId = dayId, GroupId = groupId, TeamNumber = a.TeamNumber, Leg = a.Leg, CourseKey = a.CourseKey
        }).ToList());
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    // Writes each assignment over the day's rows by (number, leg): an existing row takes the new course (and group,
    // when given), a blank course removes it, a new key adds a row.
    private static void ApplyLegCourseAssignments(
        EventDbContext db, Guid dayId, Dictionary<(int, int), LegCourseAssignment> existing,
        IReadOnlyList<LegCourseAssignment> assignments)
    {
        foreach (var assignment in assignments)
        {
            var key = (assignment.TeamNumber, assignment.Leg);
            var code = assignment.CourseKey.Trim();
            if (existing.TryGetValue(key, out var row))
            {
                if (code.Length == 0)
                {
                    db.LegCourseAssignments.Remove(row);
                    existing.Remove(key);
                }
                else
                {
                    row.CourseKey = code;
                    row.GroupId = assignment.GroupId ?? row.GroupId;
                }
            }
            else if (code.Length > 0 && assignment.TeamNumber > 0 && assignment.Leg >= 1)
            {
                row = new LegCourseAssignment
                {
                    EventDayId = dayId, GroupId = assignment.GroupId, TeamNumber = assignment.TeamNumber, Leg = assignment.Leg,
                    CourseKey = code
                };
                db.LegCourseAssignments.Add(row);
                existing[key] = row;
            }
        }
    }

    public async Task DeleteRelayTeamsForGroupDayAsync(string eventFolderPath, Guid dayId, Guid groupId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var ids = await db.RelayTeams.Where(t => t.EventDayId == dayId && t.GroupId == groupId)
            .Select(t => t.Id).ToListAsync(cancellationToken);
        if (ids.Count > 0)
        {
            await ReleaseLegsAsync(db, p => p.RelayTeamId != null && ids.Contains(p.RelayTeamId.Value), cancellationToken);
            await db.RelayTeams.Where(t => ids.Contains(t.Id)).ExecuteDeleteAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<bool> AssignRelayLegAsync(
        string eventFolderPath, Guid teamId, int leg, Guid? linkId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        var team = await db.RelayTeams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
        if (team is null)
            return false;

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        // Free the leg first (one runner per leg, Р-21), then the runner's previous leg anywhere on the day —
        // separate saves so the unique (team, leg) index never sees a transient duplicate.
        await ReleaseLegsAsync(db, p => p.RelayTeamId == teamId && p.Leg == leg, cancellationToken);
        if (linkId is { } id)
        {
            var link = await db.ParticipantDays.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
            if (link is null || link.EventDayId != team.EventDayId)
                return false;
            link.RelayTeamId = null;
            link.Leg = null;
            await db.SaveChangesAsync(cancellationToken);

            link.RelayTeamId = teamId;
            link.Leg = leg;
            link.GroupId = team.GroupId;   // Р-22
            await db.SaveChangesAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ReleaseRelayRunnerAsync(string eventFolderPath, Guid linkId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        var link = await db.ParticipantDays.FirstOrDefaultAsync(p => p.Id == linkId, cancellationToken);
        if (link is null)
            return false;
        if (link.RelayTeamId is null && link.Leg is null)
            return true;
        link.RelayTeamId = null;
        link.Leg = null;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task UpdateRelaySettingsAsync(
        string eventFolderPath, Guid settingsId, int? legCount, TimeSpan? massStartTime, string restartTimesJson,
        RelayRestartRanking restartRanking, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        var existing = await db.GroupDaySettings.FirstOrDefaultAsync(s => s.Id == settingsId, cancellationToken);
        if (existing is null)
            return;
        existing.LegCount = legCount;
        existing.MassStartTime = massStartTime;
        existing.RestartTimesJson = restartTimesJson;
        existing.RestartRanking = restartRanking;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateRelayCourseSettingsAsync(
        string eventFolderPath, Guid settingsId, RelayCourseMode mode, string teamCourseSet,
        CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        var existing = await db.GroupDaySettings.FirstOrDefaultAsync(s => s.Id == settingsId, cancellationToken);
        if (existing is null)
            return;
        existing.CourseAssignmentMode = mode;
        existing.TeamCourseSet = teamCourseSet;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task ReleaseLegsAsync(
        EventDbContext db, System.Linq.Expressions.Expression<Func<ParticipantDay, bool>> which, CancellationToken cancellationToken)
    {
        var links = await db.ParticipantDays.Where(which).ToListAsync(cancellationToken);
        foreach (var link in links)
        {
            link.RelayTeamId = null;
            link.Leg = null;
        }
        if (links.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
    }
}
