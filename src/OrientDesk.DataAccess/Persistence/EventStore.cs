using Microsoft.EntityFrameworkCore;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;

namespace OrientDesk.DataAccess.Persistence;

/// <summary>EF Core implementation of <see cref="IEventStore"/> over per-competition databases.</summary>
public sealed class EventStore : IEventStore
{
    public async Task EnsureCreatedAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(eventFolderPath);
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        // Applies all pending migrations, creating the database on first open. New migrations
        // flow to every competition automatically the next time its database is opened.
        await db.Database.MigrateAsync(cancellationToken);
    }

    public async Task<CompetitionInfo?> GetCompetitionInfoAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.Competition.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
    }

    public async Task SaveCompetitionInfoAsync(string eventFolderPath, CompetitionInfo info, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.Competition.FirstOrDefaultAsync(cancellationToken);
        if (existing is null)
        {
            db.Competition.Add(info);
        }
        else
        {
            existing.Name = info.Name;
            existing.Identifier = info.Identifier;
            existing.Venue = info.Venue;
            existing.Organisation = info.Organisation;
            existing.StartDate = info.StartDate;
            existing.EndDate = info.EndDate;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EventDay>> GetDaysAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.Days
            .AsNoTracking()
            .OrderBy(d => d.Number)
            .ToListAsync(cancellationToken);
    }

    public async Task AddDayAsync(string eventFolderPath, EventDay day, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        db.Days.Add(day);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateDayAsync(string eventFolderPath, EventDay day, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.Days.FirstOrDefaultAsync(d => d.Id == day.Id, cancellationToken);
        if (existing is null)
            return;

        existing.Date = day.Date;
        existing.Venue = day.Venue;
        existing.DefaultDiscipline = day.DefaultDiscipline;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteDayAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.Days.FirstOrDefaultAsync(d => d.Id == dayId, cancellationToken);
        if (existing is null)
            return;

        db.Days.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ControlPoint>> GetControlPointsAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        // Order alone is a stable, unique-per-day sort key (each add is max+1). We avoid a
        // CreatedAt tie-break because SQLite can't ORDER BY a DateTimeOffset column.
        return await db.ControlPoints
            .AsNoTracking()
            .Where(cp => cp.EventDayId == dayId)
            .OrderBy(cp => cp.Order)
            .ToListAsync(cancellationToken);
    }

    public async Task AddControlPointAsync(string eventFolderPath, ControlPoint point, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        db.ControlPoints.Add(point);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddControlPointsAsync(string eventFolderPath, IReadOnlyList<ControlPoint> points, CancellationToken cancellationToken = default)
    {
        if (points.Count == 0)
            return;

        await using var db = EventDbContextFactory.Create(eventFolderPath);
        db.ControlPoints.AddRange(points);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReplaceControlPointsAsync(string eventFolderPath, Guid dayId, IReadOnlyList<ControlPoint> points, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.ControlPoints
            .Where(cp => cp.EventDayId == dayId)
            .ToListAsync(cancellationToken);
        db.ControlPoints.RemoveRange(existing);

        if (points.Count > 0)
            db.ControlPoints.AddRange(points);

        // One SaveChanges so a failure leaves the day's points untouched rather than half-replaced.
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateControlPointAsync(string eventFolderPath, ControlPoint point, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.ControlPoints.FirstOrDefaultAsync(cp => cp.Id == point.Id, cancellationToken);
        if (existing is null)
            return;

        existing.Code = point.Code;
        existing.Latitude = point.Latitude;
        existing.Longitude = point.Longitude;
        existing.Type = point.Type;
        existing.Points = point.Points;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteControlPointAsync(string eventFolderPath, Guid pointId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.ControlPoints.FirstOrDefaultAsync(cp => cp.Id == pointId, cancellationToken);
        if (existing is null)
            return;

        db.ControlPoints.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Group>> GetGroupsAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.Groups
            .AsNoTracking()
            .OrderBy(g => g.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task AddGroupAsync(string eventFolderPath, Group group, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        db.Groups.Add(group);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateGroupAsync(string eventFolderPath, Group group, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.Groups.FirstOrDefaultAsync(g => g.Id == group.Id, cancellationToken);
        if (existing is null)
            return;

        existing.Name = group.Name;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteGroupAsync(string eventFolderPath, Guid groupId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (existing is null)
            return;

        db.Groups.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GroupDaySettings>> GetGroupDaySettingsAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        // Order alone is a stable, unique-per-day sort key (each add is max+1). We avoid a
        // CreatedAt tie-break because SQLite can't ORDER BY a DateTimeOffset column.
        return await db.GroupDaySettings
            .AsNoTracking()
            .Where(s => s.EventDayId == dayId)
            .OrderBy(s => s.Order)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CountGroupDaySettingsForGroupAsync(string eventFolderPath, Guid groupId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.GroupDaySettings.CountAsync(s => s.GroupId == groupId, cancellationToken);
    }

    public async Task AddGroupDaySettingsAsync(string eventFolderPath, GroupDaySettings settings, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        db.GroupDaySettings.Add(settings);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddGroupDaySettingsRangeAsync(string eventFolderPath, IReadOnlyList<GroupDaySettings> settings, CancellationToken cancellationToken = default)
    {
        if (settings.Count == 0)
            return;

        await using var db = EventDbContextFactory.Create(eventFolderPath);
        db.GroupDaySettings.AddRange(settings);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateGroupDaySettingsAsync(string eventFolderPath, GroupDaySettings settings, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.GroupDaySettings.FirstOrDefaultAsync(s => s.Id == settings.Id, cancellationToken);
        if (existing is null)
            return;

        existing.CourseOrder = settings.CourseOrder;
        existing.DistanceKm = settings.DistanceKm;
        existing.DisciplineOverride = settings.DisciplineOverride;
        existing.TimeLimitSeconds = settings.TimeLimitSeconds;
        existing.RequiredControlCount = settings.RequiredControlCount;
        existing.PenaltyPerMinute = settings.PenaltyPerMinute;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteGroupDaySettingsAsync(string eventFolderPath, Guid settingsId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.GroupDaySettings.FirstOrDefaultAsync(s => s.Id == settingsId, cancellationToken);
        if (existing is null)
            return;

        db.GroupDaySettings.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }
}
