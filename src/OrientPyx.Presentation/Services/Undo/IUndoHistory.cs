using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Services.Undo;

/// <summary>
/// The undo / redo stacks of the running app, one pair per (page, day picked on it) — <see cref="UndoHistoryKey"/>.
/// Pages capture a step synchronously when a value changes (<see cref="Capture"/>) and run the write through
/// <see cref="RunAsync(UndoEntry?, Func{Task})"/>, which enters the step for the async call; Ctrl+Z / Ctrl+Y and the
/// «Правка» menu call <see cref="UndoAsync"/> / <see cref="RedoAsync"/> for the shown page. Cleared when the
/// competition or the role changes, and on a station when its host restarts. UI thread only.
/// </summary>
public interface IUndoHistory
{
    /// <summary>Raised whenever a stack changes (menus refresh their labels).</summary>
    event EventHandler? Changed;

    /// <summary>
    /// The step the change being made right now belongs to — the open gesture's or edit session's, else a new one
    /// labelled <paramref name="label"/> on the page's current history. Null while program-made changes are applied
    /// (<see cref="UndoGesture.Programmatic"/>): their write then runs outside every step.
    /// </summary>
    UndoEntry? Capture(PageViewModelBase page, string label);

    /// <summary>The same for an explicit history (a dialog writing into the participants page of its own day).</summary>
    UndoEntry? Capture(UndoHistoryKey key, string label);

    /// <summary>Runs one write of the step (null ⇒ outside every step); the step stays open just for this call.</summary>
    Task RunAsync(UndoEntry? step, Func<Task> write);

    /// <inheritdoc cref="RunAsync(UndoEntry?, Func{Task})"/>
    Task<T> RunAsync<T>(UndoEntry? step, Func<Task<T>> write);

    /// <summary>The label of the step Ctrl+Z would undo on the page, or null when there is none.</summary>
    string? UndoLabel(PageViewModelBase? page);

    /// <summary>The label of the step Ctrl+Y would redo on the page, or null when there is none.</summary>
    string? RedoLabel(PageViewModelBase? page);

    /// <summary>
    /// Undoes the page's last step: ends a live cell edit (a text one only when <paramref name="includeTextEdit"/>),
    /// waits for the page's pending writes, reverts, reloads the page under the busy overlay, toasts the outcome.
    /// A second call while one runs is ignored.
    /// </summary>
    Task UndoAsync(PageViewModelBase page, bool includeTextEdit = false);

    /// <summary>Redoes the page's last undone step (see <see cref="UndoAsync"/>).</summary>
    Task RedoAsync(PageViewModelBase page, bool includeTextEdit = false);

    /// <summary>Drops the histories of a deleted day.</summary>
    void ForgetDay(Guid dayId);

    /// <summary>Drops every history.</summary>
    void Clear();
}
