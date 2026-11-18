using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Services.Undo;

namespace OrientPyx.DataAccess.Persistence.Undo;

/// <summary>
/// Journals every row an event-database <c>SaveChanges</c> changes under an undo step (<see cref="UndoScope.Current"/>):
/// its type, key and old/new values, into <see cref="UndoJournal.Shared"/> once the write is committed (at once without
/// a transaction, at <c>Commit</c> inside one; a failed save or a rolled-back / never-committed transaction leaves
/// nothing). A bulk statement (<c>ExecuteUpdate</c> / <c>ExecuteDelete</c>, raw DML) under a step makes the step not
/// undoable — the journal cannot see what it changed. One set of interceptors per process, added by
/// <see cref="EventDbContextFactory"/>; the per-context state lives in a weak table.
/// </summary>
internal static class UndoRecorder
{
    private static readonly ConditionalWeakTable<DbContext, ContextState> States = new();

    /// <summary>Derived columns never journalled: the points-rule copy is recomputed after a revert (EditHistoryService).</summary>
    internal static readonly IReadOnlySet<(Type Entity, string Property)> ExcludedColumns = new HashSet<(Type, string)>
    {
        (typeof(CompetitionInfo), nameof(CompetitionInfo.PointsRulesJson)),
    };

    /// <summary>The journal the recorder writes to (the process-wide one).</summary>
    internal static UndoJournal Journal => UndoJournal.Shared;

    /// <summary>The activity log line writer (set once by DI); null in a bare test.</summary>
    internal static Action<string>? Log { get; set; }

    /// <summary>The interceptors to add to every event-database context.</summary>
    internal static readonly IInterceptor[] Interceptors =
    [
        new SaveInterceptor(),
        new TransactionInterceptor(),
        new CommandInterceptor(),
    ];

    /// <summary>Binds a new context to its competition folder (the journal's key).</summary>
    internal static void Attach(DbContext db, string eventFolderPath) => States.AddOrUpdate(db, new ContextState(eventFolderPath));

    private sealed class ContextState
    {
        public ContextState(string path) => Path = path;

        public string Path { get; }

        public bool InSaveChanges { get; set; }

        public Guid CapturingStep { get; set; }

        public string? CapturingOwner { get; set; }

        public List<UndoEntityChange>? Capturing { get; set; }

        // Saved inside a transaction, waiting for its commit (per transaction object, so a silently disposed one
        // never leaks into a later commit of the same context).
        public List<(DbTransaction Transaction, Guid Step, string? Owner, UndoChangeSet Set)> Pending { get; } = [];
    }

    // ── SaveChanges

    private static void OnSaving(DbContext? db)
    {
        if (db is null || !States.TryGetValue(db, out var state))
            return;
        state.InSaveChanges = true;
        state.Capturing = null;
        if (UndoScope.Current is not { } step)
            return;

        if (!UndoScope.IsActive(step) || Journal.IsReverted(state.Path, step))
        {
            // A task that outlived its step (or a write under an already reverted one): kept out of the step, logged so
            // a missing Suppress shows up in the activity log.
            db.ChangeTracker.DetectChanges();
            if (db.ChangeTracker.HasChanges())
                Log?.Invoke($"Undo: a write under step {step} after the step ended was kept out of it");
            return;
        }

        db.ChangeTracker.DetectChanges();
        var changes = new List<UndoEntityChange>();
        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (Capture(entry) is { } change)
                changes.Add(change);
        }
        state.Capturing = changes;
        state.CapturingStep = step;
        state.CapturingOwner = UndoScope.Owner;
    }

    private static UndoEntityChange? Capture(EntityEntry entry)
    {
        if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            return null;
        var type = entry.Metadata.ClrType;
        var keyProperties = entry.Metadata.FindPrimaryKey()?.Properties;
        if (keyProperties is null)
            return null;
        var key = keyProperties
            .Select(p => entry.State == EntityState.Deleted ? entry.Property(p.Name).OriginalValue : entry.Property(p.Name).CurrentValue)
            .ToArray();
        if (key.Any(k => k is null))
            return null;

        switch (entry.State)
        {
            case EntityState.Added:
            {
                var change = new UndoEntityChange { EntityType = type, Key = key!, Kind = UndoChangeKind.Added };
                foreach (var property in entry.Properties)
                    change.After[property.Metadata.Name] = property.CurrentValue;
                return change;
            }
            case EntityState.Deleted:
            {
                var change = new UndoEntityChange { EntityType = type, Key = key!, Kind = UndoChangeKind.Deleted };
                foreach (var property in entry.Properties)
                    change.Before[property.Metadata.Name] = property.OriginalValue;
                return change;
            }
            default:
            {
                var change = new UndoEntityChange { EntityType = type, Key = key!, Kind = UndoChangeKind.Modified };
                foreach (var property in entry.Properties)
                {
                    if (!property.IsModified || ExcludedColumns.Contains((type, property.Metadata.Name)))
                        continue;
                    var before = property.OriginalValue;
                    var after = property.CurrentValue;
                    if (property.Metadata.GetValueComparer().Equals(before, after))
                        continue;
                    change.Before[property.Metadata.Name] = before;
                    change.After[property.Metadata.Name] = after;
                }
                return change.After.Count == 0 ? null : change;
            }
        }
    }

    private static void OnSaved(DbContext? db)
    {
        if (db is null || !States.TryGetValue(db, out var state))
            return;
        state.InSaveChanges = false;
        var captured = state.Capturing;
        state.Capturing = null;
        if (captured is null || captured.Count == 0)
            return;

        var set = new UndoChangeSet(captured);
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        if (transaction is null)
            Journal.Append(state.Path, state.CapturingStep, state.CapturingOwner, set);
        else
            state.Pending.Add((transaction, state.CapturingStep, state.CapturingOwner, set));
    }

    private static void OnSaveFailed(DbContext? db)
    {
        if (db is null || !States.TryGetValue(db, out var state))
            return;
        state.InSaveChanges = false;
        state.Capturing = null;
    }

    // ── Transactions

    private static void OnCommitted(DbContext? db, DbTransaction transaction)
    {
        if (db is null || !States.TryGetValue(db, out var state) || state.Pending.Count == 0)
            return;
        foreach (var pending in state.Pending.Where(p => ReferenceEquals(p.Transaction, transaction)).ToList())
        {
            Journal.Append(state.Path, pending.Step, pending.Owner, pending.Set);
            state.Pending.Remove(pending);
        }
    }

    private static void OnRolledBack(DbContext? db, DbTransaction transaction)
    {
        if (db is null || !States.TryGetValue(db, out var state) || state.Pending.Count == 0)
            return;
        state.Pending.RemoveAll(p => ReferenceEquals(p.Transaction, transaction));
    }

    // ── Bulk statements

    private static void OnCommand(DbContext? db, DbCommand command)
    {
        if (db is null || !States.TryGetValue(db, out var state) || state.InSaveChanges)
            return;
        if (UndoScope.Current is not { } step || !UndoScope.IsActive(step) || !IsDataChange(command.CommandText))
            return;
        Journal.MarkNotUndoable(state.Path, step, UndoScope.Owner);
        Log?.Invoke($"Undo: step {step} made a bulk write the journal cannot see — it cannot be undone");
    }

    internal static bool IsDataChange(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
            return false;
        var text = sql.AsSpan().TrimStart();
        // Skip leading line comments (EF tags) before the statement keyword.
        while (text.StartsWith("--"))
        {
            var end = text.IndexOf('\n');
            if (end < 0)
                return false;
            text = text[(end + 1)..].TrimStart();
        }
        return text.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase);
    }

    // ── The EF hooks

    private sealed class SaveInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            OnSaving(eventData.Context);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            OnSaving(eventData.Context);
            return ValueTask.FromResult(result);
        }

        public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        {
            OnSaved(eventData.Context);
            return result;
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            OnSaved(eventData.Context);
            return ValueTask.FromResult(result);
        }

        public override void SaveChangesFailed(DbContextErrorEventData eventData) => OnSaveFailed(eventData.Context);

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            OnSaveFailed(eventData.Context);
            return Task.CompletedTask;
        }

        public override void SaveChangesCanceled(DbContextEventData eventData) => OnSaveFailed(eventData.Context);

        public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
        {
            OnSaveFailed(eventData.Context);
            return Task.CompletedTask;
        }
    }

    private sealed class TransactionInterceptor : DbTransactionInterceptor
    {
        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
            => OnCommitted(eventData.Context, transaction);

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            OnCommitted(eventData.Context, transaction);
            return Task.CompletedTask;
        }

        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
            => OnRolledBack(eventData.Context, transaction);

        public override Task TransactionRolledBackAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            OnRolledBack(eventData.Context, transaction);
            return Task.CompletedTask;
        }

        public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
            => OnRolledBack(eventData.Context, transaction);

        public override Task TransactionFailedAsync(
            DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            OnRolledBack(eventData.Context, transaction);
            return Task.CompletedTask;
        }
    }

    private sealed class CommandInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            OnCommand(eventData.Context, command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            OnCommand(eventData.Context, command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            OnCommand(eventData.Context, command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            OnCommand(eventData.Context, command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            OnCommand(eventData.Context, command);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            OnCommand(eventData.Context, command);
            return ValueTask.FromResult(result);
        }
    }
}
