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

    /// <summary>
    /// Replaces the in-memory competition after its identifier (and folder) changed, re-points the
    /// diagnostic log at the new folder, and rewrites the last-session pointer so a restart still
    /// finds the competition under its new name. No-op when there is no current selection.
    /// </summary>
    Task RenameCurrentEventAsync(EventSummary competition, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the active day is closed for editing (<see cref="EventDay.IsLocked"/>). False when
    /// no day is selected. The UI reads this to grey out editing; the actual guarantee is enforced in
    /// <c>ICompetitionEditorService</c>.
    /// </summary>
    bool IsCurrentDayLocked { get; }

    /// <summary>
    /// Replaces the in-memory current day after its own row changed (e.g. it was locked or unlocked),
    /// so pages reading <see cref="CurrentDay"/> see the new state. Ignores a day that is not the
    /// current one. Raises <see cref="SessionChanged"/>.
    /// </summary>
    void UpdateCurrentDay(EventDay day);

    /// <summary>Clears the active selection (does not erase the persisted last session).</summary>
    void Clear();

    /// <summary>
    /// Attempts to restore the last selection from the app database, verifying that the
    /// competition folder and day still exist. Returns true if a selection was restored.
    /// </summary>
    Task<bool> TryRestoreLastAsync(CancellationToken cancellationToken = default);
}
