using System.Collections.ObjectModel;
using System.ComponentModel;

namespace OrientDesk.Presentation.Services;

/// <summary>
/// Registry of the background processes currently running in the app, surfaced by the top-bar activity
/// block. Singleton: any feature that starts a long-running process (chip auto-read today; finish-line
/// reading, online-results push later) registers an <see cref="IBackgroundActivity"/> here while it
/// runs and unregisters when it stops. The block binds to <see cref="Activities"/> and the count, so
/// it stays generic — adding a new process never touches the block (open/closed).
/// </summary>
public interface IBackgroundActivityService : INotifyPropertyChanged
{
    /// <summary>The processes currently running (or paused). Empty when nothing is going on.</summary>
    ReadOnlyObservableCollection<IBackgroundActivity> Activities { get; }

    /// <summary>Number of active processes — drives the badge on the top-bar icon.</summary>
    int ActiveCount { get; }

    /// <summary>True when at least one process is active, so the block can show/hide itself.</summary>
    bool IsAnyActive { get; }

    /// <summary>
    /// Adds an activity to the list (idempotent). Safe to call from a pool thread — the service
    /// marshals the collection change onto the UI thread.
    /// </summary>
    void Register(IBackgroundActivity activity);

    /// <summary>Removes an activity (idempotent). Safe to call from a pool thread.</summary>
    void Unregister(IBackgroundActivity activity);
}
