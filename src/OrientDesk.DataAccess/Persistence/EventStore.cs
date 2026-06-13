using Microsoft.EntityFrameworkCore;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

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

    public async Task<IReadOnlyList<Region>> GetRegionsAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.Regions
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task AddRegionAsync(string eventFolderPath, Region region, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        db.Regions.Add(region);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateRegionAsync(string eventFolderPath, Region region, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.Regions.FirstOrDefaultAsync(r => r.Id == region.Id, cancellationToken);
        if (existing is null)
            return;

        existing.Name = region.Name;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRegionAsync(string eventFolderPath, Guid regionId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.Regions.FirstOrDefaultAsync(r => r.Id == regionId, cancellationToken);
        if (existing is null)
            return;

        db.Regions.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearParticipantsRegionAsync(string eventFolderPath, Guid regionId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        await db.Participants
            .Where(p => p.RegionId == regionId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.RegionId, (Guid?)null), cancellationToken);
    }

    public async Task<IReadOnlyList<Club>> GetClubsAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.Clubs
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task AddClubAsync(string eventFolderPath, Club club, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        db.Clubs.Add(club);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateClubAsync(string eventFolderPath, Club club, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.Clubs.FirstOrDefaultAsync(c => c.Id == club.Id, cancellationToken);
        if (existing is null)
            return;

        existing.Name = club.Name;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteClubAsync(string eventFolderPath, Guid clubId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.Clubs.FirstOrDefaultAsync(c => c.Id == clubId, cancellationToken);
        if (existing is null)
            return;

        db.Clubs.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearParticipantsClubAsync(string eventFolderPath, Guid clubId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        await db.Participants
            .Where(p => p.ClubId == clubId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ClubId, (Guid?)null), cancellationToken);
    }

    public async Task<IReadOnlyList<Dussh>> GetDusshesAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.Dusshes
            .AsNoTracking()
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task AddDusshAsync(string eventFolderPath, Dussh dussh, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        db.Dusshes.Add(dussh);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateDusshAsync(string eventFolderPath, Dussh dussh, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.Dusshes.FirstOrDefaultAsync(d => d.Id == dussh.Id, cancellationToken);
        if (existing is null)
            return;

        existing.Name = dussh.Name;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteDusshAsync(string eventFolderPath, Guid dusshId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.Dusshes.FirstOrDefaultAsync(d => d.Id == dusshId, cancellationToken);
        if (existing is null)
            return;

        db.Dusshes.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearParticipantsDusshAsync(string eventFolderPath, Guid dusshId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        await db.Participants
            .Where(p => p.DusshId == dusshId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.DusshId, (Guid?)null), cancellationToken);
    }

    public async Task<IReadOnlyList<Participant>> GetParticipantsAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.Participants
            .AsNoTracking()
            .OrderBy(p => p.FullName)
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

        existing.FullName = participant.FullName;
        existing.Number = participant.Number;
        existing.Rank = participant.Rank;
        existing.Coach = participant.Coach;
        existing.BirthDate = participant.BirthDate;
        existing.RegionId = participant.RegionId;
        existing.ClubId = participant.ClubId;
        existing.DusshId = participant.DusshId;
        existing.Representative = participant.Representative;
        existing.FsouCode = participant.FsouCode;
        existing.IsFsouMember = participant.IsFsouMember;
        existing.Payment = participant.Payment;
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

    public async Task<int> DeleteAllParticipantsAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        // Drop the per-day links first (membership), then the participants themselves.
        await db.ParticipantDays.ExecuteDeleteAsync(cancellationToken);
        return await db.Participants.ExecuteDeleteAsync(cancellationToken);
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
        existing.StartTime = link.StartTime;
        existing.OutOfCompetition = link.OutOfCompetition;
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

    public async Task<IReadOnlyList<FinishReadout>> GetFinishReadoutsAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        // Order is a stable, unique-per-day sequence; we never sort by the DateTimeOffset read times
        // (SQLite can't ORDER BY a DateTimeOffset column).
        return await db.FinishReadouts
            .AsNoTracking()
            .Where(r => r.EventDayId == dayId)
            .OrderBy(r => r.Order)
            .ToListAsync(cancellationToken);
    }

    public async Task AddFinishReadoutsAsync(string eventFolderPath, IReadOnlyList<FinishReadout> readouts, CancellationToken cancellationToken = default)
    {
        if (readouts.Count == 0)
            return;
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        db.FinishReadouts.AddRange(readouts);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> DeleteFinishReadoutsForDayAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.FinishReadouts
            .Where(r => r.EventDayId == dayId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<ParticipantImportResult> ImportParticipantsBatchAsync(
        string eventFolderPath,
        UofParticipantData data,
        bool clearFirst,
        int daysCreated,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        await using var db = EventDbContextFactory.Create(eventFolderPath);

        progress?.Report(ImportProgress.Counted(ImportStage.Parsed, 0, data.Participants.Count));

        // 1. Organiser: fill it from the file when present (don't clobber an existing value with blank).
        if (!string.IsNullOrWhiteSpace(data.Organisation))
        {
            var info = await db.Competition.FirstOrDefaultAsync(cancellationToken);
            if (info is not null)
                info.Organisation = data.Organisation.Trim();
        }

        // 2. Optional wipe: clear the whole participant database so the file becomes the full roster.
        if (clearFirst)
        {
            await db.ParticipantDays.ExecuteDeleteAsync(cancellationToken);
            await db.Participants.ExecuteDeleteAsync(cancellationToken);
            progress?.Report(ImportProgress.Of(ImportStage.Cleared));
        }

        if (daysCreated > 0)
            progress?.Report(ImportProgress.Counted(ImportStage.DaysCreated, daysCreated, 0));

        // 3. Load everything we resolve against once, up front, and track it. New rows added to these
        //    sets below are written together by the single SaveChanges at the end.
        progress?.Report(ImportProgress.Of(ImportStage.ResolvingLookups));
        var days = await db.Days.ToListAsync(cancellationToken);
        var dayByNumber = days.ToDictionary(d => d.Number);
        var regions = await db.Regions.ToListAsync(cancellationToken);
        var clubs = await db.Clubs.ToListAsync(cancellationToken);
        var dusshes = await db.Dusshes.ToListAsync(cancellationToken);
        var groups = await db.Groups.ToListAsync(cancellationToken);

        Guid? ResolveRegion(string name) => ResolveLookup(name, regions, n => new Region { Name = n }, db.Regions);
        Guid? ResolveClub(string name) => ResolveLookup(name, clubs, n => new Club { Name = n }, db.Clubs);
        Guid? ResolveDussh(string name) => ResolveLookup(name, dusshes, n => new Dussh { Name = n }, db.Dusshes);

        Group? ResolveGroup(string name)
        {
            var trimmed = name.Trim();
            if (trimmed.Length == 0) return null;
            var existing = groups.FirstOrDefault(g => string.Equals(g.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new Group { Name = trimmed };
                db.Groups.Add(existing);
                groups.Add(existing);
            }
            return existing;
        }

        // Per-day group-attachment sets + running-order counters, seeded from existing rows so a
        // re-import continues the order rather than colliding.
        var existingSettings = await db.GroupDaySettings.ToListAsync(cancellationToken);
        var existingLinks = await db.ParticipantDays.ToListAsync(cancellationToken);
        var attachedByDay = new Dictionary<Guid, HashSet<Guid>>();
        var groupOrderByDay = new Dictionary<Guid, int>();
        var linkOrderByDay = new Dictionary<Guid, int>();
        foreach (var day in days)
        {
            var settings = existingSettings.Where(s => s.EventDayId == day.Id).ToList();
            attachedByDay[day.Id] = new HashSet<Guid>(settings.Select(s => s.GroupId));
            groupOrderByDay[day.Id] = settings.Count == 0 ? 0 : settings.Max(s => s.Order);
            var links = existingLinks.Where(l => l.EventDayId == day.Id).ToList();
            linkOrderByDay[day.Id] = links.Count == 0 ? 0 : links.Max(l => l.Order);
        }

        void EnsureGroupOnDay(Guid dayId, Guid groupId)
        {
            if (attachedByDay[dayId].Add(groupId))
            {
                db.GroupDaySettings.Add(new GroupDaySettings
                {
                    EventDayId = dayId,
                    GroupId = groupId,
                    Order = ++groupOrderByDay[dayId]
                });
            }
        }

        // Existing participants for FOU-code matching (keep mode); none after a clear.
        var byFsouCode = (clearFirst ? [] : await db.Participants.ToListAsync(cancellationToken))
            .Where(p => !string.IsNullOrWhiteSpace(p.FsouCode))
            .GroupBy(p => p.FsouCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var linksByParticipant = existingLinks
            .GroupBy(l => l.ParticipantId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var added = 0;
        var updated = 0;
        var processed = 0;
        var total = data.Participants.Count;

        foreach (var src in data.Participants)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var regionId = ResolveRegion(src.Region);
            var clubId = ResolveClub(src.Club);
            var dusshId = ResolveDussh(src.Dussh);

            var code = src.FsouCode.Trim();
            Participant? participant = null;
            if (code.Length > 0 && byFsouCode.TryGetValue(code, out var matched))
                participant = matched;

            if (participant is null)
            {
                participant = new Participant
                {
                    FullName = src.FullName,
                    Rank = src.Rank,
                    Coach = src.Coach,
                    BirthDate = src.BirthDate,
                    RegionId = regionId,
                    ClubId = clubId,
                    DusshId = dusshId,
                    Representative = src.Representative,
                    FsouCode = code,
                    IsFsouMember = src.IsFsouMember,
                    Payment = src.Payment
                };
                db.Participants.Add(participant);
                added++;
            }
            else
            {
                participant.FullName = src.FullName;
                participant.Rank = src.Rank;
                participant.Coach = src.Coach;
                participant.BirthDate = src.BirthDate;
                participant.RegionId = regionId;
                participant.ClubId = clubId;
                participant.DusshId = dusshId;
                participant.Representative = src.Representative;
                participant.IsFsouMember = src.IsFsouMember;
                participant.Payment = src.Payment;
                updated++;
            }

            var group = ResolveGroup(src.Group);
            var priorLinks = linksByParticipant.TryGetValue(participant.Id, out var l) ? l : [];

            foreach (var dayNumber in src.DayNumbers)
            {
                if (!dayByNumber.TryGetValue(dayNumber, out var day))
                    continue; // a number with no matching day (shouldn't happen — caller created them)

                if (group is not null)
                    EnsureGroupOnDay(day.Id, group.Id);

                var link = priorLinks.FirstOrDefault(x => x.EventDayId == day.Id);
                if (link is null)
                {
                    db.ParticipantDays.Add(new ParticipantDay
                    {
                        EventDayId = day.Id,
                        ParticipantId = participant.Id,
                        Order = ++linkOrderByDay[day.Id],
                        GroupId = group?.Id,
                        Chip = src.Chip
                    });
                }
                else
                {
                    link.GroupId = group?.Id;
                    link.Chip = src.Chip;
                }
            }

            processed++;
            // Tick the counter in place every so often (and on the last row) so the overlay updates
            // without one log line per participant.
            if (processed % 25 == 0 || processed == total)
                progress?.Report(ImportProgress.Counted(ImportStage.Participants, processed, total));
        }

        await db.SaveChangesAsync(cancellationToken);
        progress?.Report(ImportProgress.Of(ImportStage.Done));

        return new ParticipantImportResult(Added: added, Updated: updated, DaysCreated: daysCreated);
    }

    // Get-or-create against an in-memory tracked list + the DbSet (added rows are saved with the batch).
    private static Guid? ResolveLookup<T>(string name, List<T> cache, Func<string, T> create, DbSet<T> set)
        where T : class
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return null;
        var existing = cache.FirstOrDefault(e => string.Equals(NameOf(e), trimmed, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = create(trimmed);
            set.Add(existing);
            cache.Add(existing);
        }
        return IdOf(existing);
    }

    private static string NameOf(object e) => e switch
    {
        Region r => r.Name,
        Club c => c.Name,
        Dussh d => d.Name,
        _ => string.Empty
    };

    private static Guid IdOf(object e) => e switch
    {
        Region r => r.Id,
        Club c => c.Id,
        Dussh d => d.Id,
        _ => Guid.Empty
    };
}
