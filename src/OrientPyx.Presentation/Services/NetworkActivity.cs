using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// The network mode («Спільна робота») surfaced as a background activity in the top-bar block: «Хост: 2 станції»
/// while this computer serves its data, «Станція: підключено до …» while it works on a host's data, red
/// («немає зв'язку з хостом») while the station is reconnecting. Owned by <c>NetworkViewModel</c>, which registers
/// it while the host runs or the station is connected and removes it otherwise. Cannot be paused; «Зупинити» stops
/// the host / leaves the host, «Відкрити налаштування» opens the «Спільна робота» overlay. Mirrors
/// <see cref="OnlineResultsActivity"/>.
/// </summary>
public sealed partial class NetworkActivity : ObservableObject, IBackgroundActivity
{
    private readonly ILocalizationService _localization;
    private readonly Action _stop;
    private readonly Action _openSettings;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    [NotifyPropertyChangedFor(nameof(IsRunningNormally))]
    private BackgroundActivityState _state = BackgroundActivityState.Running;

    public NetworkActivity(ILocalizationService localization, Action stop, Action openSettings)
    {
        _localization = localization;
        _stop = stop;
        _openSettings = openSettings;
        _localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public string Title => _localization.Get("Activity.Network.Title");

    public bool IsPaused => State == BackgroundActivityState.Paused;

    public bool HasProblem => State == BackgroundActivityState.Failing;

    public bool IsRunningNormally => State == BackgroundActivityState.Running;

    public bool CanPause => false;
    public bool CanStop => true;
    public bool CanOpenSettings => true;

    // Never offered (CanPause is false); kept so the popup's binding resolves.
    [RelayCommand]
    private void TogglePause()
    {
    }

    [RelayCommand]
    private void Stop() => _stop();

    [RelayCommand]
    private void OpenSettings() => _openSettings();
}
