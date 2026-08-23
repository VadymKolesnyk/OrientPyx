using System.Collections.ObjectModel;
using System.Linq;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OrientPyx.Presentation.Services;

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
            OnPropertyChanged(nameof(HasAnyProblem));
        };
    }

    public ReadOnlyObservableCollection<IBackgroundActivity> Activities { get; }

    public int ActiveCount => _activities.Count;

    public bool IsAnyActive => _activities.Count > 0;

    public bool HasAnyProblem => _activities.Any(a => a.HasProblem);

    public void Register(IBackgroundActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        RunOnUi(() =>
        {
            if (_activities.Contains(activity))
                return;

            // Track each activity's own state changes so the top-bar glyph turns red the moment one
            // starts failing — the collection itself doesn't change when only a member's state does.
            activity.PropertyChanged += OnActivityPropertyChanged;
            _activities.Add(activity);
            OnPropertyChanged(nameof(HasAnyProblem));
        });
    }

    public void Unregister(IBackgroundActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        RunOnUi(() =>
        {
            activity.PropertyChanged -= OnActivityPropertyChanged;
            _activities.Remove(activity);
            OnPropertyChanged(nameof(HasAnyProblem));
        });
    }

    private void OnActivityPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IBackgroundActivity.HasProblem) or nameof(IBackgroundActivity.State)
            or null or "")
        {
            OnPropertyChanged(nameof(HasAnyProblem));
        }
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
