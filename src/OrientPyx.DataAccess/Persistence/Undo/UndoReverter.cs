using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Plugins.Sdk.Toolkit;

namespace OrientPyx.DataAccess.Persistence.Undo;

/// <summary>
/// Reverts one journalled undo step (<c>IEventStore.RevertUndoStepAsync</c>), all or nothing, in one transaction:
/// <list type="number">
/// <item>the step's net effect per row (did it exist before / after, the first «before» and last «after» of each
/// property);</item>
/// <item>each row checked against the database — still as the step left it (revert), already back as before the step
/// (skip, not a conflict), or a third value / gone (conflict);</item>
/// <item>the state AFTER the revert checked for the service's uniqueness rules, the relations by convention and closed
/// days — values the revert itself frees are free (a swap reverts);</item>
/// <item>the step's saves replayed backwards, one <c>SaveChanges</c> each, so the unique indexes see the original
/// intermediate states and never a transient duplicate.</item>
/// </list>
/// Runs under the undo scope of the revert's own step, so the recorder journals the revert like any write.
/// </summary>
internal static class UndoReverter
{
    private static readonly MethodInfo SetMethod =
        typeof(DbContext).GetMethods().Single(m => m.Name == nameof(DbContext.Set) && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);

    private static readonly MethodInfo WhereMethod = typeof(Queryable).GetMethods()
        .First(m => m.Name == nameof(Queryable.Where)
                    && m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2);

    private static readonly MethodInfo AsNoTrackingMethod =
        typeof(EntityFrameworkQueryableExtensions).GetMethod(nameof(EntityFrameworkQueryableExtensions.AsNoTracking))!;

    public static async Task<UndoRevertResult> RevertAsync(
        string eventFolderPath, Guid stepId, string? owner, UndoJournal journal, Action<string>? log,
        CancellationToken cancellationToken)
    {
        switch (journal.Find(eventFolderPath, stepId, owner, out var record))
        {
            case UndoLookup.Evicted:
                return UndoRevertResult.Of(UndoRevertStatus.Evicted);
            case UndoLookup.NotFound:
                return UndoRevertResult.Of(UndoRevertStatus.NotFound);
        }
        if (record!.Reverted)
            return UndoRevertResult.Of(UndoRevertStatus.NotFound);
        if (record.NotUndoable)
            return UndoRevertResult.Of(UndoRevertStatus.NotUndoable);

        var sets = journal.SetsOf(record);
        var rows = NetRows(sets);
        if (rows.Values.All(r => r.Kind == NetKind.None))
            return UndoRevertResult.Of(UndoRevertStatus.Empty);

        await using var db = EventDbContextFactory.Create(eventFolderPath);
        try
        {
            // IMMEDIATE (Microsoft.Data.Sqlite's default): the checks and the writes see one state.
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            var context = new RevertContext(db, rows);

            var conflicts = new List<UndoConflict>();
            foreach (var row in rows.Values)
                CheckAgainstDatabase(context, row, conflicts);
            if (conflicts.Count == 0)
            {
                CheckUniqueness(context, conflicts);
                CheckReferences(context, conflicts);
            }
            if (conflicts.Count > 0)
                return new UndoRevertResult(UndoRevertStatus.Conflict, conflicts, 0, null);

            if (LockedDay(context) is { } lockedDay)
                return new UndoRevertResult(UndoRevertStatus.DayLocked, [], 0, lockedDay);

            if (rows.Values.All(r => r.Skip || r.Kind == NetKind.None))
                return UndoRevertResult.Of(UndoRevertStatus.AlreadyReverted);

            var written = await ReplayBackwardsAsync(context, sets, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            journal.MarkReverted(eventFolderPath, stepId);
            return new UndoRevertResult(UndoRevertStatus.Done, [], written.Count, null, RowIdsOf(context, written));
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 } sqlite)
        {
            // A database constraint refused the replay (a unique index): nothing was written.
            var index = sqlite.Message;
            var colon = index.IndexOf(':');
            if (colon >= 0)
                index = index[(colon + 1)..].Trim().TrimEnd('\'', '.');
            return new UndoRevertResult(UndoRevertStatus.Conflict,
                [new UndoConflict(UndoConflictKind.KeyTaken, Describe.FirstEntity(rows), null, index, null, null)], 0, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Invoke($"Undo: reverting step {stepId} failed — {ex.GetType().Name}: {ex.Message}");
            return UndoRevertResult.Of(UndoRevertStatus.Failed);
        }
    }

    // ── 1. The net effect of the step, per row

    private enum NetKind
    {
        None,
        Modified,
        Added,
        Deleted,
    }

    private sealed class NetRow
    {
        public NetRow(Type type, object[] key, string keyText)
        {
            Type = type;
            Key = key;
            KeyText = keyText;
        }

        public Type Type { get; }

        public object[] Key { get; }

        public string KeyText { get; }

        public bool ExistedBefore { get; set; }

        public bool ExistsAfter { get; set; }

        public bool InitialComplete { get; set; }

        // Values before the step (all of them when InitialComplete) and after it (the touched ones, or all after an add).
        public Dictionary<string, object?> Initial { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, object?> Final { get; } = new(StringComparer.Ordinal);

        public NetKind Kind { get; set; }

        // Modified: properties whose value changed over the step, and which of them are to be put back / already back.
        public HashSet<string> NetProperties { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Revert { get; } = new(StringComparer.Ordinal);

        public HashSet<string> AlreadyBack { get; } = new(StringComparer.Ordinal);

        // Nothing to do for this row (an added row already gone, every property already back).
        public bool Skip { get; set; }

        // The tracked entity the replay writes to.
        public object? Entity { get; set; }

        // The row as it will be after the revert (null values = no row then).
        public bool PredictedExists { get; set; }

        public Dictionary<string, object?> Predicted { get; set; } = new(StringComparer.Ordinal);
    }

    private static Dictionary<(Type, string), NetRow> NetRows(IReadOnlyList<UndoChangeSet> sets)
    {
        var rows = new Dictionary<(Type, string), NetRow>();
        foreach (var set in sets)
        {
            foreach (var change in set.Changes)
            {
                var keyText = KeyText(change.Key);
                if (!rows.TryGetValue((change.EntityType, keyText), out var row))
                {
                    row = new NetRow(change.EntityType, change.Key, keyText) { ExistedBefore = change.Kind != UndoChangeKind.Added };
                    rows[(change.EntityType, keyText)] = row;
                }

                switch (change.Kind)
                {
                    case UndoChangeKind.Added:
                        row.Final.Clear();
                        foreach (var (name, value) in change.After)
                            row.Final[name] = value;
                        row.ExistsAfter = true;
                        break;
                    case UndoChangeKind.Modified:
                        foreach (var (name, value) in change.Before)
                        {
                            if (row.ExistedBefore && !row.InitialComplete && !row.Initial.ContainsKey(name))
                                row.Initial[name] = value;
                        }
                        foreach (var (name, value) in change.After)
                            row.Final[name] = value;
                        row.ExistsAfter = true;
                        break;
                    case UndoChangeKind.Deleted:
                        if (row.ExistedBefore && !row.InitialComplete)
                        {
                            foreach (var (name, value) in change.Before)
                            {
                                if (!row.Initial.ContainsKey(name))
                                    row.Initial[name] = value;
                            }
                            row.InitialComplete = true;
                        }
                        row.Final.Clear();
                        row.ExistsAfter = false;
                        break;
                }
            }
        }

        foreach (var row in rows.Values)
        {
            row.Kind = (row.ExistedBefore, row.ExistsAfter) switch
            {
                (true, true) => NetKind.Modified,
                (false, true) => NetKind.Added,
                (true, false) => NetKind.Deleted,
                _ => NetKind.None,
            };
            if (row.Kind != NetKind.Modified)
                continue;
            foreach (var name in row.Final.Keys)
            {
                if (row.Initial.TryGetValue(name, out var initial) && !Equals(initial, row.Final[name]))
                    row.NetProperties.Add(name);
            }
            if (row.NetProperties.Count == 0)
                row.Kind = NetKind.None;
        }
        return rows;
    }

    // ── 2. The step's rows against the database

    private sealed class RevertContext
    {
        public RevertContext(EventDbContext db, Dictionary<(Type, string), NetRow> rows)
        {
            Db = db;
            Rows = rows;
        }

        public EventDbContext Db { get; }

        public Dictionary<(Type, string), NetRow> Rows { get; }

        public IEntityType EntityType(Type type) => Db.Model.FindEntityType(type)
            ?? throw new InvalidOperationException($"{type.Name} is not an event-database entity.");

        public bool Same(Type type, string property, object? a, object? b)
        {
            var comparer = EntityType(type).FindProperty(property)?.GetValueComparer();
            return comparer?.Equals(a, b) ?? Equals(a, b);
        }

        public object? Find(Type type, object[] key) => Db.Find(type, key);

        // The row as it will be after the revert: a planned row's prediction, else the database.
        public Dictionary<string, object?>? PredictedRow(Type type, object key)
        {
            if (Rows.TryGetValue((type, KeyText([key])), out var row) && !row.Skip && row.Kind != NetKind.None)
                return row.PredictedExists ? row.Predicted : null;
            return Find(type, [key]) is { } entity ? ValuesOf(entity) : null;
        }

        public Dictionary<string, object?> ValuesOf(object entity)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            var entry = Db.Entry(entity);
            foreach (var property in entry.Properties)
                values[property.Metadata.Name] = property.CurrentValue;
            return values;
        }
    }

    private static void CheckAgainstDatabase(RevertContext context, NetRow row, List<UndoConflict> conflicts)
    {
        switch (row.Kind)
        {
            case NetKind.None:
                return;

            case NetKind.Modified:
            {
                var entity = context.Find(row.Type, row.Key);
                if (entity is null)
                {
                    conflicts.Add(Conflict(context, UndoConflictKind.RowDeleted, row, row.Final, null, null, null));
                    return;
                }
                row.Entity = entity;
                var entry = context.Db.Entry(entity);
                foreach (var name in row.NetProperties)
                {
                    var current = entry.Property(name).CurrentValue;
                    if (context.Same(row.Type, name, current, row.Final[name]))
                        row.Revert.Add(name);
                    else if (context.Same(row.Type, name, current, row.Initial[name]))
                        row.AlreadyBack.Add(name);
                    else
                        conflicts.Add(Conflict(context, UndoConflictKind.Changed, row, row.Final, name, row.Final[name], current));
                }
                if (row.Revert.Count == 0)
                {
                    row.Skip = true;
                    return;
                }
                row.Predicted = context.ValuesOf(entity);
                foreach (var name in row.Revert)
                    row.Predicted[name] = row.Initial[name];
                row.PredictedExists = true;
                return;
            }

            case NetKind.Added:
            {
                var entity = context.Find(row.Type, row.Key);
                if (entity is null)
                {
                    // Already deleted by something else: nothing left to take away.
                    row.Skip = true;
                    return;
                }
                row.Entity = entity;
                var entry = context.Db.Entry(entity);
                foreach (var (name, value) in row.Final)
                {
                    if (entry.Metadata.FindProperty(name) is null)
                        continue;
                    var current = entry.Property(name).CurrentValue;
                    if (!context.Same(row.Type, name, current, value))
                        conflicts.Add(Conflict(context, UndoConflictKind.Changed, row, row.Final, name, value, current));
                }
                row.PredictedExists = false;
                return;
            }

            case NetKind.Deleted:
            {
                if (context.Find(row.Type, row.Key) is not null)
                {
                    conflicts.Add(Conflict(context, UndoConflictKind.KeyTaken, row, row.Initial, null, null, null));
                    return;
                }
                row.Predicted = new Dictionary<string, object?>(row.Initial, StringComparer.Ordinal);
                row.PredictedExists = true;
                return;
            }
        }
    }

    // ── 3. The state after the revert

    private static IEnumerable<NetRow> Planned(RevertContext context)
        => context.Rows.Values.Where(r => !r.Skip && r.Kind != NetKind.None);

    private static void CheckUniqueness(RevertContext context, List<UndoConflict> conflicts)
    {
        foreach (var rule in UndoUniqueRules.All)
        {
            var changed = Planned(context)
                .Where(r => r.Type == rule.Entity && r.PredictedExists)
                .Where(r => r.Kind == NetKind.Deleted
                            || r.Revert.Contains(rule.Property)
                            || (rule.ScopeProperty is not null && r.Revert.Contains(rule.ScopeProperty)))
                .ToList();
            if (changed.Count == 0)
                continue;

            // Every row of the table as it will be: the database, with the planned rows in their predicted state.
            var state = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
            var keyProperties = context.EntityType(rule.Entity).FindPrimaryKey()!.Properties;
            foreach (var entity in AllRows(context.Db, rule.Entity))
            {
                var values = ValuesOfDetached(context, rule.Entity, entity);
                state[KeyText(keyProperties.Select(p => values[p.Name]).ToArray())] = values;
            }
            foreach (var row in Planned(context).Where(r => r.Type == rule.Entity))
            {
                if (row.PredictedExists)
                    state[row.KeyText] = row.Predicted;
                else
                    state.Remove(row.KeyText);
            }

            foreach (var row in changed)
            {
                var value = row.Predicted.GetValueOrDefault(rule.Property) as string;
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                var scope = rule.ScopeProperty is null ? null : row.Predicted.GetValueOrDefault(rule.ScopeProperty);
                foreach (var (keyText, other) in state)
                {
                    if (keyText == row.KeyText)
                        continue;
                    if (rule.ScopeProperty is not null && !Equals(other.GetValueOrDefault(rule.ScopeProperty), scope))
                        continue;
                    if (!TextKey.Same(other.GetValueOrDefault(rule.Property) as string, value))
                        continue;
                    conflicts.Add(new UndoConflict(
                        UndoConflictKind.ValueTaken, rule.Entity.Name, Describe.Row(context, rule.Entity, row.Predicted, row.KeyText),
                        rule.Property, value, Describe.Row(context, rule.Entity, other, keyText)));
                    break;
                }
            }
        }
    }

    private static void CheckReferences(RevertContext context, List<UndoConflict> conflicts)
    {
        foreach (var row in Planned(context).ToList())
        {
            if (row.PredictedExists)
            {
                // A row put back or changed must point at rows that exist after the revert.
                foreach (var rule in UndoReferenceRules.All.Where(r => r.Entity == row.Type))
                {
                    if (row.Kind == NetKind.Modified && !row.Revert.Contains(rule.Property))
                        continue;
                    var target = row.Predicted.GetValueOrDefault(rule.Property);
                    if (target is null || target is Guid g && g == Guid.Empty || target is string s && s.Length == 0)
                        continue;
                    if (context.PredictedRow(rule.Target, target) is null)
                        conflicts.Add(new UndoConflict(
                            UndoConflictKind.MissingReference, row.Type.Name, Describe.Row(context, row.Type, row.Predicted, row.KeyText),
                            rule.Property, rule.Target.Name, null));
                }
            }
            else
            {
                // A row the revert deletes must not be needed by rows the step did not make.
                var key = row.Key.Length == 1 ? row.Key[0] : null;
                if (key is null)
                    continue;
                foreach (var rule in UndoReferenceRules.All.Where(r => r.Target == row.Type))
                {
                    var count = 0;
                    var keyProperties = context.EntityType(rule.Entity).FindPrimaryKey()!.Properties;
                    foreach (var entity in RowsWhere(context.Db, rule.Entity, rule.Property, key))
                    {
                        var values = ValuesOfDetached(context, rule.Entity, entity);
                        var keyText = KeyText(keyProperties.Select(p => values[p.Name]).ToArray());
                        if (context.Rows.TryGetValue((rule.Entity, keyText), out var planned) && !planned.Skip && planned.Kind != NetKind.None)
                            continue; // judged by its predicted state below
                        count++;
                    }
                    count += Planned(context).Count(r => r.Type == rule.Entity && r.PredictedExists
                                                         && Equals(r.Predicted.GetValueOrDefault(rule.Property), key));
                    if (count > 0)
                        conflicts.Add(new UndoConflict(
                            UndoConflictKind.StillReferenced, row.Type.Name, Describe.Row(context, row.Type, row.Final, row.KeyText),
                            rule.Entity.Name, null, count.ToString(CultureInfo.InvariantCulture)));
                }
            }
        }
    }

    // The closed day of a row the revert writes (its day now or after the revert), if any.
    private static int? LockedDay(RevertContext context)
    {
        var dayIds = new HashSet<Guid>();
        foreach (var row in Planned(context))
        {
            if (context.EntityType(row.Type).FindProperty(nameof(ParticipantDay.EventDayId)) is null)
                continue;
            if (row.Entity is { } entity && context.Db.Entry(entity).Property(nameof(ParticipantDay.EventDayId)).CurrentValue is Guid current)
                dayIds.Add(current);
            if (row.PredictedExists && row.Predicted.GetValueOrDefault(nameof(ParticipantDay.EventDayId)) is Guid predicted)
                dayIds.Add(predicted);
            if (row.Final.GetValueOrDefault(nameof(ParticipantDay.EventDayId)) is Guid final)
                dayIds.Add(final);
        }
        if (dayIds.Count == 0)
            return null;
        var locked = context.Db.Days.AsNoTracking().Where(d => d.IsLocked).ToList()
            .Where(d => dayIds.Contains(d.Id))
            .OrderBy(d => d.Number)
            .FirstOrDefault();
        return locked?.Number;
    }

    // ── 4. Replay

    private static async Task<List<NetRow>> ReplayBackwardsAsync(
        RevertContext context, IReadOnlyList<UndoChangeSet> sets, CancellationToken cancellationToken)
    {
        var written = new List<NetRow>();
        var db = context.Db;
        for (var s = sets.Count - 1; s >= 0; s--)
        {
            var any = false;
            var changes = sets[s].Changes;
            for (var c = changes.Count - 1; c >= 0; c--)
            {
                var change = changes[c];
                if (!context.Rows.TryGetValue((change.EntityType, KeyText(change.Key)), out var row) || row.Skip || row.Kind == NetKind.None)
                    continue;

                switch (change.Kind)
                {
                    case UndoChangeKind.Modified:
                    {
                        // A row the revert deletes needs no intermediate values; a kept row gets back only the
                        // properties to revert (not those already back, nor those the step left as it found them —
                        // someone may have changed those since).
                        if (row.Kind == NetKind.Added)
                            continue;
                        var entity = row.Entity ??= context.Find(row.Type, row.Key);
                        if (entity is null)
                            continue;
                        var entry = db.Entry(entity);
                        foreach (var (name, before) in change.Before)
                        {
                            if (row.Kind == NetKind.Modified && !row.Revert.Contains(name))
                                continue;
                            if (entry.Metadata.FindProperty(name) is null)
                                continue;
                            entry.Property(name).CurrentValue = before;
                            any = true;
                        }
                        break;
                    }
                    case UndoChangeKind.Added:
                    {
                        var entity = row.Entity ??= context.Find(row.Type, row.Key);
                        if (entity is null)
                            continue;
                        db.Remove(entity);
                        row.Entity = null;
                        any = true;
                        break;
                    }
                    case UndoChangeKind.Deleted:
                    {
                        var entity = Activator.CreateInstance(row.Type)
                                     ?? throw new InvalidOperationException($"Cannot create {row.Type.Name}.");
                        var entry = db.Entry(entity);
                        foreach (var (name, before) in change.Before)
                        {
                            if (entry.Metadata.FindProperty(name) is not null)
                                entry.Property(name).CurrentValue = before;
                        }
                        db.Add(entity);
                        row.Entity = entity;
                        any = true;
                        break;
                    }
                }
                if (!written.Contains(row))
                    written.Add(row);
            }
            if (any)
                await db.SaveChangesAsync(cancellationToken);
        }
        return written;
    }

    private static IReadOnlyList<Guid> RowIdsOf(RevertContext context, List<NetRow> written)
    {
        var ids = new HashSet<Guid>();
        foreach (var row in written)
        {
            foreach (var part in row.Key)
                if (part is Guid g)
                    ids.Add(g);
            var values = row.PredictedExists ? row.Predicted : row.Final;
            if (values.GetValueOrDefault(nameof(ParticipantDay.ParticipantId)) is Guid participant)
                ids.Add(participant);
        }
        return ids.ToList();
    }

    // ── Helpers

    internal static string KeyText(object?[] key)
        => string.Join("|", key.Select(k => Convert.ToString(k, CultureInfo.InvariantCulture)));

    private static IEnumerable<object> AllRows(DbContext db, Type type)
    {
        var set = SetMethod.MakeGenericMethod(type).Invoke(db, null)!;
        var noTracking = AsNoTrackingMethod.MakeGenericMethod(type).Invoke(null, [set])!;
        return ((IEnumerable)noTracking).Cast<object>().ToList();
    }

    private static IEnumerable<object> RowsWhere(DbContext db, Type type, string property, object value)
    {
        var set = SetMethod.MakeGenericMethod(type).Invoke(db, null)!;
        var parameter = Expression.Parameter(type, "e");
        var member = Expression.Property(parameter, property);
        var lambda = Expression.Lambda(Expression.Equal(member, Expression.Constant(value, member.Type)), parameter);
        var filtered = WhereMethod.MakeGenericMethod(type).Invoke(null, [set, lambda])!;
        var noTracking = AsNoTrackingMethod.MakeGenericMethod(type).Invoke(null, [filtered])!;
        return ((IEnumerable)noTracking).Cast<object>().ToList();
    }

    private static Dictionary<string, object?> ValuesOfDetached(RevertContext context, Type type, object entity)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in context.EntityType(type).GetProperties())
            if (property.PropertyInfo is { } info)
                values[property.Name] = info.GetValue(entity);
        return values;
    }

    private static UndoConflict Conflict(
        RevertContext context, UndoConflictKind kind, NetRow row, Dictionary<string, object?> values, string? field,
        object? expected, object? actual)
    {
        var merged = row.Initial.Count > 0 ? new Dictionary<string, object?>(row.Initial, StringComparer.Ordinal) : new(StringComparer.Ordinal);
        foreach (var (name, value) in values)
            merged[name] = value;
        if (row.Entity is { } entity)
            foreach (var (name, value) in context.ValuesOf(entity))
                merged.TryAdd(name, value);
        return new UndoConflict(
            kind, row.Type.Name, Describe.Row(context, row.Type, merged, row.KeyText), field,
            field is null ? null : Describe.Value(context, row.Type, field, expected),
            field is null ? null : Describe.Value(context, row.Type, field, actual));
    }

    /// <summary>Readable names of rows and values for the conflict list (the client words them).</summary>
    private static class Describe
    {
        private static readonly string[] NameProperties = ["FullName", "Name", "Label", "Code", "Number", "Note"];

        public static string FirstEntity(Dictionary<(Type, string), NetRow> rows)
            => rows.Values.FirstOrDefault(r => r.Kind != NetKind.None)?.Type.Name ?? string.Empty;

        public static string Row(RevertContext context, Type type, IReadOnlyDictionary<string, object?> values, string keyText)
        {
            if (type != typeof(Participant) && values.GetValueOrDefault(nameof(ParticipantDay.ParticipantId)) is Guid participantId
                && context.PredictedRow(typeof(Participant), participantId) is { } participant
                && participant.GetValueOrDefault(nameof(Participant.FullName)) is string fullName && fullName.Length > 0)
                return fullName;
            if (type == typeof(RelayTeam))
            {
                if (values.GetValueOrDefault(nameof(RelayTeam.Number)) is int number)
                    return number.ToString(CultureInfo.InvariantCulture);
                if (values.GetValueOrDefault(nameof(RelayTeam.Name)) is string teamName && teamName.Length > 0)
                    return teamName;
            }
            if (type == typeof(LegCourseAssignment)
                && values.GetValueOrDefault(nameof(LegCourseAssignment.TeamNumber)) is int team
                && values.GetValueOrDefault(nameof(LegCourseAssignment.Leg)) is int leg)
                return $"{team}.{leg}";
            if (type == typeof(EventDay) && values.GetValueOrDefault(nameof(EventDay.Number)) is int day)
                return day.ToString(CultureInfo.InvariantCulture);
            foreach (var name in NameProperties)
            {
                var text = Convert.ToString(values.GetValueOrDefault(name), CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text))
                    return text.Trim();
            }
            return keyText;
        }

        public static string? Value(RevertContext context, Type type, string property, object? value)
        {
            if (value is null)
                return null;
            if (UndoReferenceRules.Find(type, property) is { } rule && value is not string { Length: 0 })
            {
                try
                {
                    return context.PredictedRow(rule.Target, value) is { } target
                        ? Row(context, rule.Target, target, KeyText([value]))
                        : null;
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    return null;
                }
            }
            return value switch
            {
                string s => s,
                bool b => b ? "true" : "false",
                DateTimeOffset d => d.TimeOfDay == TimeSpan.Zero
                    ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
                    : d.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
                TimeSpan t => t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
                Enum e => e.ToString(),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };
        }
    }
}
