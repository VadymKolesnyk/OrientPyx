namespace OrientPyx.Presentation.Services.Undo;

/// <summary>Which undo history a step belongs to: a page and the day picked on it (null for a page without days and
/// for the participants roster).</summary>
public readonly record struct UndoHistoryKey(string Page, Guid? DayId);

/// <summary>
/// One step in an undo or redo stack: the step id the writes ran under, its label and the writes still running. Holds
/// no row view model (rows are recycled and must be collectable) — only the id and the text.
/// </summary>
public sealed class UndoEntry
{
    private readonly object _gate = new();
    private readonly List<Task> _running = [];
    private bool _succeeded;

    internal UndoEntry(Guid stepId, string label, UndoHistoryKey key)
    {
        StepId = stepId;
        Label = label;
        Key = key;
    }

    /// <summary>The id the step's writes ran under (the journal's key).</summary>
    public Guid StepId { get; }

    /// <summary>What the step did, for the menu and the toasts («Чіп — Іваненко Іван»).</summary>
    public string Label { get; }

    public UndoHistoryKey Key { get; }

    /// <summary>
    /// The step has been undone or redone: a later change in the same gesture or edit session gets a new step (a new
    /// position in the history) instead of joining this one.
    /// </summary>
    public bool Closed { get; internal set; }

    /// <summary>At least one write of the step ran to its end (a step with none is empty and skipped silently).</summary>
    public bool Succeeded
    {
        get
        {
            lock (_gate)
                return _succeeded;
        }
        internal set
        {
            lock (_gate)
                _succeeded = value;
        }
    }

    internal void Track(Task write)
    {
        lock (_gate)
        {
            _running.RemoveAll(t => t.IsCompleted);
            if (!write.IsCompleted)
                _running.Add(write);
        }
    }

    /// <summary>Completes once none of the step's writes is still running.</summary>
    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] running;
            lock (_gate)
            {
                _running.RemoveAll(t => t.IsCompleted);
                running = _running.ToArray();
            }
            if (running.Length == 0)
                return;
            try
            {
                await Task.WhenAll(running);
            }
            catch
            {
                // A failed write is still a finished one.
            }
        }
    }
}
