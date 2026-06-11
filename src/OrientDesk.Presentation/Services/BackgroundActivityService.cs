using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OrientDesk.Presentation.Services;

/// <summary>
/// Default <see cref="IBackgroundActivityService"/>. Keeps a UI-thread-only observable list of running
/// activities; <see cref="Register"/>/<see cref="Unregister"/> marshal onto the UI thread because
/// pollers raise these from pool threads (SQLite/file work runs off the UI thread). Re-raises
/// <see cref="ActiveCount"/>/<see cref="IsAnyActive"/> whenever the list changes.
/// </summary>
public sealed class BackgroundActivityService : ObservableObject, IBackgroundActivityService
{
    private readonly ObservableCollection<IBackgroundActivity> _activities = [];

    public BackgroundActivityService()
    {
        Activities = new ReadOnlyObservableCollection<IBackgroundActivity>(_activities);
        _activities.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ActiveCount));
            OnPropertyChanged(nameof(IsAnyActive));
        };
    }

    public ReadOnlyObservableCollection<IBackgroundActivity> Activities { get; }

    public int ActiveCount => _activities.Count;

    public bool IsAnyActive => _activities.Count > 0;

    public void Register(IBackgroundActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        RunOnUi(() =>
        {
            if (!_activities.Contains(activity))
                _activities.Add(activity);
        });
    }

    public void Unregister(IBackgroundActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        RunOnUi(() => _activities.Remove(activity));
    }

    // The list backs UI; only ever touch it on the UI thread. Callers may be on a pool thread.
    private static void RunOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}
