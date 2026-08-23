using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// The finish-read auto-read surfaced as a background activity in the top-bar block. Owned by
/// <c>FinishReadViewModel</c>, which drives the poller; this is a thin localized façade exposing
/// pause/resume/stop and a "go to the Finish-read page" jump, each delegated to a VM-supplied callback.
/// Mirrors <see cref="ChipAutoReadActivity"/>.
/// </summary>
public sealed partial class FinishReadActivity : ObservableObject, IBackgroundActivity
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
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    [NotifyPropertyChangedFor(nameof(IsRunningNormally))]
    private BackgroundActivityState _state = BackgroundActivityState.Running;

    public FinishReadActivity(
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

    public string Title => _localization.Get("Activity.FinishRead.Title");

    public bool IsPaused => State == BackgroundActivityState.Paused;

    public bool HasProblem => State == BackgroundActivityState.Failing;

    public bool IsRunningNormally => State == BackgroundActivityState.Running;

    public bool CanPause => true;
    public bool CanStop => true;
    public bool CanOpenSettings => true;

    [RelayCommand]
    private void TogglePause()
    {
        // Anything that isn't Paused (Running OR Failing) pauses; only a paused activity resumes. Keyed on
        // Paused rather than Running so the pause button still pauses while the activity is in trouble.
        if (State != BackgroundActivityState.Paused)
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
