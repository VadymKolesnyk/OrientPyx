using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models.Network;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.ViewModels;

/// <summary>
/// The «Спільна робота» overlay (network mode): two cards — «Цей комп'ютер — хост» (switch the host on, show the PIN,
/// the addresses, the paired stations and the self-checks with their one-click fixes) and «Підключитися до хоста»
/// (the hosts found on the LAN, a manual address / connection code, the PIN, the connection state, the station
/// checks). One singleton: it also owns the top-bar <see cref="NetworkActivity"/>, registered while the host runs or
/// the station is connected. Host / client events arrive on transport threads and are marshalled to the UI thread.
/// </summary>
public sealed partial class NetworkViewModel : PageViewModelBase
{
    private static readonly Regex EndpointPattern = new(@"^\d{1,3}(\.\d{1,3}){3}:\d{1,5}$", RegexOptions.Compiled);
    private static readonly Regex ConnectionCodePattern = new(@"^[A-Za-z2-9]{12}$", RegexOptions.Compiled);
    private static readonly TimeSpan HostRefreshCoalesce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan LastSeenRefresh = TimeSpan.FromSeconds(15);

    private readonly INetworkHost _host;
    private readonly INetworkClient _client;
    private readonly IStationDiagnostics _diagnostics;
    private readonly IWorkspaceMode _mode;
    private readonly IAppStore _appStore;
    private readonly IActivityLog _log;
    private readonly IBackgroundActivityService _activities;
    private readonly NetworkActivity _activity;

    private NetworkSettings? _settings;
    private bool _isOpen;
    private bool _activityRegistered;
    private bool _syncingHostSwitch;
    private DispatcherTimer? _hostRefreshTimer;
    private DispatcherTimer? _lastSeenTimer;

    // The last diagnostics, kept so the rows can be rebuilt with new labels on a language switch.
    private IReadOnlyList<DiagnosticCheck> _hostCheckResults = [];
    private IReadOnlyList<DiagnosticCheck> _stationCheckResults = [];
    private IReadOnlyList<DiagnosticCheck> _warningResults = [];

    public NetworkViewModel(
        ILocalizationService localization,
        INetworkHost host,
        INetworkClient client,
        IStationDiagnostics diagnostics,
        IWorkspaceMode mode,
        IAppStore appStore,
        IActivityLog log,
        IBackgroundActivityService activities)
        : base(localization)
    {
        _host = host;
        _client = client;
        _diagnostics = diagnostics;
        _mode = mode;
        _appStore = appStore;
        _log = log;
        _activities = activities;
        _activity = new NetworkActivity(localization, stop: () => _ = StopFromActivityAsync(),
            openSettings: () => OpenRequested?.Invoke(this, EventArgs.Empty));

        _hostName = Environment.MachineName;
        _stationName = Environment.MachineName;

        _host.Changed += (_, _) => Dispatcher.UIThread.Post(ScheduleHostRefresh);
        _client.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshStation);
        _mode.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshAll);
        Localization.PropertyChanged += (_, _) => RefreshAll();
    }

    public override string NavKey => "Nav.Network";
    public override string TitleKey => "Page.Network.Title";
    public override string TextKey => "Page.Network.Text";

    /// <summary>Raised by the top-bar activity's «Відкрити налаштування» — the root coordinator opens the overlay.</summary>
    public event EventHandler? OpenRequested;

    // ── Overlay lifecycle

    /// <summary>Called when the overlay is shown: loads the names, starts host discovery, refreshes both cards.</summary>
    public async Task OnOpenedAsync()
    {
        _isOpen = true;
        await LoadSettingsAsync();
        try
        {
            _client.StartDiscovery();
        }
        catch (Exception ex)
        {
            _log.Error("Network discovery start", ex);
        }
        _lastSeenTimer ??= new DispatcherTimer { Interval = LastSeenRefresh };
        _lastSeenTimer.Tick -= OnLastSeenTick;
        _lastSeenTimer.Tick += OnLastSeenTick;
        _lastSeenTimer.Start();
        RefreshAll();
    }

    /// <summary>Called when the overlay is closed: stops discovery (the names were saved on focus loss).</summary>
    public void OnClosed()
    {
        _isOpen = false;
        _lastSeenTimer?.Stop();
        try
        {
            _client.StopDiscovery();
        }
        catch (Exception ex)
        {
            _log.Error("Network discovery stop", ex);
        }
    }

    private void OnLastSeenTick(object? sender, EventArgs e)
    {
        foreach (var row in Stations)
            row.RefreshLastSeen(Localization);
    }

    // ── Startup

    /// <summary>
    /// Starts the host when it was switched on last time (<see cref="NetworkSettings.HostEnabled"/>). Never throws:
    /// a failure is logged and shown on the host card when the overlay is opened.
    /// </summary>
    public async Task AutoStartHostAsync()
    {
        try
        {
            await LoadSettingsAsync();
            if (_settings is not { HostEnabled: true } || _host.IsRunning || _mode.IsStation)
                return;
            await _host.StartAsync();
            _log.Info("Network host auto-started.");
        }
        catch (Exception ex)
        {
            _log.Error("Network host auto-start", ex);
            HostError = ex.Message;
        }
        RefreshAll();
    }

    /// <summary>Best-effort shutdown on exit: stops the host / leaves the host. Bounded by the caller's timeout.</summary>
    public async Task ShutdownAsync()
    {
        try
        {
            if (_host.IsRunning)
                await _host.StopAsync();
            else if (_mode.IsStation)
                await _client.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Network shutdown", ex);
        }
    }

    // ── Settings (names)

    [ObservableProperty]
    private string _hostName;

    [ObservableProperty]
    private string _stationName;

    private async Task LoadSettingsAsync()
    {
        if (_settings is not null)
            return;
        try
        {
            var settings = await _appStore.GetNetworkSettingsAsync();
            _settings = settings;
            HostName = string.IsNullOrWhiteSpace(settings.HostName) ? Environment.MachineName : settings.HostName;
            StationName = string.IsNullOrWhiteSpace(settings.StationName) ? Environment.MachineName : settings.StationName;
            if (string.IsNullOrWhiteSpace(AddressText) && !string.IsNullOrWhiteSpace(settings.StationHost))
                AddressText = settings.StationHost;
        }
        catch (Exception ex)
        {
            _log.Error("Network settings load", ex);
        }
    }

    /// <summary>Saves the host / station names (the view calls it when a name box loses focus).</summary>
    [RelayCommand]
    private async Task SaveNamesAsync()
    {
        if (_settings is null)
            return;
        var hostName = HostName.Trim();
        var stationName = StationName.Trim();
        if (hostName == _settings.HostName && stationName == _settings.StationName)
            return;
        _settings.HostName = hostName;
        _settings.StationName = stationName;
        try
        {
            await _appStore.SaveNetworkSettingsAsync(_settings);
        }
        catch (Exception ex)
        {
            _log.Error("Network settings save", ex);
        }
    }

    private async Task SaveHostEnabledAsync(bool enabled)
    {
        if (_settings is null)
            return;
        _settings.HostEnabled = enabled;
        try
        {
            await _appStore.SaveNetworkSettingsAsync(_settings);
        }
        catch (Exception ex)
        {
            _log.Error("Network settings save", ex);
        }
    }

    // ── Host card

    /// <summary>The «Приймати підключення станцій» switch. Bound two-way; the change starts / stops the host.</summary>
    [ObservableProperty]
    private bool _isHostEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHostCardEnabled))]
    [NotifyPropertyChangedFor(nameof(IsStationCardEnabled))]
    private bool _isHostBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHostError))]
    private string? _hostError;

    [ObservableProperty]
    private bool _isHostChecking;

    public bool HasHostError => !string.IsNullOrEmpty(HostError);

    public bool IsHostRunning => _host.IsRunning;

    /// <summary>The 4-digit PIN as shown on the host card.</summary>
    public string PinDisplay => _host.Info?.Pin ?? string.Empty;

    public string ConnectionCode => _host.IsRunning ? _host.ConnectionCode : string.Empty;

    public bool HasConnectionCode => !string.IsNullOrEmpty(ConnectionCode);

    public ObservableCollection<string> Addresses { get; } = [];

    public ObservableCollection<StationRowViewModel> Stations { get; } = [];

    public bool HasStations => Stations.Count > 0;

    public ObservableCollection<CheckRowViewModel> HostChecks { get; } = [];

    public bool HasHostChecks => HostChecks.Count > 0;

    /// <summary>True when every self-check passed (Ok / Info / Skipped) — the big «Готово» panel.</summary>
    public bool IsHostReady => _host.IsRunning && _hostCheckResults.Count > 0
                               && _hostCheckResults.All(c => c.State is CheckState.Ok or CheckState.Info or CheckState.Skipped);

    public string ReadyText => string.Format(Localization.Get("Network.Host.Ready"),
        _host.Info?.HostName ?? HostName, PinDisplay);

    /// <summary>The host card is greyed out while this instance is a station.</summary>
    public bool IsHostCardEnabled => !_mode.IsStation && !IsHostBusy;

    public bool ShowHostStationHint => _mode.IsStation;

    partial void OnIsHostEnabledChanged(bool value)
    {
        if (_syncingHostSwitch || value == _host.IsRunning)
            return;
        _ = ApplyHostSwitchAsync(value);
    }

    private async Task ApplyHostSwitchAsync(bool on)
    {
        if (IsHostBusy)
        {
            SyncHostSwitch();
            return;
        }
        IsHostBusy = true;
        HostError = null;
        try
        {
            await LoadSettingsAsync();
            await SaveNamesAsync();
            if (on)
            {
                await _host.StartAsync();
                await SaveHostEnabledAsync(true);
                _log.Action("Network host switched on");
            }
            else
            {
                await _host.StopAsync();
                await SaveHostEnabledAsync(false);
                _hostCheckResults = [];
                _log.Action("Network host switched off");
            }
        }
        catch (Exception ex)
        {
            _log.Error(on ? "Network host start" : "Network host stop", ex);
            HostError = ex.Message;
        }
        finally
        {
            IsHostBusy = false;
        }
        RefreshAll();
        if (_host.IsRunning && on)
            await RunHostChecksAsync();
    }

    private void SyncHostSwitch()
    {
        _syncingHostSwitch = true;
        IsHostEnabled = _host.IsRunning;
        _syncingHostSwitch = false;
    }

    [RelayCommand]
    private void RegeneratePin()
    {
        if (!_host.IsRunning)
            return;
        try
        {
            _host.RegeneratePin();
            _log.Action("Network host: new PIN");
        }
        catch (Exception ex)
        {
            _log.Error("Network host PIN", ex);
            HostError = ex.Message;
        }
        RefreshHost();
    }

    // The «Довірений» check box of a station row: the host remembers the DEVICE (its id), not the name.
    private async void TrustStation(StationRowViewModel row, bool trusted)
    {
        try
        {
            await _host.SetStationTrustedAsync(row.Token, trusted);
        }
        catch (Exception ex)
        {
            _log.Error("Network host: trust station", ex);
        }
    }

    [RelayCommand]
    private async Task DisconnectStationAsync(StationRowViewModel? row)
    {
        if (row is null)
            return;
        try
        {
            await _host.DisconnectStationAsync(row.Token);
            _log.Action($"Network host: station '{row.Name}' disconnected");
        }
        catch (Exception ex)
        {
            _log.Error("Network host disconnect station", ex);
            HostError = ex.Message;
        }
        RefreshHost();
    }

    [RelayCommand]
    private async Task DisconnectAllStationsAsync()
    {
        try
        {
            await _host.DisconnectAllAsync();
            _log.Action("Network host: all stations disconnected");
        }
        catch (Exception ex)
        {
            _log.Error("Network host disconnect all", ex);
            HostError = ex.Message;
        }
        RefreshHost();
    }

    /// <summary>Runs the host self-checks («Перевірити ще раз»).</summary>
    [RelayCommand]
    private async Task RunHostChecksAsync()
    {
        if (!_host.IsRunning || _host.Info is not { } info || IsHostChecking)
            return;
        IsHostChecking = true;
        try
        {
            _hostCheckResults = await _diagnostics.RunHostChecksAsync(info.Port);
        }
        catch (Exception ex)
        {
            _log.Error("Network host checks", ex);
            HostError = ex.Message;
        }
        finally
        {
            IsHostChecking = false;
        }
        RebuildHostChecks();
    }

    /// <summary>Applies a check's one-click fix and replaces that row with the re-run check.</summary>
    [RelayCommand]
    private async Task ApplyFixAsync(CheckRowViewModel? row)
    {
        if (row?.FixKey is null || row.IsFixing || _host.Info is not { } info)
            return;
        row.IsFixing = true;
        try
        {
            var updated = await _diagnostics.ApplyFixAsync(row.FixKey, info.Port);
            _log.Action($"Network fix '{row.FixKey}' → {updated.State}");
            var list = _hostCheckResults.ToList();
            var index = list.FindIndex(c => c.Key == row.Key);
            if (index >= 0)
                list[index] = updated;
            else
                list.Add(updated);
            _hostCheckResults = list;
        }
        catch (Exception ex)
        {
            _log.Error($"Network fix '{row.FixKey}'", ex);
            HostError = ex.Message;
        }
        finally
        {
            row.IsFixing = false;
        }
        RebuildHostChecks();
    }

    private void RebuildHostChecks()
    {
        HostChecks.Clear();
        foreach (var check in _hostCheckResults)
            HostChecks.Add(new CheckRowViewModel(check, Localization));
        OnPropertyChanged(nameof(HasHostChecks));
        OnPropertyChanged(nameof(IsHostReady));
        OnPropertyChanged(nameof(ReadyText));
    }

    // Host.Changed fires on every station request; coalesce so the station table is not rebuilt per RPC.
    private void ScheduleHostRefresh()
    {
        _hostRefreshTimer ??= new DispatcherTimer { Interval = HostRefreshCoalesce };
        _hostRefreshTimer.Tick -= OnHostRefreshTick;
        _hostRefreshTimer.Tick += OnHostRefreshTick;
        if (!_hostRefreshTimer.IsEnabled)
            _hostRefreshTimer.Start();
    }

    private void OnHostRefreshTick(object? sender, EventArgs e)
    {
        _hostRefreshTimer?.Stop();
        RefreshHost();
    }

    private void RefreshHost()
    {
        SyncHostSwitch();
        var info = _host.IsRunning ? _host.Info : null;

        Addresses.Clear();
        if (info is not null)
            foreach (var address in info.Addresses)
                Addresses.Add($"{address}:{info.Port}");

        IReadOnlyList<ConnectedStation> stations = _host.IsRunning ? _host.Stations : [];
        Stations.Clear();
        foreach (var station in stations.OrderByDescending(s => s.IsOnline).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            Stations.Add(new StationRowViewModel(station, Localization, TrustStation));

        if (!_host.IsRunning && _hostCheckResults.Count > 0)
        {
            _hostCheckResults = [];
            RebuildHostChecks();
        }

        OnPropertyChanged(nameof(IsHostRunning));
        OnPropertyChanged(nameof(PinDisplay));
        OnPropertyChanged(nameof(ConnectionCode));
        OnPropertyChanged(nameof(HasConnectionCode));
        OnPropertyChanged(nameof(HasStations));
        OnPropertyChanged(nameof(IsHostReady));
        OnPropertyChanged(nameof(ReadyText));
        OnPropertyChanged(nameof(IsHostCardEnabled));
        OnPropertyChanged(nameof(ShowHostStationHint));
        OnPropertyChanged(nameof(IsStationCardEnabled));
        OnPropertyChanged(nameof(ShowStationHostHint));
        ConnectCommand.NotifyCanExecuteChanged();
        UpdateActivity();
    }

    // ── Station card

    public ObservableCollection<DiscoveredHostRowViewModel> DiscoveredHosts { get; } = [];

    public bool HasDiscoveredHosts => DiscoveredHosts.Count > 0;

    [ObservableProperty]
    private DiscoveredHostRowViewModel? _selectedHost;

    /// <summary>A manual <c>ip:port</c> or a 12-character connection code; selecting a found host fills it in.</summary>
    [ObservableProperty]
    private string _addressText = string.Empty;

    private string _pin = string.Empty;

    /// <summary>The 4-digit PIN shown on the host; anything but digits is dropped.</summary>
    public string Pin
    {
        get => _pin;
        set
        {
            var digits = new string((value ?? string.Empty).Where(char.IsDigit).Take(4).ToArray());
            if (SetProperty(ref _pin, digits, nameof(Pin)))
                ConnectCommand.NotifyCanExecuteChanged();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStationCardEnabled))]
    private bool _isConnecting;

    [ObservableProperty]
    private bool _isStationChecking;

    public bool IsConnected => _mode.IsStation && _client.State is StationState.Connected or StationState.Reconnecting;

    public bool IsStationOffline => _mode.IsStation && _client.State == StationState.Reconnecting;

    public string StationStateText => _client.State switch
    {
        StationState.Connecting => Localization.Get("Network.Station.State.Connecting"),
        StationState.Connected => string.Format(Localization.Get("Network.Station.State.Connected"), _client.Host?.HostName ?? string.Empty),
        StationState.Reconnecting => string.Format(Localization.Get("Network.Station.State.Reconnecting"), _client.Host?.HostName ?? string.Empty),
        _ => Localization.Get("Network.Station.State.Disconnected"),
    };

    public string ErrorText
    {
        get
        {
            if (string.IsNullOrEmpty(_client.LastErrorKey) && string.IsNullOrEmpty(_client.LastErrorDetail))
                return string.Empty;
            var text = LocalizeError(_client.LastErrorKey);
            var detail = _client.LastErrorDetail;
            return string.IsNullOrWhiteSpace(detail) || string.Equals(detail, text, StringComparison.Ordinal)
                ? text
                : $"{text} ({detail})";
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorText) && !IsConnected;

    public ObservableCollection<CheckRowViewModel> Warnings { get; } = [];

    public bool HasWarnings => Warnings.Count > 0;

    public ObservableCollection<CheckRowViewModel> StationChecks { get; } = [];

    public bool HasStationChecks => StationChecks.Count > 0;

    /// <summary>The station card is greyed out while this instance is a host.</summary>
    public bool IsStationCardEnabled => !_mode.IsHost && !IsHostBusy && !IsConnecting;

    public bool ShowStationHostHint => _mode.IsHost;

    private bool _restoringSelection;

    partial void OnSelectedHostChanged(DiscoveredHostRowViewModel? value)
    {
        if (!_restoringSelection && value is not null && !string.IsNullOrEmpty(value.Endpoint))
            AddressText = value.Endpoint;
    }

    partial void OnAddressTextChanged(string value) => ConnectCommand.NotifyCanExecuteChanged();

    private bool CanConnect() => !IsConnecting && !_mode.IsHost && !IsConnected && ResolveEndpoint() is not null;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        var endpoint = ResolveEndpoint();
        if (endpoint is null)
            return;
        IsConnecting = true;
        try
        {
            await LoadSettingsAsync();
            await SaveNamesAsync();
            var pin = Pin.Length == 4 ? Pin : null;
            var result = await _client.ConnectAsync(endpoint, pin);
            _warningResults = result.Warnings;
            if (result.Success)
            {
                _log.Action($"Network station connected to {endpoint}");
                Pin = string.Empty;
            }
            else
            {
                _log.Info($"Network station: connect to {endpoint} failed ({result.ErrorKey}: {result.Detail})");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Network station connect", ex);
            _warningResults = [];
        }
        finally
        {
            IsConnecting = false;
        }
        RefreshStation();
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        try
        {
            await _client.DisconnectAsync();
            _log.Action("Network station disconnected");
        }
        catch (Exception ex)
        {
            _log.Error("Network station disconnect", ex);
        }
        _warningResults = [];
        RefreshStation();
    }

    /// <summary>Runs the station checks («Перевірити») against the typed / selected / connected host.</summary>
    [RelayCommand]
    private async Task RunStationChecksAsync()
    {
        if (IsStationChecking)
            return;
        var endpoint = ResolveEndpoint() ?? _client.Host?.Endpoint;
        IsStationChecking = true;
        try
        {
            _stationCheckResults = await _diagnostics.RunStationChecksAsync(endpoint);
        }
        catch (Exception ex)
        {
            _log.Error("Network station checks", ex);
        }
        finally
        {
            IsStationChecking = false;
        }
        RebuildStationChecks();
    }

    /// <summary>The endpoint to connect to: the typed <c>ip:port</c>, a decoded connection code, or the selected host.</summary>
    private string? ResolveEndpoint()
    {
        var text = AddressText.Trim();
        if (text.Length == 0)
            return string.IsNullOrEmpty(SelectedHost?.Endpoint) ? null : SelectedHost!.Endpoint;
        if (EndpointPattern.IsMatch(text))
            return text;
        var compact = text.Replace("-", string.Empty).Replace(" ", string.Empty);
        if (ConnectionCodePattern.IsMatch(compact))
        {
            try
            {
                return _client.DecodeConnectionCode(compact);
            }
            catch (Exception)
            {
                return null;
            }
        }
        return null;
    }

    private string LocalizeError(string? key)
    {
        if (!string.IsNullOrEmpty(key))
        {
            var full = key.StartsWith("Network.Error.", StringComparison.Ordinal) ? key : $"Network.Error.{key}";
            var text = Localization.Get(full);
            if (!string.Equals(text, full, StringComparison.Ordinal))
                return text;
        }
        return Localization.Get("Network.Error.Generic");
    }

    private void RefreshStation()
    {
        var selectedId = SelectedHost?.HostId;
        IReadOnlyList<DiscoveredHost> found;
        try
        {
            found = _client.DiscoveredHosts;
        }
        catch (Exception)
        {
            found = [];
        }
        DiscoveredHosts.Clear();
        foreach (var host in found.OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase))
            DiscoveredHosts.Add(new DiscoveredHostRowViewModel(host));
        var keep = selectedId is null ? null : DiscoveredHosts.FirstOrDefault(h => h.HostId == selectedId);
        if (!ReferenceEquals(SelectedHost, keep))
        {
            // Restore the selection by id without re-filling the address box from it.
            _restoringSelection = true;
            SelectedHost = keep;
            _restoringSelection = false;
        }

        Warnings.Clear();
        foreach (var warning in _warningResults)
            Warnings.Add(new CheckRowViewModel(warning, Localization));
        RebuildStationChecks();

        OnPropertyChanged(nameof(HasDiscoveredHosts));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsStationOffline));
        OnPropertyChanged(nameof(StationStateText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsStationCardEnabled));
        OnPropertyChanged(nameof(ShowStationHostHint));
        OnPropertyChanged(nameof(IsHostCardEnabled));
        OnPropertyChanged(nameof(ShowHostStationHint));
        ConnectCommand.NotifyCanExecuteChanged();
        UpdateActivity();
    }

    private void RebuildStationChecks()
    {
        StationChecks.Clear();
        foreach (var check in _stationCheckResults)
            StationChecks.Add(new CheckRowViewModel(check, Localization));
        OnPropertyChanged(nameof(HasStationChecks));
    }

    private void RefreshAll()
    {
        RefreshHost();
        RebuildHostChecks();
        RefreshStation();
    }

    // ── Top-bar activity

    private void UpdateActivity()
    {
        var hosting = _host.IsRunning;
        var station = _mode.IsStation && _client.State != StationState.Disconnected;
        if (!hosting && !station)
        {
            if (_activityRegistered)
            {
                _activities.Unregister(_activity);
                _activityRegistered = false;
            }
            return;
        }

        if (hosting)
        {
            _activity.StatusText = string.Format(Localization.Get("Activity.Network.StatusHost"), _host.Stations.Count(s => s.IsOnline));
            _activity.State = BackgroundActivityState.Running;
        }
        else if (_client.State == StationState.Reconnecting)
        {
            _activity.StatusText = Localization.Get("Activity.Network.StatusOffline");
            _activity.State = BackgroundActivityState.Failing;
        }
        else
        {
            _activity.StatusText = string.Format(Localization.Get("Activity.Network.StatusStation"), _client.Host?.HostName ?? string.Empty);
            _activity.State = BackgroundActivityState.Running;
        }

        if (!_activityRegistered)
        {
            _activities.Register(_activity);
            _activityRegistered = true;
        }
    }

    private async Task StopFromActivityAsync()
    {
        if (_host.IsRunning)
            await ApplyHostSwitchAsync(false);
        else if (_mode.IsStation)
            await DisconnectAsync();
    }
}

/// <summary>One paired station in the host card's table.</summary>
public sealed partial class StationRowViewModel : ObservableObject
{
    private readonly ConnectedStation _station;

    [ObservableProperty]
    private string _lastSeenText = string.Empty;

    private readonly Action<StationRowViewModel, bool> _onTrustChanged;
    private readonly bool _ready;

    public StationRowViewModel(ConnectedStation station, ILocalizationService localization, Action<StationRowViewModel, bool> onTrustChanged)
    {
        _station = station;
        _onTrustChanged = onTrustChanged;
        _isTrusted = station.IsTrusted;
        RefreshLastSeen(localization);
        _ready = true;
    }

    public string Token => _station.Token;
    public string Name => _station.Name;
    public string Address => _station.Address;
    public bool IsOnline => _station.IsOnline;

    /// <summary>A station of an older build sends no device id and cannot be trusted by device.</summary>
    public bool CanTrust => !string.IsNullOrEmpty(_station.DeviceId);

    /// <summary>«Довірений пристрій»: pairs again without a PIN. Two-way from the row's check box.</summary>
    [ObservableProperty]
    private bool _isTrusted;

    partial void OnIsTrustedChanged(bool value)
    {
        if (_ready)
            _onTrustChanged(this, value);
    }

    /// <summary>«щойно» / «N хв тому» — recomputed by the page's timer while the overlay is open.</summary>
    public void RefreshLastSeen(ILocalizationService localization)
    {
        var minutes = (int)Math.Max(0, (DateTimeOffset.Now - _station.LastSeenAt).TotalMinutes);
        LastSeenText = minutes == 0
            ? localization.Get("Network.Host.Stations.JustNow")
            : string.Format(localization.Get("Network.Host.Stations.MinutesAgo"), minutes);
    }
}

/// <summary>One host found on the LAN («Головний-ноут · Кубок міста · v0.8.0»).</summary>
public sealed class DiscoveredHostRowViewModel
{
    public DiscoveredHostRowViewModel(DiscoveredHost host)
    {
        HostId = host.HostId;
        Endpoint = host.Endpoint;
        var parts = new List<string> { host.Name };
        if (!string.IsNullOrWhiteSpace(host.Competition))
            parts.Add(host.Competition);
        if (!string.IsNullOrWhiteSpace(host.Version))
            parts.Add($"v{host.Version}");
        Label = string.Join(" · ", parts);
    }

    public string HostId { get; }
    public string Endpoint { get; }
    public string Label { get; }
}

/// <summary>One self-check row: state icon, localised title (<c>Network.Check.&lt;key&gt;</c>), detail, optional fix.</summary>
public sealed partial class CheckRowViewModel : ObservableObject
{
    private readonly DiagnosticCheck _check;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFix))]
    private bool _isFixing;

    public CheckRowViewModel(DiagnosticCheck check, ILocalizationService localization)
    {
        _check = check;
        Title = localization.Get($"Network.Check.{check.Key}");
        FixLabel = check.Fix is null ? string.Empty : localization.Get($"Network.Fix.{check.Fix.Key}");
        // The detail in the UI language when the check names a key (the English Detail stays for the log).
        Detail = check.DetailKey is { } key
            ? string.Format(localization.Get(key), check.DetailArgs.Cast<object>().ToArray())
            : check.Detail;
    }

    public string Key => _check.Key;
    public CheckState State => _check.State;
    public string Title { get; }
    public string Detail { get; }
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public string? FixKey => _check.Fix?.Key;
    public bool HasFix => _check.Fix is not null;
    public bool NeedsElevation => _check.Fix?.NeedsElevation ?? false;
    public string FixLabel { get; }
    public bool CanFix => HasFix && !IsFixing;

    public bool IsOk => State == CheckState.Ok;
    public bool IsWarning => State == CheckState.Warning;
    public bool IsFailed => State == CheckState.Failed;
    public bool IsInfo => State is CheckState.Info or CheckState.Skipped;
}
