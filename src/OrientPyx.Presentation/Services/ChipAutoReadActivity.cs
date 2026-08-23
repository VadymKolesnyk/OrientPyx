using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// The chip rental auto-read surfaced as a background activity in the top-bar block. Owned by
/// <c>ChipsViewModel</c>, which drives the actual poller; this is a thin localized façade that exposes
/// pause/resume/stop and a "go to the Chips page" jump, delegating each to a callback the VM supplies.
/// Keeping the wiring in delegates (rather than referencing the VM) means the activity stays a small,
/// self-describing unit and the VM keeps full control of the poller and session lifetime.
/// </summary>
public sealed partial class ChipAutoReadActivity : ObservableObject, IBackgroundActivity
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

    public ChipAutoReadActivity(
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

        // Title follows the UI language while the activity is shown.
        _localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public string Title => _localization.Get("Activity.ChipAutoRead.Title");

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
