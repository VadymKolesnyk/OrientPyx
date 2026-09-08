using Microsoft.EntityFrameworkCore;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.DataAccess.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IEventStore"/> over per-competition databases.
/// Ordered reads sort by <c>Order</c> alone — a stable, unique-per-day key (each add is max+1);
/// there is deliberately no CreatedAt tie-break, since SQLite cannot ORDER BY a DateTimeOffset column.
/// </summary>
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

    public async Task CheckpointAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        // No database file yet → nothing to checkpoint (and opening one would create an empty DB).
        var dbPath = Path.Combine(eventFolderPath, AppDatabasePaths.EventDatabaseFileName);
        if (!File.Exists(dbPath))
            return;

        await using var db = EventDbContextFactory.Create(eventFolderPath);
        // TRUNCATE checkpoints all committed frames into event.db and then shrinks the -wal file to zero,
        // so the exported event.db holds every committed change on its own. Other open connections in this
        // process may write new WAL frames afterwards, but the zip loop still archives any -wal/-shm files,
        // so nothing is lost either way — this just makes the common case a self-contained event.db.
        await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
    }

    public async Task ReleaseAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await CheckpointAsync(eventFolderPath, cancellationToken);

        // Microsoft.Data.Sqlite pools connections per connection string, so a disposed DbContext still
        // leaves the file open. Clearing the pools drops those handles; the next store call simply
        // opens a fresh connection. This is process-wide (every event DB), which is harmless — the
        // pool exists only to save re-open cost.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
            existing.IsHidden = info.IsHidden;
            existing.RaisedFeeEnabled = info.RaisedFeeEnabled;
            existing.RaisedFeeAmount = info.RaisedFeeAmount;
            existing.ChipRentalPricePerDay = info.ChipRentalPricePerDay;
            existing.CourseSetter = info.CourseSetter;
            existing.CourseSetterCategory = info.CourseSetterCategory;
            existing.ChiefJudge = info.ChiefJudge;
            existing.ChiefJudgeCategory = info.ChiefJudgeCategory;
            existing.ChiefSecretary = info.ChiefSecretary;
            existing.ChiefSecretaryCategory = info.ChiefSecretaryCategory;
            existing.Jury = info.Jury;
            existing.DefaultPointsRuleId = info.DefaultPointsRuleId;
            existing.RosterEnabled = info.RosterEnabled;
            // PaymentPerDay is deliberately NOT copied here: switching that mode also migrates every
            // participant's payment value, so it has its own writer (SetPaymentPerDayAsync). A page that
            // saves a stale CompetitionInfo must not silently flip the mode back.
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetHiddenAsync(string eventFolderPath, bool hidden, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.Competition.FirstOrDefaultAsync(cancellationToken);
        if (existing is null)
            return;

        existing.IsHidden = hidden;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetRosterEnabledAsync(string eventFolderPath, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.Competition.FirstOrDefaultAsync(cancellationToken);
        if (existing is null)
            return;

        existing.RosterEnabled = enabled;
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
        existing.IsLocked = day.IsLocked;
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
        existing.IsDisabled = point.IsDisabled;
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

    public async Task<int> SetControlPointsDisabledAsync(
        string eventFolderPath, Guid dayId, IReadOnlyCollection<Guid> disabledPointIds,
        CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var disabled = disabledPointIds as ISet<Guid> ?? disabledPointIds.ToHashSet();
        var points = await db.ControlPoints
            .Where(cp => cp.EventDayId == dayId)
            .ToListAsync(cancellationToken);

        var changed = 0;
        foreach (var cp in points)
        {
            var shouldDisable = disabled.Contains(cp.Id);
            if (cp.IsDisabled == shouldDisable)
                continue;
            cp.IsDisabled = shouldDisable;
            changed++;
        }

        if (changed > 0)
            await db.SaveChangesAsync(cancellationToken);
        return changed;
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

    public async Task UpdateGroupEntryFeeAsync(string eventFolderPath, Guid groupId, decimal? entryFee, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (existing is null)
            return;

        existing.EntryFee = entryFee;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateGroupAgeWindowAsync(string eventFolderPath, Guid groupId, int? minBirthYear, int? maxBirthYear, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (existing is null)
            return;

        existing.MinBirthYear = minBirthYear;
        existing.MaxBirthYear = maxBirthYear;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GroupDaySettings>> GetGroupDaySettingsAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
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
        existing.CourseSetter = settings.CourseSetter;
        existing.CourseSetterCategory = settings.CourseSetterCategory;
        existing.PointsRuleId = settings.PointsRuleId;
        existing.RankLevel = settings.RankLevel;
        existing.MasterCount = settings.MasterCount;
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

    public async Task<IReadOnlyList<ScatterVariant>> GetScatterVariantsAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.ScatterVariants
            .AsNoTracking()
            .Where(v => v.EventDayId == dayId)
            .OrderBy(v => v.Order)
            .ToListAsync(cancellationToken);
    }

    public async Task ReplaceScatterVariantsForGroupAsync(string eventFolderPath, Guid dayId, Guid groupId, IReadOnlyList<ScatterVariant> variants, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        // Clear the group's existing variants for the day, then insert the new set — one transaction so a
        // re-import never leaves a mix of old and new orders. An empty list simply clears them.
        var existing = await db.ScatterVariants
            .Where(v => v.EventDayId == dayId && v.GroupId == groupId)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
            db.ScatterVariants.RemoveRange(existing);

        if (variants.Count > 0)
            db.ScatterVariants.AddRange(variants);

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
        existing.Note = participant.Note;
        existing.Team = participant.Team;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteParticipantAsync(string eventFolderPath, Guid participantId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.Participants.FirstOrDefaultAsync(p => p.Id == participantId, cancellationToken);
        if (existing is null)
            return;

        // ParticipantId is a foreign key by convention only (no navigation / DB cascade), so removing
        // the participant does NOT clean up its per-day links or discount links — they would be left
        // orphaned. Orphaned ParticipantDay rows are invisible to the roster/day-grid (which join to an
        // existing participant) but the dashboard counts raw links, so they inflate «Учасників на дні»
        // and show up as phantom «На дистанції». Delete them here so the roster delete is a full cascade,
        // matching the day-grid path (RemoveParticipantFromDayAsync) that removes the link before the person.
        await db.ParticipantDays
            .Where(p => p.ParticipantId == participantId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.ParticipantDiscounts
            .Where(p => p.ParticipantId == participantId)
            .ExecuteDeleteAsync(cancellationToken);

        db.Participants.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> DeleteAllParticipantsAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        // Drop the per-day links and discount links first, then the participants themselves.
        await db.ParticipantDays.ExecuteDeleteAsync(cancellationToken);
        await db.ParticipantDiscounts.ExecuteDeleteAsync(cancellationToken);
        return await db.Participants.ExecuteDeleteAsync(cancellationToken);
    }

    public async Task SetParticipantPaysRaisedFeeAsync(string eventFolderPath, Guid participantId, bool paysRaisedFee, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.Participants.FirstOrDefaultAsync(p => p.Id == participantId, cancellationToken);
        if (existing is null)
            return;

        existing.PaysRaisedFee = paysRaisedFee;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ParticipantDiscount>> GetParticipantDiscountsAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.ParticipantDiscounts
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task SetParticipantDiscountAsync(string eventFolderPath, Guid participantId, Guid discountId, bool on, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.ParticipantDiscounts
            .FirstOrDefaultAsync(p => p.ParticipantId == participantId && p.DiscountId == discountId, cancellationToken);

        if (on)
        {
            if (existing is null)
                db.ParticipantDiscounts.Add(new ParticipantDiscount { ParticipantId = participantId, DiscountId = discountId });
        }
        else if (existing is not null)
        {
            db.ParticipantDiscounts.Remove(existing);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ParticipantDay>> GetParticipantDaysAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.ParticipantDays
            .AsNoTracking()
            .Where(p => p.EventDayId == dayId)
            .OrderBy(p => p.Order)
            .ToListAsync(cancellationToken);
    }

    public async Task<Guid> SetParticipantDayGroupAsync(
        string eventFolderPath, Guid participantId, Guid dayId, Guid? groupId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        // Look-up and insert share one transaction so a concurrent caller can't slip between them and add
        // a second link for the same (day, participant). The unique index is the backstop; this keeps the
        // common path from ever hitting it.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var existing = await db.ParticipantDays
            .FirstOrDefaultAsync(p => p.EventDayId == dayId && p.ParticipantId == participantId, cancellationToken);

        if (existing is null)
        {
            // Joining the day: the link carries the chosen group; order continues the day's grid.
            var maxOrder = await db.ParticipantDays
                .Where(p => p.EventDayId == dayId)
                .Select(p => (int?)p.Order)
                .MaxAsync(cancellationToken) ?? 0;

            var link = new ParticipantDay
            {
                EventDayId = dayId,
                ParticipantId = participantId,
                Order = maxOrder + 1,
                GroupId = groupId
            };
            db.ParticipantDays.Add(link);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return link.Id;
        }

        // Already a member: change only the group, preserving the day's chip/order/start time.
        existing.GroupId = groupId;
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return existing.Id;
    }

    public async Task<EventDaySnapshot> GetDaySnapshotAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        // ONE connection, ONE transaction for every table below. Each read otherwise opens its own
        // connection, and under WAL each connection takes its own read snapshot — so a write landing
        // between two reads (a draw saving while a protocol page reloads) would give one list the old
        // rows and the next list the new ones. The built document then showed rows twice, in a broken
        // order. A single transaction pins one snapshot for the whole set.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // Sort orders below mirror the individual per-table readers, so a caller switching to the
        // snapshot sees exactly the ordering it had before.
        var links = await db.ParticipantDays.AsNoTracking()
            .Where(p => p.EventDayId == dayId).OrderBy(p => p.Order).ToListAsync(cancellationToken);
        var participants = await db.Participants.AsNoTracking()
            .OrderBy(p => p.FullName).ToListAsync(cancellationToken);
        var groups = await db.Groups.AsNoTracking()
            .OrderBy(g => g.Name).ToListAsync(cancellationToken);
        var groupDaySettings = await db.GroupDaySettings.AsNoTracking()
            .Where(s => s.EventDayId == dayId).OrderBy(s => s.Order).ToListAsync(cancellationToken);
        var controlPoints = await db.ControlPoints.AsNoTracking()
            .Where(cp => cp.EventDayId == dayId).OrderBy(cp => cp.Order).ToListAsync(cancellationToken);
        var regions = await db.Regions.AsNoTracking()
            .OrderBy(r => r.Name).ToListAsync(cancellationToken);
        var clubs = await db.Clubs.AsNoTracking()
            .OrderBy(c => c.Name).ToListAsync(cancellationToken);
        var dusshes = await db.Dusshes.AsNoTracking()
            .OrderBy(d => d.Name).ToListAsync(cancellationToken);
        var days = await db.Days.AsNoTracking()
            .OrderBy(d => d.Number).ToListAsync(cancellationToken);
        var info = await db.Competition.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

        return new EventDaySnapshot
        {
            Links = links,
            Participants = participants,
            Groups = groups,
            GroupDaySettings = groupDaySettings,
            ControlPoints = controlPoints,
            Regions = regions,
            Clubs = clubs,
            Dusshes = dusshes,
            Days = days,
            Info = info
        };
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
        existing.StartTime = link.StartTime;
        existing.OutOfCompetition = link.OutOfCompetition;
        // NOTE: ResultStatusOverride and Bonus are deliberately NOT copied — each has its own writer
        // (SetParticipantDayResultStatusAsync / SetParticipantDayBonusAsync) so this debounced row save
        // can't wipe the judge override or the points correction.
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetParticipantDayResultStatusAsync(string eventFolderPath, Guid linkId, FinishStatus? status, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.ParticipantDays.FirstOrDefaultAsync(p => p.Id == linkId, cancellationToken);
        if (existing is null)
            return;

        // The sole writer of the override column (UpdateParticipantDayAsync leaves it untouched).
        existing.ResultStatusOverride = status;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetParticipantDayBonusAsync(string eventFolderPath, Guid linkId, int? bonus, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.ParticipantDays.FirstOrDefaultAsync(p => p.Id == linkId, cancellationToken);
        if (existing is null)
            return;

        // The sole writer of the bonus column (UpdateParticipantDayAsync leaves it untouched), so the
        // debounced row save can't wipe a points correction set here.
        existing.Bonus = bonus;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetParticipantDayPaymentAsync(string eventFolderPath, Guid linkId, string payment, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.ParticipantDays.FirstOrDefaultAsync(p => p.Id == linkId, cancellationToken);
        if (existing is null)
            return;

        // The sole writer of the per-day payment column (UpdateParticipantDayAsync leaves it untouched).
        existing.Payment = (payment ?? string.Empty).Trim();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetParticipantDayRaisedFeeAsync(string eventFolderPath, Guid linkId, bool paysRaisedFee, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.ParticipantDays.FirstOrDefaultAsync(p => p.Id == linkId, cancellationToken);
        if (existing is null)
            return;

        // The sole writer of the per-day raised-fee column (UpdateParticipantDayAsync leaves it untouched).
        existing.PaysRaisedFee = paysRaisedFee;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetPaymentPerDayAsync(
        string eventFolderPath,
        bool paymentPerDay,
        IReadOnlyDictionary<Guid, string> participantPayments,
        IReadOnlyDictionary<Guid, string> dayPayments,
        IReadOnlyDictionary<Guid, bool> participantRaisedFees,
        IReadOnlyDictionary<Guid, bool> dayRaisedFees,
        CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var info = await db.Competition.FirstOrDefaultAsync(cancellationToken);
        if (info is not null)
            info.PaymentPerDay = paymentPerDay;

        // The mode switch rewrites the raised-fee flag on the side it moves TO and clears the side it
        // moves away from, so the two never disagree about who pays the raised fee.
        if (participantPayments.Count > 0 || participantRaisedFees.Count > 0 || !paymentPerDay)
        {
            foreach (var participant in await db.Participants.ToListAsync(cancellationToken))
            {
                if (participantPayments.TryGetValue(participant.Id, out var value))
                    participant.Payment = value;
                if (paymentPerDay)
                    participant.PaysRaisedFee = false;
                else
                    participant.PaysRaisedFee = participantRaisedFees.TryGetValue(participant.Id, out var raised) && raised;
            }
        }

        if (dayPayments.Count > 0 || dayRaisedFees.Count > 0 || paymentPerDay)
        {
            foreach (var link in await db.ParticipantDays.ToListAsync(cancellationToken))
            {
                if (dayPayments.TryGetValue(link.Id, out var value))
                    link.Payment = value;
                if (paymentPerDay)
                    link.PaysRaisedFee = dayRaisedFees.TryGetValue(link.Id, out var raised) && raised;
                else
                    link.PaysRaisedFee = false;
            }
        }

        // One SaveChanges = one transaction, so the flag and the migrated values always move together.
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> SetParticipantDayChipsBatchAsync(
        string eventFolderPath,
        IReadOnlyList<(Guid ParticipantId, Guid DayId, string Chip)> assignments,
        CancellationToken cancellationToken = default)
    {
        if (assignments.Count == 0)
            return 0;

        await using var db = EventDbContextFactory.Create(eventFolderPath);

        // Load only the links on the touched days (tracked), then map each assignment to its link and set
        // the chip in memory. A single SaveChanges at the end commits the whole batch in one transaction.
        var dayIds = assignments.Select(a => a.DayId).Distinct().ToList();
        var links = await db.ParticipantDays
            .Where(p => dayIds.Contains(p.EventDayId))
            .ToListAsync(cancellationToken);
        var byKey = links.ToDictionary(l => (l.ParticipantId, l.EventDayId));

        var updated = 0;
        foreach (var (participantId, dayId, chip) in assignments)
        {
            if (!byKey.TryGetValue((participantId, dayId), out var link))
                continue;
            link.Chip = (chip ?? string.Empty).Trim();
            updated++;
        }

        if (updated > 0)
            await db.SaveChangesAsync(cancellationToken);
        return updated;
    }

    public async Task<CopyParticipantsResult> CopyParticipantsBetweenDaysAsync(
        string eventFolderPath,
        CopyParticipantsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.SourceDayId == request.TargetDayId)
            return new CopyParticipantsResult(0, 0, 0, 0);

        await using var db = EventDbContextFactory.Create(eventFolderPath);

        // Everything is read up front and written with one SaveChanges at the end, so a copy either
        // lands whole or not at all.
        var source = await db.ParticipantDays
            .Where(p => p.EventDayId == request.SourceDayId)
            .OrderBy(p => p.Order)
            .ToListAsync(cancellationToken);
        if (source.Count == 0)
            return new CopyParticipantsResult(0, 0, 0, 0);

        var target = await db.ParticipantDays
            .Where(p => p.EventDayId == request.TargetDayId)
            .ToListAsync(cancellationToken);

        // Who is already there stays untouched; a copy never overwrites an existing day record.
        var present = target.Select(p => p.ParticipantId).ToHashSet();

        // Chips must stay unique per day, so the numbers already in use on the target day block a copy
        // of that same number; each chip we hand out joins the set so two source rows can't collide either.
        var chipsInUse = target
            .Select(p => (p.Chip ?? string.Empty).Trim())
            .Where(c => c.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The groups already on the target day. A copied member's group that is missing here gets its own
        // GroupDaySettings row (blank course fields) so the group genuinely runs on the target day.
        var targetGroupSettings = await db.GroupDaySettings
            .Where(g => g.EventDayId == request.TargetDayId)
            .ToListAsync(cancellationToken);
        var groupsOnTarget = targetGroupSettings.Select(g => g.GroupId).ToHashSet();
        var nextGroupOrder = targetGroupSettings.Count == 0 ? 1 : targetGroupSettings.Max(g => g.Order) + 1;

        var nextOrder = target.Count == 0 ? 1 : target.Max(p => p.Order) + 1;
        var options = request.Options;

        var copied = 0;
        var alreadyPresent = 0;
        var groupsCreated = 0;
        var chipsSkipped = 0;

        foreach (var link in source)
        {
            if (present.Contains(link.ParticipantId))
            {
                alreadyPresent++;
                continue;
            }

            // Bring the group onto the target day when it isn't there yet. The group itself is
            // competition-level, so nothing needs creating at that level — only its day membership.
            if (link.GroupId is { } groupId && groupsOnTarget.Add(groupId))
            {
                db.GroupDaySettings.Add(new GroupDaySettings
                {
                    EventDayId = request.TargetDayId,
                    GroupId = groupId,
                    Order = nextGroupOrder++
                });
                groupsCreated++;
            }

            var chip = string.Empty;
            if (options.Chip)
            {
                var candidate = (link.Chip ?? string.Empty).Trim();
                if (candidate.Length > 0)
                {
                    if (chipsInUse.Add(candidate))
                        chip = candidate;
                    else
                        chipsSkipped++;
                }
            }

            db.ParticipantDays.Add(new ParticipantDay
            {
                EventDayId = request.TargetDayId,
                ParticipantId = link.ParticipantId,
                Order = nextOrder++,
                GroupId = link.GroupId,
                Chip = chip,
                // «Оплата» is one concept to the user: the note and the raised-fee flag travel together.
                Payment = options.Payment ? link.Payment ?? string.Empty : string.Empty,
                PaysRaisedFee = options.Payment && link.PaysRaisedFee,
                StartTime = options.StartTime ? link.StartTime : null,
                OutOfCompetition = options.OutOfCompetition && link.OutOfCompetition
            });
            present.Add(link.ParticipantId);
            copied++;
        }

        if (copied > 0 || groupsCreated > 0)
            await db.SaveChangesAsync(cancellationToken);

        return new CopyParticipantsResult(copied, alreadyPresent, groupsCreated, chipsSkipped);
    }

    public async Task<int> SetParticipantDayStartTimesBatchAsync(
        string eventFolderPath,
        IReadOnlyList<(Guid LinkId, TimeSpan StartTime)> assignments,
        CancellationToken cancellationToken = default)
    {
        if (assignments.Count == 0)
            return 0;

        await using var db = EventDbContextFactory.Create(eventFolderPath);

        // Load the touched links (tracked) by id, set their start time in memory, then one SaveChanges
        // commits the whole draw in a single transaction.
        var ids = assignments.Select(a => a.LinkId).Distinct().ToList();
        var links = await db.ParticipantDays
            .Where(p => ids.Contains(p.Id))
            .ToListAsync(cancellationToken);
        var byId = links.ToDictionary(l => l.Id);

        var updated = 0;
        foreach (var (linkId, startTime) in assignments)
        {
            if (!byId.TryGetValue(linkId, out var link))
                continue;
            link.StartTime = startTime;
            updated++;
        }

        if (updated > 0)
            await db.SaveChangesAsync(cancellationToken);
        return updated;
    }

    public async Task<int> SetParticipantNumbersBatchAsync(
        string eventFolderPath,
        IReadOnlyList<(Guid ParticipantId, string Number)> assignments,
        CancellationToken cancellationToken = default)
    {
        if (assignments.Count == 0)
            return 0;

        await using var db = EventDbContextFactory.Create(eventFolderPath);

        // Load only the touched participants (tracked), set each number in memory, then one SaveChanges
        // commits the whole assignment in a single transaction — no overlapping per-row writes to drop.
        var ids = assignments.Select(a => a.ParticipantId).Distinct().ToList();
        var participants = await db.Participants
            .Where(p => ids.Contains(p.Id))
            .ToListAsync(cancellationToken);
        var byId = participants.ToDictionary(p => p.Id);

        var updated = 0;
        foreach (var (participantId, number) in assignments)
        {
            if (!byId.TryGetValue(participantId, out var participant))
                continue;
            participant.Number = (number ?? string.Empty).Trim();
            updated++;
        }

        if (updated > 0)
            await db.SaveChangesAsync(cancellationToken);
        return updated;
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

    public async Task UpdateFinishReadoutAsync(string eventFolderPath, FinishReadout readout, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        var existing = await db.FinishReadouts.FirstOrDefaultAsync(r => r.Id == readout.Id, cancellationToken);
        if (existing is null)
            return;

        existing.ChipNumber = readout.ChipNumber;
        existing.StartTime = readout.StartTime;
        existing.FinishTime = readout.FinishTime;
        existing.Punches = readout.Punches;
        existing.PunchTimes = readout.PunchTimes;
        existing.ManualStatus = readout.ManualStatus;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> DeleteFinishReadoutsForDayAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.FinishReadouts
            .Where(r => r.EventDayId == dayId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChipPriceOverride>> GetChipPriceOverridesAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        return await db.ChipPriceOverrides
            .AsNoTracking()
            .OrderBy(o => o.Note)
            .ToListAsync(cancellationToken);
    }

    public async Task AddChipPriceOverrideAsync(string eventFolderPath, ChipPriceOverride priceOverride, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        db.ChipPriceOverrides.Add(priceOverride);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateChipPriceOverrideAsync(string eventFolderPath, ChipPriceOverride priceOverride, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.ChipPriceOverrides.FirstOrDefaultAsync(o => o.Id == priceOverride.Id, cancellationToken);
        if (existing is null)
            return;

        existing.Note = priceOverride.Note;
        existing.PricePerDay = priceOverride.PricePerDay;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteChipPriceOverrideAsync(string eventFolderPath, Guid overrideId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.ChipPriceOverrides.FirstOrDefaultAsync(o => o.Id == overrideId, cancellationToken);
        if (existing is null)
            return;

        db.ChipPriceOverrides.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The default name of the seeded, non-deletable FSOU-member discount (uk-UA, the app
    /// default language). It is user-editable afterwards; the <see cref="EntryFeeDiscount.IsFsouMemberDiscount"/>
    /// flag — not the name — is what marks it.</summary>
    private const string FsouMemberDiscountName = "Знижка членам ФСОУ";

    public async Task<IReadOnlyList<EntryFeeDiscount>> GetEntryFeeDiscountsAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        // Ensure the always-present FSOU-member discount exists exactly once before returning the list,
        // so every competition has it (0 % by default) without a separate seeding step.
        var hasFsou = await db.EntryFeeDiscounts.AnyAsync(d => d.IsFsouMemberDiscount, cancellationToken);
        if (!hasFsou)
        {
            db.EntryFeeDiscounts.Add(new EntryFeeDiscount
            {
                Name = FsouMemberDiscountName,
                Percent = 0,
                IsFsouMemberDiscount = true
            });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // A concurrent load (e.g. two pages opening at once) won the race and inserted the
                // FSOU-member row first; the filtered unique index rejected ours. That is fine — the
                // row exists, so re-load the list below without it.
                db.ChangeTracker.Clear();
            }
        }

        // The FSOU-member discount sorts first; the rest by name. Done client-side after the query so
        // the ordering rule (flag first) stays simple.
        var all = await db.EntryFeeDiscounts.AsNoTracking().ToListAsync(cancellationToken);
        return all
            .OrderByDescending(d => d.IsFsouMemberDiscount)
            .ThenBy(d => d.Name)
            .ToList();
    }

    public async Task AddEntryFeeDiscountAsync(string eventFolderPath, EntryFeeDiscount discount, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        db.EntryFeeDiscounts.Add(discount);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateEntryFeeDiscountAsync(string eventFolderPath, EntryFeeDiscount discount, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.EntryFeeDiscounts.FirstOrDefaultAsync(d => d.Id == discount.Id, cancellationToken);
        if (existing is null)
            return;

        existing.Name = discount.Name;
        existing.Percent = discount.Percent;
        existing.AppliesToChipRental = discount.AppliesToChipRental;
        // IsFsouMemberDiscount is intrinsic to the seeded row and never toggled by an edit, so it
        // is deliberately not copied here.
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteEntryFeeDiscountAsync(string eventFolderPath, Guid discountId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);

        var existing = await db.EntryFeeDiscounts.FirstOrDefaultAsync(d => d.Id == discountId, cancellationToken);
        if (existing is null)
            return;

        // The FSOU-member discount is permanent — never delete it (the UI also hides its delete button).
        if (existing.IsFsouMemberDiscount)
            return;

        // Drop the participant↔discount links for this discount, then the discount itself.
        await db.ParticipantDiscounts.Where(p => p.DiscountId == discountId).ExecuteDeleteAsync(cancellationToken);
        db.EntryFeeDiscounts.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> GetResultProtocolJsonAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        var row = await db.ResultProtocolSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.EventDayId == dayId, cancellationToken);
        return row?.Json;
    }

    public async Task SaveResultProtocolJsonAsync(string eventFolderPath, Guid dayId, string json, CancellationToken cancellationToken = default)
    {
        // Auto-save fires fire-and-forget on every edit, so two saves for the same day can run concurrently:
        // both read no row and both INSERT, which trips the (EventDayId) unique index. Retry once as an update
        // if the concurrent insert won the race.
        await UpsertWithUniqueRetryAsync(eventFolderPath, async db =>
        {
            var row = await db.ResultProtocolSettings.FirstOrDefaultAsync(r => r.EventDayId == dayId, cancellationToken);
            if (row is null)
            {
                row = new ResultProtocolSettingsRow { EventDayId = dayId, Json = json };
                db.ResultProtocolSettings.Add(row);
            }
            else
            {
                row.Json = json;
            }
            await db.SaveChangesAsync(cancellationToken);
        });
    }

    public async Task<string?> GetStartProtocolJsonAsync(string eventFolderPath, Guid dayId, StartProtocolKind kind, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        var row = await db.StartProtocolSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.EventDayId == dayId && r.Kind == kind, cancellationToken);
        return row?.Json;
    }

    public async Task SaveStartProtocolJsonAsync(string eventFolderPath, Guid dayId, StartProtocolKind kind, string json, CancellationToken cancellationToken = default)
    {
        // See SaveResultProtocolJsonAsync: concurrent auto-saves race on the (EventDayId, Kind) unique index.
        await UpsertWithUniqueRetryAsync(eventFolderPath, async db =>
        {
            var row = await db.StartProtocolSettings.FirstOrDefaultAsync(r => r.EventDayId == dayId && r.Kind == kind, cancellationToken);
            if (row is null)
            {
                row = new StartProtocolSettingsRow { EventDayId = dayId, Kind = kind, Json = json };
                db.StartProtocolSettings.Add(row);
            }
            else
            {
                row.Json = json;
            }
            await db.SaveChangesAsync(cancellationToken);
        });
    }

    public async Task<string?> GetDrawSettingsJsonAsync(string eventFolderPath, Guid dayId, DrawSettingsKind kind, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        var row = await db.DrawSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.EventDayId == dayId && r.Kind == kind, cancellationToken);
        return row?.Json;
    }

    public async Task SaveDrawSettingsJsonAsync(string eventFolderPath, Guid dayId, DrawSettingsKind kind, string json, CancellationToken cancellationToken = default)
    {
        // See SaveResultProtocolJsonAsync: concurrent auto-saves race on the (EventDayId, Kind) unique index.
        await UpsertWithUniqueRetryAsync(eventFolderPath, async db =>
        {
            var row = await db.DrawSettings.FirstOrDefaultAsync(r => r.EventDayId == dayId && r.Kind == kind, cancellationToken);
            if (row is null)
            {
                row = new DrawSettingsRow { EventDayId = dayId, Kind = kind, Json = json };
                db.DrawSettings.Add(row);
            }
            else
            {
                row.Json = json;
            }
            await db.SaveChangesAsync(cancellationToken);
        });
    }

    public async Task<string?> GetSummaryProtocolJsonAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        var row = await db.SummaryProtocolSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        return row?.Json;
    }

    public async Task SaveSummaryProtocolJsonAsync(string eventFolderPath, string json, CancellationToken cancellationToken = default)
    {
        // Single competition-level row (Id = 1). Concurrent auto-saves race on the primary key, so use the same
        // read-then-insert-or-update retry the per-day templates use.
        await UpsertWithUniqueRetryAsync(eventFolderPath, async db =>
        {
            var row = await db.SummaryProtocolSettings.FirstOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                row = new SummaryProtocolSettingsRow { Id = 1, Json = json };
                db.SummaryProtocolSettings.Add(row);
            }
            else
            {
                row.Json = json;
            }
            await db.SaveChangesAsync(cancellationToken);
        });
    }

    public async Task<string?> GetStatementJsonAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        var row = await db.StatementSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        return row?.Json;
    }

    public async Task SaveStatementJsonAsync(string eventFolderPath, string json, CancellationToken cancellationToken = default)
    {
        // Single competition-level row (Id = 1). Concurrent auto-saves race on the primary key, so use the same
        // read-then-insert-or-update retry the summary-protocol template uses.
        await UpsertWithUniqueRetryAsync(eventFolderPath, async db =>
        {
            var row = await db.StatementSettings.FirstOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                row = new StatementSettingsRow { Id = 1, Json = json };
                db.StatementSettings.Add(row);
            }
            else
            {
                row.Json = json;
            }
            await db.SaveChangesAsync(cancellationToken);
        });
    }

    public async Task<string?> GetOnlinePublishJsonAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        var row = await db.OnlinePublishSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        return row?.Json;
    }

    public async Task SaveOnlinePublishJsonAsync(string eventFolderPath, string json, CancellationToken cancellationToken = default)
    {
        // Single competition-level row (Id = 1); same read-then-insert-or-update retry as the protocol templates.
        await UpsertWithUniqueRetryAsync(eventFolderPath, async db =>
        {
            var row = await db.OnlinePublishSettings.FirstOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                row = new OnlinePublishSettingsRow { Id = 1, Json = json };
                db.OnlinePublishSettings.Add(row);
            }
            else
            {
                row.Json = json;
            }
            await db.SaveChangesAsync(cancellationToken);
        });
    }

    public async Task<string?> GetMonitorJsonAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        await using var db = EventDbContextFactory.Create(eventFolderPath);
        var row = await db.MonitorSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        return row?.Json;
    }

    public async Task SaveMonitorJsonAsync(string eventFolderPath, string json, CancellationToken cancellationToken = default)
    {
        // Single competition-level row (Id = 1); same read-then-insert-or-update retry as the protocol templates.
        await UpsertWithUniqueRetryAsync(eventFolderPath, async db =>
        {
            var row = await db.MonitorSettings.FirstOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                row = new MonitorSettingsRow { Id = 1, Json = json };
                db.MonitorSettings.Add(row);
            }
            else
            {
                row.Json = json;
            }
            await db.SaveChangesAsync(cancellationToken);
        });
    }

    // Runs a read-then-insert-or-update against a fresh context, retrying once on a SQLite UNIQUE violation:
    // when two callers race, the loser's INSERT fails, so the retry re-reads (now seeing the winner's row) and
    // updates instead. Each attempt gets its own context so the failed change tracker is discarded.
    private static async Task UpsertWithUniqueRetryAsync(string eventFolderPath, Func<EventDbContext, Task> upsert)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var db = EventDbContextFactory.Create(eventFolderPath);
            try
            {
                await upsert(db);
                return;
            }
            catch (DbUpdateException ex) when (attempt == 0 && IsUniqueViolation(ex))
            {
                // Lost the insert race; loop to re-read and update.
            }
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 };

    public async Task<ParticipantImportResult> ImportParticipantsBatchAsync(
        string eventFolderPath,
        UofParticipantData data,
        bool clearFirst,
        int daysCreated,
        ParticipantImportScope scope,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(scope);

        var currentDayOnly = scope.Mode == ParticipantImportMode.CurrentDayOnly;

        await using var db = EventDbContextFactory.Create(eventFolderPath);

        progress?.Report(ImportProgress.Counted(ImportStage.Parsed, 0, data.Participants.Count));

        // 1. Organiser: fill it from the file when present (don't clobber an existing value with blank).
        if (!string.IsNullOrWhiteSpace(data.Organisation))
        {
            var info = await db.Competition.FirstOrDefaultAsync(cancellationToken);
            if (info is not null)
                info.Organisation = data.Organisation.Trim();
        }

        // 2. Optional wipe. All-days mode clears the whole participant database so the file becomes the
        //    full roster. Current-day-only mode clears just the target day's links (the participants and
        //    their other days stay), so the file becomes that day's roster without touching other days.
        if (clearFirst)
        {
            if (currentDayOnly)
            {
                var targetDay = await db.Days
                    .Where(d => d.Number == scope.TargetDayNumber)
                    .Select(d => (Guid?)d.Id)
                    .FirstOrDefaultAsync(cancellationToken);
                if (targetDay is { } id)
                    await db.ParticipantDays.Where(l => l.EventDayId == id).ExecuteDeleteAsync(cancellationToken);
            }
            else
            {
                await db.ParticipantDays.ExecuteDeleteAsync(cancellationToken);
                await db.Participants.ExecuteDeleteAsync(cancellationToken);
            }
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
        var competition = await db.Competition.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var startYear = competition?.StartDate?.Year ?? DateTimeOffset.Now.Year;
        // Where an imported payment lands: on the participant (one payment for the competition) or on each
        // day link the import touches (per-day mode).
        var paymentPerDay = competition?.PaymentPerDay ?? false;

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
                var (min, max) = Group.DeriveAgeWindow(trimmed, startYear);
                existing = new Group { Name = trimmed, MinBirthYear = min, MaxBirthYear = max };
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

        // Existing participants to match imported rows against, so a re-import updates in place rather
        // than duplicating. All-days mode matches by FOU code (and finds nothing after a full wipe);
        // current-day-only mode never wipes participants and matches by the chosen link field so the
        // new day's link attaches to the same person already imported from another day.
        var matchByName = scope.LinkField == ParticipantLinkField.FullName;
        var existingParticipants = (!currentDayOnly && clearFirst)
            ? new List<Participant>()
            : await db.Participants.ToListAsync(cancellationToken);

        static string NameKey(string name) => string.Join(' ',
            name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var byFsouCode = existingParticipants
            .Where(p => !string.IsNullOrWhiteSpace(p.FsouCode))
            .GroupBy(p => p.FsouCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byName = existingParticipants
            .Where(p => !string.IsNullOrWhiteSpace(p.FullName))
            .GroupBy(p => NameKey(p.FullName), StringComparer.OrdinalIgnoreCase)
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
            if (matchByName)
            {
                var nameKey = NameKey(src.FullName);
                if (nameKey.Length > 0 && byName.TryGetValue(nameKey, out var matchedByName))
                    participant = matchedByName;
            }
            else if (code.Length > 0 && byFsouCode.TryGetValue(code, out var matched))
            {
                participant = matched;
            }

            // The payment cell may simply be absent from the file (an unmapped CSV column, a blank cell);
            // that is "no information", not "clear it", so a blank value never overwrites a stored one.
            var srcPayment = (src.Payment ?? string.Empty).Trim();
            var hasPayment = srcPayment.Length > 0;

            if (participant is null)
            {
                participant = new Participant
                {
                    FullName = src.FullName,
                    Number = src.Number,
                    Team = src.Team,
                    Rank = src.Rank,
                    Coach = src.Coach,
                    BirthDate = src.BirthDate,
                    RegionId = regionId,
                    ClubId = clubId,
                    DusshId = dusshId,
                    Representative = src.Representative,
                    FsouCode = code,
                    IsFsouMember = src.IsFsouMember,
                    // A brand-new athlete is fully populated, but the payment still honours the caller's
                    // "оплата" toggle (a day-scoped import that was told not to bring payments must not
                    // bring them for new people either), and in per-day mode it belongs on the day link.
                    Payment = !paymentPerDay && hasPayment && ImportsPayment(scope, currentDayOnly)
                        ? srcPayment
                        : string.Empty
                };
                db.Participants.Add(participant);
                added++;

                // Register the new participant so a later row with the same key (a second file line for
                // the same athlete) updates it rather than adding a duplicate.
                var newNameKey = NameKey(participant.FullName);
                if (newNameKey.Length > 0)
                    byName.TryAdd(newNameKey, participant);
                if (code.Length > 0)
                    byFsouCode.TryAdd(code, participant);
            }
            else
            {
                // All-days mode overwrites every participant field (legacy behaviour). Current-day-only mode
                // only touches the fields the caller ticked (default: none — a day-scoped import shouldn't
                // silently rewrite an athlete's shared details across their other days).
                bool Update(ParticipantUpdateFields field) =>
                    !currentDayOnly || scope.UpdateFields.HasFlag(field);

                if (Update(ParticipantUpdateFields.FullName))
                    participant.FullName = src.FullName;
                // Number and Team are absent in UOF files; only overwrite when the source supplies one,
                // so re-importing UOF data over a CSV-set number/team doesn't wipe it.
                if (Update(ParticipantUpdateFields.Number) && src.Number.Length > 0)
                    participant.Number = src.Number;
                if (Update(ParticipantUpdateFields.Team) && src.Team.Length > 0)
                    participant.Team = src.Team;
                if (Update(ParticipantUpdateFields.Rank))
                    participant.Rank = src.Rank;
                if (Update(ParticipantUpdateFields.Coach))
                    participant.Coach = src.Coach;
                if (Update(ParticipantUpdateFields.BirthDate))
                    participant.BirthDate = src.BirthDate;
                if (Update(ParticipantUpdateFields.Region))
                    participant.RegionId = regionId;
                if (Update(ParticipantUpdateFields.Club))
                    participant.ClubId = clubId;
                if (Update(ParticipantUpdateFields.Dussh))
                    participant.DusshId = dusshId;
                if (Update(ParticipantUpdateFields.Representative))
                    participant.Representative = src.Representative;
                if (Update(ParticipantUpdateFields.IsFsouMember))
                    participant.IsFsouMember = src.IsFsouMember;
                // Never wipe a stored payment with a blank cell (see srcPayment above); in per-day mode the
                // value belongs on the day link, so the participant-level column is left alone entirely.
                if (!paymentPerDay && hasPayment && Update(ParticipantUpdateFields.Payment))
                    participant.Payment = srcPayment;
                updated++;
            }

            var group = ResolveGroup(src.Group);
            var priorLinks = linksByParticipant.TryGetValue(participant.Id, out var l) ? l : [];

            // Current-day-only mode ignores the file's day info and enters the athlete on the target day.
            // Otherwise: a participant the file lists for no day (empty/blank ProgEvent) is entered on
            // every day, rather than being imported with no day membership at all.
            var dayNumbers = currentDayOnly
                ? [scope.TargetDayNumber]
                : src.DayNumbers.Count > 0 ? src.DayNumbers : days.Select(d => d.Number).ToList();

            // A source file may list the same day twice for one participant; entering it once is what the
            // import means, and a second link for the same (day, participant) is rejected by the unique index.
            foreach (var dayNumber in dayNumbers.Distinct())
            {
                if (!dayByNumber.TryGetValue(dayNumber, out var day))
                    continue; // a number with no matching day (shouldn't happen — caller created them)

                if (group is not null)
                    EnsureGroupOnDay(day.Id, group.Id);

                // In per-day mode the file's payment goes onto every day this import enters the athlete on
                // (in current-day-only mode that is exactly the target day) — never onto their other days.
                var dayPayment = paymentPerDay && hasPayment && ImportsPayment(scope, currentDayOnly)
                    ? srcPayment
                    : null;

                var link = priorLinks.FirstOrDefault(x => x.EventDayId == day.Id);
                if (link is null)
                {
                    var newLink = new ParticipantDay
                    {
                        EventDayId = day.Id,
                        ParticipantId = participant.Id,
                        Order = ++linkOrderByDay[day.Id],
                        GroupId = group?.Id,
                        Chip = src.Chip,
                        Payment = dayPayment ?? string.Empty
                    };
                    db.ParticipantDays.Add(newLink);
                    // Track it so a later pass over the same participant updates this link instead of
                    // adding a second one for the day.
                    priorLinks.Add(newLink);
                }
                else
                {
                    link.GroupId = group?.Id;
                    link.Chip = src.Chip;
                    if (dayPayment is not null)
                        link.Payment = dayPayment;
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

    // Whether this import is allowed to bring the «Оплата» column at all. All-days mode always may (its
    // legacy behaviour is to overwrite everything); a day-scoped import only when the user ticked it in the
    // options modal. Applies to a brand-new athlete too, which the per-field Update() check below does not.
    private static bool ImportsPayment(ParticipantImportScope scope, bool currentDayOnly) =>
        !currentDayOnly || scope.UpdateFields.HasFlag(ParticipantUpdateFields.Payment);

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
