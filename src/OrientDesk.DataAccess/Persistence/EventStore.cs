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
        await db.Database.EnsureCreatedAsync(cancellationToken);
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
        existing.Discipline = day.Discipline;
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
}
