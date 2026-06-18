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
        EventsPath = AppDatabasePaths.DefaultEventsPath
    };

    public async Task<AppPaths?> GetPathsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (row is null)
            return null;

        return new AppPaths { EventsPath = row.EventsPath };
    }

    public async Task SavePathsAsync(AppPaths paths, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            db.Settings.Add(new AppSettingsRow { Id = 1, EventsPath = paths.EventsPath });
        }
        else
        {
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

    public async Task<(string PrinterName, int WidthMm)?> GetPrintSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (row is null)
            return null;

        return (row.PrinterName, row.ReceiptWidthMm);
    }

    public async Task SavePrintSettingsAsync(string printerName, int widthMm, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            db.Settings.Add(new AppSettingsRow { Id = 1, PrinterName = printerName, ReceiptWidthMm = widthMm });
        }
        else
        {
            row.PrinterName = printerName;
            row.ReceiptWidthMm = widthMm;
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

    // ── Sports ranks ───────────────────────────────────────────────────────────────────────────────

    public async Task SeedRanksIfEmptyAsync(IReadOnlyList<SportRank> ranks, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Ranks.AnyAsync(cancellationToken))
            return;

        db.Ranks.AddRange(ranks);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SportRank>> GetRanksAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Ranks
            .AsNoTracking()
            .OrderBy(r => r.Order)
            .ThenBy(r => r.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<SportRank> AddRankAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var maxOrder = await db.Ranks.AnyAsync(cancellationToken)
            ? await db.Ranks.MaxAsync(r => r.Order, cancellationToken)
            : -1;

        var rank = new SportRank { Name = string.Empty, Points = 0, Order = maxOrder + 1 };
        db.Ranks.Add(rank);
        await db.SaveChangesAsync(cancellationToken);
        return rank;
    }

    public async Task UpdateRankAsync(SportRank rank, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.Ranks.FirstOrDefaultAsync(r => r.Id == rank.Id, cancellationToken);
        if (existing is null)
            return;

        // Reject a rename that would collide with another rank (case-insensitive); keep the old name.
        var name = (rank.Name ?? string.Empty).Trim();
        if (name.Length > 0)
        {
            var clash = await db.Ranks.AnyAsync(
                r => r.Id != rank.Id && r.Name == name, cancellationToken);
            if (!clash)
                existing.Name = name;
        }
        else
        {
            existing.Name = string.Empty;
        }

        existing.Points = rank.Points;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRankAsync(Guid rankId, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Ranks.FirstOrDefaultAsync(r => r.Id == rankId, cancellationToken);
        if (existing is null)
            return;

        db.Ranks.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
    }
}
