using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace OrientPyx.DataAccess.Persistence;

/// <summary>
/// Brings a database up to its latest migration — the one way both <c>app.db</c> and every <c>event.db</c> are migrated.
/// <list type="bullet">
/// <item>An up-to-date database is only read: the migrations history, with plain SQL, against this build's migration
/// list (read once per context type). EF's <c>Migrate</c> is not cheap even when there is nothing to apply — it writes
/// a lock row and compares the model with the snapshot — and even its <c>GetPendingMigrations</c> reflects over the
/// migrations assembly for every new context; the competition list opens every competition's database, so that cost
/// was paid per competition on every start.</item>
/// <item>EF Core 9+ guards <c>Migrate</c> with a row in <c>__EFMigrationsLock</c> (SQLite has no other cross-process
/// lock). A process killed while holding it — Task Manager, a crash, a power cut — leaves the row behind and every later
/// <c>Migrate</c> of that database waits for it forever, so the program would hang on start. A row older than
/// <see cref="StaleAfter"/> belongs to no live migration (a real one takes seconds) and is removed first.</item>
/// </list>
/// </summary>
internal static class DatabaseMigrator
{
    private const string LockTable = "__EFMigrationsLock";
    private const string HistoryTable = "__EFMigrationsHistory";

    private static readonly ConcurrentDictionary<Type, string[]> KnownMigrations = new();

    /// <summary>A lock row this old was left by a process that died while migrating.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    /// <summary>The activity log line writer (set once by DI); null in a bare test.</summary>
    internal static Action<string>? Log { get; set; }

    public static async Task MigrateAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        // No database file yet: EF creates it — in WAL journal mode, which it sets only when IT creates the file — and
        // applies every migration. Nothing here may open a connection first: SQLite would create an empty file on
        // open, EF would then take the database as existing, and the competition would stay in rollback-journal mode,
        // where any reader makes a concurrent commit fail with «database is locked» (caught by the UI tests, 2026-11-23).
        if (!await db.GetService<IRelationalDatabaseCreator>().ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await IsUpToDateAsync(db, cancellationToken).ConfigureAwait(false))
            return;

        await ClearStaleLockAsync(db, cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous form for the app database at start-up (no synchronization context is captured).</summary>
    public static void Migrate(DbContext db) => MigrateAsync(db).GetAwaiter().GetResult();

    private static async Task<bool> IsUpToDateAsync(DbContext db, CancellationToken cancellationToken)
    {
        var known = KnownMigrations.GetOrAdd(db.GetType(), _ => db.Database.GetMigrations().ToArray());
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await TableExistsAsync(connection, HistoryTable, cancellationToken).ConfigureAwait(false))
                return false;

            var applied = new HashSet<string>(StringComparer.Ordinal);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT \"MigrationId\" FROM \"{HistoryTable}\";";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                applied.Add(reader.GetString(0));
            return known.All(applied.Contains);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task<bool> TableExistsAsync(DbConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}';";
        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(count, CultureInfo.InvariantCulture) > 0;
    }

    // Waits while another process migrates (its lock row is fresh), removes a row left by a dead one.
    private static async Task ClearStaleLockAsync(DbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // An unreadable stamp counts from the moment it was first seen, so the wait is bounded either way.
            var firstSeen = DateTimeOffset.UtcNow;
            while (true)
            {
                var (exists, stamp) = await ReadLockAsync(connection, cancellationToken).ConfigureAwait(false);
                if (!exists)
                    return;

                var takenAt = stamp ?? firstSeen;
                if (DateTimeOffset.UtcNow - takenAt >= StaleAfter)
                {
                    await using var delete = connection.CreateCommand();
                    delete.CommandText = $"DELETE FROM \"{LockTable}\";";
                    await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    Log?.Invoke($"Removed a stale migration lock from {connection.DataSource} (taken {takenAt:u}).");
                    return;
                }

                await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task<(bool Exists, DateTimeOffset? Stamp)> ReadLockAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, LockTable, cancellationToken).ConfigureAwait(false))
            return (false, null);

        await using var row = connection.CreateCommand();
        row.CommandText = $"SELECT \"Timestamp\" FROM \"{LockTable}\" LIMIT 1;";
        var value = await row.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
            return (false, null);

        return DateTimeOffset.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var stamp)
            ? (true, stamp)
            : (true, null);
    }
}
