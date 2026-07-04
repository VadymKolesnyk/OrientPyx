using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;

namespace OrientPyx.Presentation.ViewModels.Pages;

public sealed partial class SettingsViewModel : PageViewModelBase
{
    private readonly IAppSettingsService _settings;
    private readonly IUiScaleService _uiScale;
    private readonly IBusyService _busy;
    private readonly IUpdateService _updates;

    [ObservableProperty]
    private string _eventsPath = string.Empty;

    [ObservableProperty]
    private bool _pathsSaved;

    // --- Online live-results (Supabase) connection -------------------------------------------------

    [ObservableProperty]
    private string _onlineSupabaseUrl = string.Empty;

    [ObservableProperty]
    private string _onlineServiceRoleKey = string.Empty;

    // Whether the secret service-role key is shown in clear text (eye toggle). Masked by default.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServiceKeyPasswordChar))]
    private bool _revealServiceRoleKey;

    // '\0' shows the key in clear text; '•' masks it.
    public char ServiceKeyPasswordChar => RevealServiceRoleKey ? '\0' : '•';

    [RelayCommand]
    private void ToggleRevealServiceRoleKey() => RevealServiceRoleKey = !RevealServiceRoleKey;

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
        IBusyService busy,
        IUpdateService updates)
        : base(localization)
    {
        _settings = settings;
        _uiScale = uiScale;
        _busy = busy;
        _updates = updates;
        _ = LoadPathsAsync();
        _ = LoadOnlineAsync();
        _ = LoadReadoutTypeAsync();
    }

    // --- Updates -----------------------------------------------------------------------------------

    /// <summary>The running app version (e.g. "1.4.0"), or "—" for a dev/xcopy build.</summary>
    public string AppVersion =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "—";

    /// <summary>Whether the «check for updates» control is meaningful (only for installed builds).</summary>
    public bool CanCheckForUpdates => _updates.IsInstalled;

    /// <summary>Status line under the update button: available version, "up to date", or an error hint.</summary>
    [ObservableProperty]
    private string _updateStatus = string.Empty;

    // Set once a check finds a newer version, so the button flips to «download & restart».
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingUpdate))]
    private string? _availableVersion;

    public bool HasPendingUpdate => AvailableVersion is not null;

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (!_updates.IsInstalled)
            return;

        UpdateStatus = Localization.Get("Page.Settings.Update.Checking");
        var version = await _updates.CheckForUpdateAsync();
        AvailableVersion = version;
        UpdateStatus = version is null
            ? Localization.Get("Page.Settings.Update.UpToDate")
            : string.Format(Localization.Get("Page.Settings.Update.Available"), version);
    }

    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        if (AvailableVersion is null)
            return;

        UpdateStatus = Localization.Get("Page.Settings.Update.Downloading");
        var ok = await _updates.DownloadAndRestartAsync();
        // On success the process restarts and never reaches here; a false means it failed and we stay.
        if (!ok)
            UpdateStatus = Localization.Get("Page.Settings.Update.Failed");
    }

    // --- Readout type (timing system) --------------------------------------------------------------

    // Guards the setter while LoadReadoutTypeAsync applies the stored value, so it doesn't persist during load.
    private bool _loadingReadoutType;

    /// <summary>The selected timing-system readout format; setting it persists immediately.</summary>
    [ObservableProperty]
    private ReadoutType _readoutType = ReadoutType.SportIdent;

    /// <summary>Radio-button bindings for the two formats (two-way; the checked one sets ReadoutType).</summary>
    public bool IsSportIdent
    {
        get => ReadoutType == ReadoutType.SportIdent;
        set { if (value) ReadoutType = ReadoutType.SportIdent; }
    }

    public bool IsSportTime
    {
        get => ReadoutType == ReadoutType.SportTime;
        set { if (value) ReadoutType = ReadoutType.SportTime; }
    }

    partial void OnReadoutTypeChanged(ReadoutType value)
    {
        OnPropertyChanged(nameof(IsSportIdent));
        OnPropertyChanged(nameof(IsSportTime));
        if (!_loadingReadoutType)
            _ = _settings.SaveReadoutTypeAsync(value);
    }

    private async Task LoadReadoutTypeAsync()
    {
        var stored = await _settings.GetReadoutTypeAsync();
        _loadingReadoutType = true;
        try { ReadoutType = stored; }
        finally { _loadingReadoutType = false; }
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
