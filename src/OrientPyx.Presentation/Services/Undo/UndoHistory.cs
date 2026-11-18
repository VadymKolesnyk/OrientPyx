using Avalonia.Threading;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services.Undo;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Services.Undo;

/// <inheritdoc />
public sealed class UndoHistory : IUndoHistory
{
    /// <summary>Steps kept per history (page + day); older ones fall off.</summary>
    public const int Depth = 100;

    // Conflict lines listed in the toast before «і ще N».
    private const int ShownConflicts = 5;

    private readonly IEditHistoryService _history;
    private readonly IBusyService _busy;
    private readonly IToastService _toasts;
    private readonly ILocalizationService _localization;
    private readonly IActivityLog? _log;
    private readonly ISessionService _session;
    private readonly IWorkspaceMode _mode;
    private readonly Dictionary<UndoHistoryKey, Stacks> _stacks = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _eventPath;
    private WorkspaceRole _role;

    public UndoHistory(
        IEditHistoryService history,
        IBusyService busy,
        IToastService toasts,
        ILocalizationService localization,
        ISessionService session,
        IWorkspaceMode mode,
        INetworkClient? network = null,
        IActivityLog? log = null)
    {
        _history = history;
        _busy = busy;
        _toasts = toasts;
        _localization = localization;
        _session = session;
        _mode = mode;
        _log = log;
        _eventPath = session.CurrentEvent?.FolderPath;
        _role = mode.Role;

        // Another competition, another role (local / host / station) or a restarted host: the journal the steps point
        // at is not the one they were made in. A day switch keeps the histories (each day has its own).
        session.SessionChanged += (_, _) => OnUi(() =>
        {
            var path = _session.CurrentEvent?.FolderPath;
            if (!string.Equals(path, _eventPath, StringComparison.OrdinalIgnoreCase))
            {
                _eventPath = path;
                Clear();
            }
        });
        mode.Changed += (_, _) => OnUi(() =>
        {
            if (_mode.Role != _role)
            {
                _role = _mode.Role;
                Clear();
            }
        });
        if (network is not null)
            network.HostRestarted += (_, _) => OnUi(Clear);
    }

    public event EventHandler? Changed;

    private sealed class Stacks
    {
        public List<UndoEntry> Undo { get; } = [];

        public List<UndoEntry> Redo { get; } = [];
    }

    // ── Capture / run

    public UndoEntry? Capture(PageViewModelBase page, string label)
        => page.SupportsUndo ? Capture(page.CurrentUndoKey, label) : null;

    public UndoEntry? Capture(UndoHistoryKey key, string label)
    {
        if (UndoGesture.IsProgrammatic)
            return null;
        var slot = UndoGesture.CurrentSlot;
        if (slot?.Entry is { Closed: false } joined && joined.Key == key)
            return joined;

        var entry = new UndoEntry(Guid.NewGuid(), string.IsNullOrWhiteSpace(slot?.Label) ? label : slot!.Label!, key);
        var stacks = StacksOf(key);
        stacks.Undo.Add(entry);
        if (stacks.Undo.Count > Depth)
            stacks.Undo.RemoveAt(0);
        // A new edit forgets what was undone before it (У-5).
        stacks.Redo.Clear();
        if (slot is not null)
            slot.Entry = entry;
        RaiseChanged();
        return entry;
    }

    public Task RunAsync(UndoEntry? step, Func<Task> write)
    {
        if (step is null)
            return RunOutsideAsync(write);
        var task = RunInStepAsync(step, write);
        step.Track(task);
        return task;
    }

    public Task<T> RunAsync<T>(UndoEntry? step, Func<Task<T>> write)
    {
        if (step is null)
            return RunOutsideAsync(write);
        var task = RunInStepAsync(step, write);
        step.Track(task);
        return task;
    }

    // Async methods, so the scope set inside never leaks back into the caller's flow.
    private static async Task RunInStepAsync(UndoEntry step, Func<Task> write)
    {
        using (UndoScope.Enter(step.StepId))
        {
            await write();
            step.Succeeded = true;
        }
    }

    private static async Task<T> RunInStepAsync<T>(UndoEntry step, Func<Task<T>> write)
    {
        using (UndoScope.Enter(step.StepId))
        {
            var result = await write();
            step.Succeeded = true;
            return result;
        }
    }

    private static async Task RunOutsideAsync(Func<Task> write)
    {
        using (UndoScope.Suppress())
            await write();
    }

    private static async Task<T> RunOutsideAsync<T>(Func<Task<T>> write)
    {
        using (UndoScope.Suppress())
            return await write();
    }

    // ── Menus

    public string? UndoLabel(PageViewModelBase? page)
        => page is { SupportsUndo: true } && _stacks.TryGetValue(page.CurrentUndoKey, out var s) && s.Undo.Count > 0 ? s.Undo[^1].Label : null;

    public string? RedoLabel(PageViewModelBase? page)
        => page is { SupportsUndo: true } && _stacks.TryGetValue(page.CurrentUndoKey, out var s) && s.Redo.Count > 0 ? s.Redo[^1].Label : null;

    // ── Undo / redo

    public Task UndoAsync(PageViewModelBase page, bool includeTextEdit = false) => StepAsync(page, undo: true, includeTextEdit);

    public Task RedoAsync(PageViewModelBase page, bool includeTextEdit = false) => StepAsync(page, undo: false, includeTextEdit);

    private async Task StepAsync(PageViewModelBase page, bool undo, bool includeTextEdit)
    {
        if (!page.SupportsUndo)
            return;
        // A press while the previous undo/redo still runs (a held key's auto-repeat too) is dropped, not queued (У-18).
        if (!_gate.Wait(0))
            return;
        try
        {
            // A live edit ends first, as Enter would (its value stays and becomes part of what is undone, У-1)…
            UndoGesture.CommitLiveSession(includeTextEdit);
            // …and the page's debounced writes land, so what is undone is what is saved (У-6).
            await page.FlushPendingWritesAsync();

            var key = page.CurrentUndoKey;
            while (true)
            {
                if (!_stacks.TryGetValue(key, out var stacks))
                    return;
                var from = undo ? stacks.Undo : stacks.Redo;
                if (from.Count == 0)
                    return;
                var entry = from[^1];
                await entry.WhenIdleAsync();
                if (!entry.Succeeded)
                {
                    // Nothing of it reached the store (Escape, a cancelled debounce, a failed write): skip silently (У-15).
                    from.Remove(entry);
                    RaiseChanged();
                    continue;
                }

                // A quick pre-check; the revert itself checks the day of every row in its transaction (У-11).
                if (!await page.EnsureDayEditableForActionAsync())
                    return;

                UndoOutcome? outcome = null;
                try
                {
                    await _busy.RunAsync(async () =>
                    {
                        outcome = await _history.RevertAsync(entry.StepId);
                        // Reloaded under the same overlay: once it is gone the rows already show what was put back, so an
                        // edit of the same row cannot write a stale row back over the revert (§9.5). The busy service runs
                        // this body on the thread pool, and a page reload rebuilds bound collections and row view models —
                        // so it goes back to the UI thread (still inside the overlay).
                        if (outcome.Result.Status == UndoRevertStatus.Done)
                            await Dispatcher.UIThread.InvokeAsync(page.ReloadForUndoAsync);
                    });
                }
                catch (Exception ex)
                {
                    // E.g. the host did not answer: the step stays, the operator can try again.
                    _log?.Error($"Undo: {(undo ? "undo" : "redo")} of «{entry.Label}»", ex);
                    Toast(undo ? "Undo.Toast.UndoFailed.Title" : "Undo.Toast.RedoFailed.Title",
                        string.Format(_localization.Get("Undo.Toast.Failed.Message"), entry.Label));
                    return;
                }

                if (outcome is null)
                    return;
                if (Handle(page, stacks, entry, undo, outcome))
                    return;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // Applies the outcome to the stacks and tells the operator. False ⇒ go on to the next step (an empty one).
    private bool Handle(PageViewModelBase page, Stacks stacks, UndoEntry entry, bool undo, UndoOutcome outcome)
    {
        var from = undo ? stacks.Undo : stacks.Redo;
        var to = undo ? stacks.Redo : stacks.Undo;
        var result = outcome.Result;
        switch (result.Status)
        {
            case UndoRevertStatus.Done:
            {
                from.Remove(entry);
                entry.Closed = true;
                // The revert is a step of its own: undoing it again is the redo (and the other way round).
                var back = new UndoEntry(outcome.RevertStepId, entry.Label, entry.Key) { Succeeded = true };
                to.Add(back);
                if (to.Count > Depth)
                    to.RemoveAt(0);
                RaiseChanged();
                _log?.Action($"{(undo ? "Undo" : "Redo")}: {entry.Label} ({result.ChangedRows} rows)");
                Toast(undo ? "Undo.Toast.Undone.Title" : "Undo.Toast.Redone.Title", entry.Label);
                page.OnUndoApplied(result);
                return true;
            }

            case UndoRevertStatus.Empty:
            case UndoRevertStatus.NotFound:
                from.Remove(entry);
                RaiseChanged();
                return false;

            case UndoRevertStatus.AlreadyReverted:
                from.Remove(entry);
                RaiseChanged();
                Toast("Undo.Toast.AlreadyReverted.Title", entry.Label);
                return true;

            case UndoRevertStatus.Conflict:
                from.Remove(entry);
                RaiseChanged();
                _log?.Action($"{(undo ? "Undo" : "Redo")} refused (conflict): {entry.Label}");
                Toast(undo ? "Undo.Toast.Conflict.UndoTitle" : "Undo.Toast.Conflict.RedoTitle",
                    string.Format(_localization.Get("Undo.Toast.Conflict.Message"), entry.Label, ConflictLines(result.Conflicts)));
                return true;

            case UndoRevertStatus.DayLocked:
                // Stays in the stack: once the day is opened again it can be undone (У-11).
                Toast(undo ? "Undo.Toast.UndoFailed.Title" : "Undo.Toast.RedoFailed.Title",
                    string.Format(_localization.Get("Undo.Toast.DayLocked.Message"), result.LockedDayNumber, entry.Label));
                return true;

            case UndoRevertStatus.NotUndoable:
                from.Remove(entry);
                RaiseChanged();
                Toast(undo ? "Undo.Toast.UndoFailed.Title" : "Undo.Toast.RedoFailed.Title",
                    string.Format(_localization.Get("Undo.Toast.NotUndoable.Message"), entry.Label));
                return true;

            case UndoRevertStatus.Evicted:
                // Everything below is older still: the whole history of this page and day is gone (У-12).
                stacks.Undo.Clear();
                stacks.Redo.Clear();
                RaiseChanged();
                Toast(undo ? "Undo.Toast.UndoFailed.Title" : "Undo.Toast.RedoFailed.Title", _localization.Get("Undo.Toast.Evicted.Message"));
                return true;

            default:
                from.Remove(entry);
                RaiseChanged();
                Toast(undo ? "Undo.Toast.UndoFailed.Title" : "Undo.Toast.RedoFailed.Title",
                    string.Format(_localization.Get("Undo.Toast.Failed.Message"), entry.Label));
                return true;
        }
    }

    // ── Housekeeping

    public void ForgetDay(Guid dayId)
    {
        var removed = false;
        foreach (var key in _stacks.Keys.Where(k => k.DayId == dayId).ToList())
            removed |= _stacks.Remove(key);
        if (removed)
            RaiseChanged();
    }

    public void Clear()
    {
        if (_stacks.Count == 0)
            return;
        _stacks.Clear();
        RaiseChanged();
    }

    private Stacks StacksOf(UndoHistoryKey key)
    {
        if (!_stacks.TryGetValue(key, out var stacks))
        {
            stacks = new Stacks();
            _stacks[key] = stacks;
        }
        return stacks;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void Toast(string titleKey, string message) => _toasts.Show(_localization.Get(titleKey), message);

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    // ── Conflict text (worded here, in this machine's language — the store only returns data)

    private string ConflictLines(IReadOnlyList<UndoConflict> conflicts)
    {
        var lines = conflicts.Take(ShownConflicts).Select(Describe).ToList();
        if (conflicts.Count > ShownConflicts)
            lines.Add(string.Format(_localization.Get("Undo.Toast.More"), conflicts.Count - ShownConflicts));
        return string.Join(Environment.NewLine, lines);
    }

    private string Describe(UndoConflict conflict)
    {
        var row = RowName(conflict);
        switch (conflict.Kind)
        {
            case UndoConflictKind.Changed:
                return conflict.Actual is null or ""
                    ? string.Format(_localization.Get("Undo.Conflict.ChangedEmpty"), row, Field(conflict.Entity, conflict.Field))
                    : string.Format(_localization.Get("Undo.Conflict.Changed"), row, Field(conflict.Entity, conflict.Field), Value(conflict.Actual));
            case UndoConflictKind.RowDeleted:
                return string.Format(_localization.Get("Undo.Conflict.RowDeleted"), row);
            case UndoConflictKind.KeyTaken:
                return conflict.Field is { Length: > 0 } index && conflict.Row is null
                    ? string.Format(_localization.Get("Undo.Conflict.KeyTakenIndex"), index)
                    : string.Format(_localization.Get("Undo.Conflict.KeyTaken"), row);
            case UndoConflictKind.ValueTaken:
                return string.Format(_localization.Get("Undo.Conflict.ValueTaken"),
                    row, Field(conflict.Entity, conflict.Field), conflict.Expected, conflict.Actual);
            case UndoConflictKind.MissingReference:
                return string.Format(_localization.Get("Undo.Conflict.MissingReference"),
                    row, Known($"Undo.Missing.{conflict.Expected}") ?? Field(conflict.Entity, conflict.Field));
            case UndoConflictKind.StillReferenced:
                return string.Format(_localization.Get("Undo.Conflict.StillReferenced"),
                    Known($"Undo.Entity.{conflict.Entity}") ?? conflict.Entity, conflict.Row, conflict.Actual,
                    Known($"Undo.Rows.{conflict.Field}") ?? conflict.Field);
            default:
                return row;
        }
    }

    private string RowName(UndoConflict conflict)
    {
        if (conflict.Entity == "RelayTeam" && conflict.Row is { Length: > 0 } team)
            return string.Format(_localization.Get("Undo.Row.RelayTeam"), team);
        return conflict.Row is { Length: > 0 } row ? row : Known($"Undo.Entity.{conflict.Entity}") ?? conflict.Entity;
    }

    private string Field(string entity, string? field)
        => field is null ? string.Empty : Known($"Undo.Field.{entity}.{field}") ?? field;

    private string Value(string? value) => value switch
    {
        "true" => _localization.Get("Undo.Value.True"),
        "false" => _localization.Get("Undo.Value.False"),
        _ => value ?? string.Empty,
    };

    // The translation of a key, or null when there is none (Get returns the key itself then).
    private string? Known(string key)
    {
        var text = _localization.Get(key);
        return string.Equals(text, key, StringComparison.Ordinal) ? null : text;
    }
}
