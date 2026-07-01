using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.Services;

/// <summary>
/// The on-screen results-monitor generation surfaced as a background activity in the top-bar block. Owned by
/// <c>MonitorResultsViewModel</c>, which drives the file-writing loop; this is a thin localized façade
/// exposing pause/resume/stop and a "go to the Monitor page" jump, each delegated to a VM-supplied callback.
/// Mirrors <see cref="OnlineResultsActivity"/>.
/// </summary>
public sealed partial class MonitorResultsActivity : ObservableObject, IBackgroundActivity
{
    private readonly ILocalizationService _localization;
    private readonly Action _pause;
    private readonly Action _resume;
    private readonly Action _stop;
    private readonly Action _openSettings;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    private BackgroundActivityState _state = BackgroundActivityState.Running;

    public MonitorResultsActivity(
        ILocalizationService localization,
        Action pause,
        Action resume,
        Action stop,
        Action openSettings)
    {
        _localization = localization;
        _pause = pause;
        _resume = resume;
        _stop = stop;
        _openSettings = openSettings;

        _localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public string Title => _localization.Get("Activity.Monitor.Title");

    public bool IsPaused => State == BackgroundActivityState.Paused;

    public bool CanPause => true;
    public bool CanStop => true;
    public bool CanOpenSettings => true;

    [RelayCommand]
    private void TogglePause()
    {
        if (State == BackgroundActivityState.Running)
        {
            _pause();
            State = BackgroundActivityState.Paused;
        }
        else
        {
            _resume();
            State = BackgroundActivityState.Running;
        }
    }

    [RelayCommand]
    private void Stop() => _stop();

    [RelayCommand]
    private void OpenSettings() => _openSettings();
}
