namespace OrientPyx.DataAccess.Persistence.Undo;

/// <summary>What one journalled row change did.</summary>
internal enum UndoChangeKind
{
    Added,
    Modified,
    Deleted,
}

/// <summary>
/// One row changed by one <c>SaveChanges</c>: its CLR type, primary key (in model order) and the values — the changed
/// properties for <see cref="UndoChangeKind.Modified"/>, every value for an added (<see cref="After"/>) or deleted
/// (<see cref="Before"/>) row. Values are the model's CLR values (before converters).
/// </summary>
internal sealed class UndoEntityChange
{
    public required Type EntityType { get; init; }

    public required object[] Key { get; init; }

    public required UndoChangeKind Kind { get; init; }

    public Dictionary<string, object?> Before { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, object?> After { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>The rows one <c>SaveChanges</c> changed, in the order EF reported them.</summary>
internal sealed class UndoChangeSet
{
    public UndoChangeSet(IReadOnlyList<UndoEntityChange> changes) => Changes = changes;

    public IReadOnlyList<UndoEntityChange> Changes { get; }
}

/// <summary>One journalled undo step of one owner on one competition.</summary>
internal sealed class UndoStepRecord
{
    public UndoStepRecord(string path, Guid stepId, string owner)
    {
        Path = path;
        StepId = stepId;
        Owner = owner;
    }

    public string Path { get; }

    public Guid StepId { get; }

    public string Owner { get; }

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;

    public List<UndoChangeSet> Sets { get; } = [];

    public bool NotUndoable { get; set; }

    public bool Reverted { get; set; }

    public int ChangeCount { get; set; }

    // The order in which the record entered its owner's list (eviction goes by it).
    internal long Sequence { get; set; }
}

/// <summary>What the journal knows about a step.</summary>
internal enum UndoLookup
{
    Found,
    NotFound,
    Evicted,
}

/// <summary>
/// The in-memory undo journal of the process that owns the event databases: every step's row changes, keyed by
/// (competition folder, step id), with its owner (a station's token on a host, empty = this machine). Thread-safe.
/// Limits per instance (tests use their own); the app uses <see cref="Shared"/>, since the context factory is static.
/// </summary>
internal sealed class UndoJournal
{
    /// <summary>The process-wide journal.</summary>
    public static UndoJournal Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<(string Path, Guid Step), UndoStepRecord> _steps = new();
    private readonly Dictionary<(string Path, string Owner), List<UndoStepRecord>> _byOwner = new();
    private readonly Dictionary<(string Path, string Owner), (HashSet<Guid> Set, Queue<Guid> Order)> _evicted = new();
    private long _sequence;
    private int _totalChanges;

    /// <summary>Steps kept per (competition, owner).</summary>
    public int MaxStepsPerOwner { get; init; } = 200;

    /// <summary>Row changes kept over all steps.</summary>
    public int MaxChanges { get; init; } = 200_000;

    /// <summary>A step past this many row changes becomes not undoable (and keeps none).</summary>
    public int MaxChangesPerStep { get; init; } = 20_000;

    /// <summary>Ids of evicted steps remembered per owner (to tell «evicted» from «never seen»).</summary>
    public int MaxEvictedRemembered { get; init; } = 10_000;

    /// <summary>Total row changes held (for tests).</summary>
    public int TotalChanges
    {
        get
        {
            lock (_gate)
                return _totalChanges;
        }
    }

    /// <summary>Registers a step before its first write, so a step that ends up changing nothing is «empty», not «unknown».</summary>
    public void Touch(string path, Guid stepId, string? owner)
    {
        lock (_gate)
            GetOrAdd(Normalize(path), stepId, owner ?? string.Empty);
    }

    /// <summary>Appends one committed <c>SaveChanges</c> to its step.</summary>
    public void Append(string path, Guid stepId, string? owner, UndoChangeSet set)
    {
        if (set.Changes.Count == 0)
            return;
        lock (_gate)
        {
            var record = GetOrAdd(Normalize(path), stepId, owner ?? string.Empty);
            if (record is null || record.Reverted || record.NotUndoable)
                return;
            if (record.ChangeCount + set.Changes.Count > MaxChangesPerStep)
            {
                MarkNotUndoableLocked(record);
                return;
            }
            record.Sets.Add(set);
            record.ChangeCount += set.Changes.Count;
            _totalChanges += set.Changes.Count;
            TrimTotalLocked();
        }
    }

    /// <summary>The step made a write the journal cannot see (a bulk statement): it can never be reverted.</summary>
    public void MarkNotUndoable(string path, Guid stepId, string? owner)
    {
        lock (_gate)
        {
            if (GetOrAdd(Normalize(path), stepId, owner ?? string.Empty) is { } record)
                MarkNotUndoableLocked(record);
        }
    }

    /// <summary>The step has been reverted: changes arriving under it later are not added to it.</summary>
    public void MarkReverted(string path, Guid stepId)
    {
        lock (_gate)
        {
            if (_steps.TryGetValue((Normalize(path), stepId), out var record))
                record.Reverted = true;
        }
    }

    /// <summary>True when the step is known and already reverted (the recorder keeps later writes out of it).</summary>
    public bool IsReverted(string path, Guid stepId)
    {
        lock (_gate)
            return _steps.TryGetValue((Normalize(path), stepId), out var record) && record.Reverted;
    }

    /// <summary>Finds a step of <paramref name="owner"/>: another owner's step reads as not found.</summary>
    public UndoLookup Find(string path, Guid stepId, string? owner, out UndoStepRecord? record)
    {
        var key = Normalize(path);
        var who = owner ?? string.Empty;
        lock (_gate)
        {
            if (_steps.TryGetValue((key, stepId), out var found) && found.Owner == who)
            {
                record = found;
                return UndoLookup.Found;
            }
            record = null;
            if (_evicted.TryGetValue((key, who), out var evicted) && evicted.Set.Contains(stepId))
                return UndoLookup.Evicted;
            return UndoLookup.NotFound;
        }
    }

    /// <summary>A snapshot of the step's change sets (the record keeps growing under its lock otherwise).</summary>
    public IReadOnlyList<UndoChangeSet> SetsOf(UndoStepRecord record)
    {
        lock (_gate)
            return record.Sets.ToList();
    }

    /// <summary>Drops everything journalled for a competition (its folder is renamed, deleted or replaced).</summary>
    public void Forget(string path)
    {
        var key = Normalize(path);
        lock (_gate)
        {
            foreach (var step in _steps.Where(kv => kv.Key.Path == key).Select(kv => kv.Value).ToList())
            {
                _totalChanges -= step.ChangeCount;
                _steps.Remove((key, step.StepId));
            }
            foreach (var owner in _byOwner.Keys.Where(k => k.Path == key).ToList())
                _byOwner.Remove(owner);
            foreach (var owner in _evicted.Keys.Where(k => k.Path == key).ToList())
                _evicted.Remove(owner);
        }
    }

    /// <summary>The number of steps held for an owner (for tests).</summary>
    public int StepCount(string path, string? owner)
    {
        lock (_gate)
            return _byOwner.TryGetValue((Normalize(path), owner ?? string.Empty), out var list) ? list.Count : 0;
    }

    // A record for (path, step); null when the step id is already used by another owner (never shared).
    private UndoStepRecord? GetOrAdd(string path, Guid stepId, string owner)
    {
        if (_steps.TryGetValue((path, stepId), out var existing))
            return existing.Owner == owner ? existing : null;

        var record = new UndoStepRecord(path, stepId, owner) { Sequence = ++_sequence };
        _steps[(path, stepId)] = record;
        if (!_byOwner.TryGetValue((path, owner), out var list))
        {
            list = [];
            _byOwner[(path, owner)] = list;
        }
        list.Add(record);
        if (list.Count > MaxStepsPerOwner)
        {
            // The oldest empty step goes first (registered by Touch, nothing written), else the oldest one. A not-undoable
            // step keeps no changes either, but it is not empty (its revert says so), so it waits for its turn by age.
            var victim = list.Where(r => IsEmpty(r) && r != record).OrderBy(r => r.Sequence).FirstOrDefault()
                         ?? list[0];
            EvictLocked(victim);
        }
        return record;
    }

    private static bool IsEmpty(UndoStepRecord record) => record.ChangeCount == 0 && !record.NotUndoable;

    private void MarkNotUndoableLocked(UndoStepRecord record)
    {
        if (!record.NotUndoable)
        {
            record.NotUndoable = true;
            _totalChanges -= record.ChangeCount;
            record.ChangeCount = 0;
            record.Sets.Clear();
        }
    }

    // Past the global limit the oldest steps of all owners go (empty ones hold nothing, so they never help).
    private void TrimTotalLocked()
    {
        while (_totalChanges > MaxChanges)
        {
            var victim = _steps.Values.Where(r => r.ChangeCount > 0).OrderBy(r => r.Sequence).FirstOrDefault();
            if (victim is null)
                return;
            EvictLocked(victim);
        }
    }

    private void EvictLocked(UndoStepRecord victim)
    {
        _steps.Remove((victim.Path, victim.StepId));
        _totalChanges -= victim.ChangeCount;
        if (_byOwner.TryGetValue((victim.Path, victim.Owner), out var list))
            list.Remove(victim);

        // «Evicted» tells the client its history is lost from there on down (everything older went first). An empty step
        // is evicted out of turn and held nothing, so it is simply forgotten: it then reads as never seen, which the
        // client skips silently like an empty one — instead of dropping the older steps still in the journal.
        if (IsEmpty(victim))
            return;

        if (!_evicted.TryGetValue((victim.Path, victim.Owner), out var evicted))
        {
            evicted = (new HashSet<Guid>(), new Queue<Guid>());
            _evicted[(victim.Path, victim.Owner)] = evicted;
        }
        if (evicted.Set.Add(victim.StepId))
            evicted.Order.Enqueue(victim.StepId);
        while (evicted.Order.Count > MaxEvictedRemembered)
            evicted.Set.Remove(evicted.Order.Dequeue());
    }

    private static string Normalize(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path).TrimEnd('/', '\\').ToUpperInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            return path.TrimEnd('/', '\\').ToUpperInvariant();
        }
    }
}
