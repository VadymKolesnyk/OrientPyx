using Microsoft.EntityFrameworkCore;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.DataAccess.Persistence;

namespace OrientPyx.DataAccess.FileSystem;

/// <summary>
/// Builds the competition list from the events folder. Every competition's database is opened once — brought up to
/// the current schema (an up-to-date one is only read, see <see cref="DatabaseMigrator"/>) and its summary read on the
/// same connection — and a few folders are read at a time: the list is read on every start (restoring the last
/// competition looks it up here) and every time the picker is shown, so its cost grows with every competition kept.
/// </summary>
public sealed class EventFolderScanner : IEventFolderScanner
{
    private const int Parallelism = 4;

    private readonly IActivityLog _log;

    public EventFolderScanner(IActivityLog log)
    {
        _log = log;
    }

    public async Task<IReadOnlyList<EventSummary>> ScanAsync(string eventsPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventsPath) || !Directory.Exists(eventsPath))
            return [];

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var folders = Directory.EnumerateDirectories(eventsPath)
            .Where(folder => !Path.GetFileName(folder).StartsWith('.'))
            .Where(folder => File.Exists(AppDatabasePaths.GetEventDatabaseFilePath(folder)))
            .ToList();

        var summaries = new EventSummary?[folders.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, folders.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                try
                {
                    summaries[i] = await ReadSummaryAsync(folders[i], ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Error($"Competition folder skipped: {folders[i]}", ex);
                }
            });

        var list = summaries.OfType<EventSummary>().OrderByDescending(s => s.CreatedAt).ToList();
        _log.Info($"Competition list: {list.Count} competitions read in {clock.ElapsedMilliseconds} ms.");
        return list;
    }

    private static async Task<EventSummary?> ReadSummaryAsync(string folder, CancellationToken cancellationToken)
    {
        // A read, like the store's own reads (UndoStoreGuard): it never joins an undo step.
        using var noUndo = BusinessLogic.Services.Undo.UndoScope.Suppress();
        await using var db = EventDbContextFactory.Create(folder);
        await DatabaseMigrator.MigrateAsync(db, cancellationToken);

        var info = await db.Competition.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (info is null)
            return null;

        var dayCount = await db.Days.CountAsync(cancellationToken);

        return new EventSummary
        {
            Identifier = info.Identifier,
            Name = info.Name,
            Venue = info.Venue,
            FolderPath = folder,
            CreatedAt = info.CreatedAt,
            DayCount = dayCount,
            StartDate = info.StartDate,
            EndDate = info.EndDate,
            IsHidden = info.IsHidden
        };
    }
}
