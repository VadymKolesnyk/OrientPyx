using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Holds the active competition + day in-memory for the running instance. The shared app
/// database is only used to remember/restore the last selection across launches — runtime
/// state is never driven by it, so concurrent instances don't overwrite each other.
/// </summary>
public interface ISessionService
{
    EventSummary? CurrentEvent { get; }
    EventDay? CurrentDay { get; }
    bool HasSelection { get; }

    event EventHandler? SessionChanged;

    /// <summary>Sets the active selection (in-memory) and persists it as the last session.</summary>
    Task SelectAsync(EventSummary competition, EventDay day, CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches the active day within the current competition (in-memory) and persists it as
    /// the last session. No-op when there is no current selection.
    /// </summary>
    Task SetCurrentDayAsync(EventDay day, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the in-memory competition summary after its metadata was edited, so the
    /// window title / context reflect the new name. Raises <see cref="SessionChanged"/>.
    /// </summary>
    void UpdateCurrentEvent(EventSummary competition);

    /// <summary>Clears the active selection (does not erase the persisted last session).</summary>
    void Clear();

    /// <summary>
    /// Attempts to restore the last selection from the app database, verifying that the
    /// competition folder and day still exist. Returns true if a selection was restored.
    /// </summary>
    Task<bool> TryRestoreLastAsync(CancellationToken cancellationToken = default);
}
