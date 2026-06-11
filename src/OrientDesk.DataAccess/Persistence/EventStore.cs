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

    public async Task UpdateDayNumberAsync(string eventFolderPath, Guid dayId, int newNumber, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.Days.FirstOrDefaultAsync(d => d.Id == dayId, cancellationToken);
        if (existing is null)
            return;

        existing.Number = newNumber;
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

    public async Task<IReadOnlyList<RentalChip>> GetRentalChipsAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.RentalChips
            .AsNoTracking()
            .OrderBy(c => c.Number)
            .ToListAsync(cancellationToken);
    }

    public async Task AddRentalChipAsync(string eventFolderPath, RentalChip chip, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        db.RentalChips.Add(chip);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddRentalChipsAsync(string eventFolderPath, IReadOnlyList<RentalChip> chips, CancellationToken cancellationToken = default)
    {
        if (chips.Count == 0)
            return;

        await using var db = EventDbContextFactory.Create(eventFolderPath);
        db.RentalChips.AddRange(chips);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateRentalChipAsync(string eventFolderPath, RentalChip chip, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.RentalChips.FirstOrDefaultAsync(c => c.Id == chip.Id, cancellationToken);
        if (existing is null)
            return;

        existing.Number = chip.Number;
        existing.Note = chip.Note;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRentalChipAsync(string eventFolderPath, Guid chipId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.RentalChips.FirstOrDefaultAsync(c => c.Id == chipId, cancellationToken);
        if (existing is null)
            return;

        db.RentalChips.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> DeleteAllRentalChipsAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.RentalChips.ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Participant>> GetParticipantsAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.Participants
            .AsNoTracking()
            .OrderBy(p => p.Surname)
            .ThenBy(p => p.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task AddParticipantAsync(string eventFolderPath, Participant participant, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        db.Participants.Add(participant);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateParticipantAsync(string eventFolderPath, Participant participant, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.Participants.FirstOrDefaultAsync(p => p.Id == participant.Id, cancellationToken);
        if (existing is null)
            return;

        existing.Surname = participant.Surname;
        existing.Name = participant.Name;
        existing.Number = participant.Number;
        existing.Rank = participant.Rank;
        existing.Coach = participant.Coach;
        existing.BirthDate = participant.BirthDate;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteParticipantAsync(string eventFolderPath, Guid participantId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.Participants.FirstOrDefaultAsync(p => p.Id == participantId, cancellationToken);
        if (existing is null)
            return;

        db.Participants.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ParticipantDay>> GetParticipantDaysAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        // Order alone is a stable, unique-per-day sort key (each add is max+1). We avoid a
        // CreatedAt tie-break because SQLite can't ORDER BY a DateTimeOffset column.
        return await db.ParticipantDays
            .AsNoTracking()
            .Where(p => p.EventDayId == dayId)
            .OrderBy(p => p.Order)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ParticipantDay>> GetAllParticipantDaysAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.ParticipantDays
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CountParticipantDaysForParticipantAsync(string eventFolderPath, Guid participantId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.ParticipantDays.CountAsync(p => p.ParticipantId == participantId, cancellationToken);
    }

    public async Task AddParticipantDayAsync(string eventFolderPath, ParticipantDay link, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        db.ParticipantDays.Add(link);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateParticipantDayAsync(string eventFolderPath, ParticipantDay link, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.ParticipantDays.FirstOrDefaultAsync(p => p.Id == link.Id, cancellationToken);
        if (existing is null)
            return;

        existing.Order = link.Order;
        existing.GroupId = link.GroupId;
        existing.Chip = link.Chip;
        existing.Team = link.Team;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteParticipantDayAsync(string eventFolderPath, Guid linkId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.ParticipantDays.FirstOrDefaultAsync(p => p.Id == linkId, cancellationToken);
        if (existing is null)
            return;

        db.ParticipantDays.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }
}
