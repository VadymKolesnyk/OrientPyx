using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrientPyx.DataAccess.Persistence;

namespace OrientPyx.Tests.Core;

/// <summary>
/// Opening a competition database (<see cref="EventStore.EnsureCreatedAsync"/> → <see cref="DatabaseMigrator"/>):
/// EF Core guards a migration with a row in <c>__EFMigrationsLock</c>, and a process killed while holding it left the
/// database unopenable — every later migrate waited for the row forever, so the program hung on start. Found while
/// measuring the start-up (2026-11-23): a benchmark run killed during the competition list left two such databases.
/// </summary>
public sealed class DatabaseMigratorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "orientpyx-tests", "migrator-" + Guid.NewGuid().ToString("N"));

    public DatabaseMigratorTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task NewDatabase_IsCreatedInWalMode()
    {
        // EF sets WAL only when it creates the file itself. A migrator that opened a connection first left every new
        // competition in rollback-journal mode, where a reader makes a concurrent commit fail with «database is locked».
        await new EventStore().EnsureCreatedAsync(_folder);

        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string?)await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task LockLeftByAKilledMigration_IsRemoved_AndTheDatabaseIsCreated()
    {
        // The state a kill leaves on the very first open: the lock table with its row, nothing else.
        await WriteLockAsync(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));

        await new EventStore().EnsureCreatedAsync(_folder).WaitAsync(TimeSpan.FromSeconds(30));

        await using var db = EventDbContextFactory.Create(_folder);
        Assert.Equal(0, await db.Competition.CountAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(0, await CountLockRowsAsync());
    }

    [Fact]
    public async Task UpToDateDatabase_IsOnlyRead_EvenWithALockRowInIt()
    {
        await new EventStore().EnsureCreatedAsync(_folder);
        // Another process migrating right now (a fresh row): an up-to-date database has nothing to wait for.
        await WriteLockAsync(DateTimeOffset.UtcNow);

        var clock = Stopwatch.StartNew();
        await new EventStore().EnsureCreatedAsync(_folder).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        Assert.Equal(1, await CountLockRowsAsync()); // not touched
    }

    [Fact]
    public async Task FreshLockOfALiveMigration_IsWaitedFor_NotTaken()
    {
        await WriteLockAsync(DateTimeOffset.UtcNow);
        var release = Task.Run(async () =>
        {
            await Task.Delay(700);
            await ExecuteAsync("DELETE FROM \"__EFMigrationsLock\";");
        });

        var clock = Stopwatch.StartNew();
        await new EventStore().EnsureCreatedAsync(_folder).WaitAsync(TimeSpan.FromSeconds(30));
        await release;

        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(600), $"took {clock.Elapsed}");
        await using var db = EventDbContextFactory.Create(_folder);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    private Task WriteLockAsync(DateTimeOffset takenAt) => ExecuteAsync(
        """
        CREATE TABLE IF NOT EXISTS "__EFMigrationsLock" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK___EFMigrationsLock" PRIMARY KEY,
            "Timestamp" TEXT NOT NULL
        );
        INSERT OR REPLACE INTO "__EFMigrationsLock" ("Id", "Timestamp") VALUES (1, $stamp);
        """,
        takenAt.ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", System.Globalization.CultureInfo.InvariantCulture));

    private async Task<long> CountLockRowsAsync()
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsLock\";";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql, string? stamp = null)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (stamp is not null)
            command.Parameters.AddWithValue("$stamp", stamp);
        await command.ExecuteNonQueryAsync();
    }

    private SqliteConnection Open() =>
        new($"Data Source={Path.Combine(_folder, AppDatabasePaths.EventDatabaseFileName)};Pooling=False");
}
