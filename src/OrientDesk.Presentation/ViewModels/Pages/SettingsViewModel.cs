using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;

namespace OrientDesk.Presentation.ViewModels.Pages;

public sealed partial class SettingsViewModel : PageViewModelBase
{
    private readonly IAppSettingsService _settings;
    private readonly IUiScaleService _uiScale;
    private readonly IBusyService _busy;

    [ObservableProperty]
    private string _eventsPath = string.Empty;

    [ObservableProperty]
    private bool _pathsSaved;

    // --- Online live-results (Supabase) connection -------------------------------------------------

    [ObservableProperty]
    private string _onlineSupabaseUrl = string.Empty;

    [ObservableProperty]
    private string _onlineServiceRoleKey = string.Empty;

    [ObservableProperty]
    private string _onlinePublicBaseUrl = string.Empty;

    [ObservableProperty]
    private int _onlineIntervalSeconds = OnlineApiSettings.DefaultIntervalSeconds;

    [ObservableProperty]
    private bool _onlineSaved;

    public SettingsViewModel(
        ILocalizationService localization,
        IAppSettingsService settings,
        IUiScaleService uiScale,
        IBusyService busy)
        : base(localization)
    {
        _settings = settings;
        _uiScale = uiScale;
        _busy = busy;
        _ = LoadPathsAsync();
        _ = LoadOnlineAsync();
    }

    public override string NavKey => "Nav.Settings";
    public override string TitleKey => "Page.Settings.Title";
    public override string TextKey => "Page.Settings.Text";

    public double MinFontScale => _settings.MinFontScale;
    public double MaxFontScale => _settings.MaxFontScale;

    /// <summary>Font scale slider value. Setting it applies live and persists.</summary>
    public double FontScale
    {
        get => _uiScale.Scale;
        set
        {
            if (Math.Abs(_uiScale.Scale - value) < 0.0001)
                return;

            _ = _uiScale.SetScaleAsync(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(FontScalePercent));
        }
    }

    /// <summary>Human-friendly label, e.g. "120%".</summary>
    public string FontScalePercent => $"{Math.Round(_uiScale.Scale * 100)}%";

    [RelayCommand]
    private void SetLanguage(string cultureName)
    {
        Localization.SetLanguage(CultureInfo.GetCultureInfo(cultureName));
    }

    [RelayCommand]
    private async Task SavePathsAsync()
    {
        await _busy.RunAsync(() =>
            _settings.SavePathsAsync(new AppPaths { EventsPath = EventsPath }));
        PathsSaved = true;
    }

    private async Task LoadPathsAsync()
    {
        var paths = await _settings.GetPathsAsync();
        EventsPath = paths.EventsPath;
    }

    [RelayCommand]
    private async Task SaveOnlineAsync()
    {
        var interval = Math.Max(OnlineApiSettings.MinIntervalSeconds, OnlineIntervalSeconds);
        var settings = new OnlineApiSettings(
            (OnlineSupabaseUrl ?? string.Empty).Trim(),
            (OnlineServiceRoleKey ?? string.Empty).Trim(),
            (OnlinePublicBaseUrl ?? string.Empty).Trim(),
            interval);
        await _busy.RunAsync(() => _settings.SaveOnlineApiSettingsAsync(settings));
        OnlineIntervalSeconds = interval;
        OnlineSaved = true;
    }

    [RelayCommand]
    private void IncrementOnlineInterval() => OnlineIntervalSeconds++;

    [RelayCommand]
    private void DecrementOnlineInterval()
    {
        if (OnlineIntervalSeconds > OnlineApiSettings.MinIntervalSeconds)
            OnlineIntervalSeconds--;
    }

    private async Task LoadOnlineAsync()
    {
        var online = await _settings.GetOnlineApiSettingsAsync();
        OnlineSupabaseUrl = online.SupabaseUrl;
        OnlineServiceRoleKey = online.ServiceRoleKey;
        OnlinePublicBaseUrl = online.PublicBaseUrl;
        OnlineIntervalSeconds = online.IntervalSeconds;
    }
}
