using Microsoft.EntityFrameworkCore;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.DataAccess.Persistence;

/// <summary>Relay teams (Р-20…Р-22): teams of a day and the runners on their legs.</summary>
public sealed partial class EventStore
{
    public async Task<IReadOnlyList<RelayTeam>> GetRelayTeamsAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.RelayTeams.AsNoTracking()
            .Where(t => t.EventDayId == dayId)
            .OrderBy(t => t.Number)
            .ToListAsync(cancellationToken);
    }

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
                    row.CourseKey = code;
            }
            else if (code.Length > 0 && assignment.TeamNumber > 0 && assignment.Leg >= 1)
            {
                row = new LegCourseAssignment { EventDayId = dayId, TeamNumber = assignment.TeamNumber, Leg = assignment.Leg, CourseKey = code };
                db.LegCourseAssignments.Add(row);
                existing[key] = row;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
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
        string eventFolderPath, Guid settingsId, RelayCourseMode mode, string legCoursesJson, string teamCourseSet,
        CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        var existing = await db.GroupDaySettings.FirstOrDefaultAsync(s => s.Id == settingsId, cancellationToken);
        if (existing is null)
            return;
        existing.CourseAssignmentMode = mode;
        existing.LegCoursesJson = legCoursesJson;
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
