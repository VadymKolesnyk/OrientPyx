using Microsoft.EntityFrameworkCore;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.DataAccess.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IAppStore"/> over the shared app database.
/// Stateless — creates a short-lived context per operation via the factory — so it can be
/// consumed by singleton services (e.g. the session) without a captive-dependency problem.
/// </summary>
public sealed class AppStore : IAppStore
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public AppStore(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public AppPaths GetDefaultPaths() => new()
    {
        DataPath = AppDatabasePaths.DefaultDataPath,
        EventsPath = AppDatabasePaths.DefaultEventsPath
    };

    public async Task<AppPaths?> GetPathsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (row is null)
            return null;

        return new AppPaths { DataPath = row.DataPath, EventsPath = row.EventsPath };
    }

    public async Task SavePathsAsync(AppPaths paths, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            db.Settings.Add(new AppSettingsRow { Id = 1, DataPath = paths.DataPath, EventsPath = paths.EventsPath });
        }
        else
        {
            row.DataPath = paths.DataPath;
            row.EventsPath = paths.EventsPath;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<double?> GetFontScaleAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return row?.FontScale;
    }

    public async Task SaveFontScaleAsync(double fontScale, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            db.Settings.Add(new AppSettingsRow { Id = 1, FontScale = fontScale });
        }
        else
        {
            row.FontScale = fontScale;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<(string? Identifier, int? DayNumber)> GetLastSessionAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.LastSession.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return (row?.LastEventIdentifier, row?.LastEventDayNumber);
    }

    public async Task SaveLastSessionAsync(string? identifier, int? dayNumber, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.LastSession.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            db.LastSession.Add(new LastSessionRow { Id = 1, LastEventIdentifier = identifier, LastEventDayNumber = dayNumber });
        }
        else
        {
            row.LastEventIdentifier = identifier;
            row.LastEventDayNumber = dayNumber;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
