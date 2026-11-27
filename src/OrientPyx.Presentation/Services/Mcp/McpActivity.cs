using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.Services.Mcp;

/// <summary>
/// «Асистент (MCP)» in the top bar's activities while the server runs: connected clients, the changes it made, the last
/// one; «Зупинити» switches the assistant off, «Налаштування» opens its block.
/// </summary>
public sealed partial class McpActivity : ObservableObject, IBackgroundActivity
{
    private readonly ILocalizationService _localization;
    private readonly Action _stop;
    private readonly Action _openSettings;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaused), nameof(HasProblem), nameof(IsRunningNormally))]
    private BackgroundActivityState _state = BackgroundActivityState.Running;

    public McpActivity(ILocalizationService localization, Action stop, Action openSettings)
    {
        _localization = localization;
        _stop = stop;
        _openSettings = openSettings;
        _localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public string Title => _localization.Get("Activity.Mcp.Title");

    public bool IsPaused => State == BackgroundActivityState.Paused;
    public bool HasProblem => State == BackgroundActivityState.Failing;
    public bool IsRunningNormally => State == BackgroundActivityState.Running;
    public bool CanPause => false;
    public bool CanStop => true;
    public bool CanOpenSettings => true;

    [RelayCommand]
    private void TogglePause()
    {
    }

    [RelayCommand]
    private void Stop() => _stop();

    [RelayCommand]
    private void OpenSettings() => _openSettings();
}
