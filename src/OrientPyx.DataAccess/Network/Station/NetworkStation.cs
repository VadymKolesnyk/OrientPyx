using System.Net;
using System.Net.Http.Json;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models.Network;
using OrientPyx.BusinessLogic.Serialization;
using OrientPyx.BusinessLogic.Services.Network;
using OrientPyx.DataAccess.Network.Discovery;
using OrientPyx.DataAccess.Network.Protocol;
using OrientPyx.DataAccess.Persistence;

namespace OrientPyx.DataAccess.Network.Station;

/// <summary>
/// This computer as a station (<see cref="INetworkClient"/>): pairs with a host, builds the remote proxies into
/// <see cref="RemoteServices"/>, switches the workspace to <see cref="WorkspaceRole.Station"/>, keeps the change
/// stream and the log forwarder running, and tracks the connection state. Deliberately depends on none of the
/// competition services (they depend on the routers this fills) — the UI clears the session on connect/disconnect.
/// </summary>
public sealed class NetworkStation : INetworkClient, IDisposable
{
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PairTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ClockSkewLimit = TimeSpan.FromSeconds(2);

    private readonly WorkspaceMode _mode;
    private readonly RemoteServices _remote;
    private readonly IDataChangeBus _bus;
    private readonly IAppStore _appStore;
    private readonly IActivityLog _log;
    private readonly IPluginCatalog _plugins;
    private readonly DiscoveryClient _discovery;
    private readonly IAppSettingsService _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private RpcChannel? _channel;
    private RemoteChangeListener? _listener;
    private LogForwarder? _forwarder;
    private SnapshotCache? _cache;
    private StationState _state = StationState.Disconnected;
    private string? _currentEvent;
    private DateTimeOffset _offlineSince;

    public NetworkStation(
        WorkspaceMode mode,
        RemoteServices remote,
        IDataChangeBus bus,
        IAppStore appStore,
        IActivityLog log,
        IPluginCatalog plugins,
        DiscoveryClient discovery,
        IAppSettingsService settings)
    {
        _mode = mode;
        _remote = remote;
        _bus = bus;
        _appStore = appStore;
        _log = log;
        _plugins = plugins;
        _discovery = discovery;
        _settings = settings;
        _discovery.HostsChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _bus.Changed += OnBusChanged;
    }

    public StationState State
    {
        get => _state;
        private set
        {
            if (_state == value)
                return;
            _state = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public StationInfo? Host { get; private set; }

    public string? LastErrorKey { get; private set; }

    public string? LastErrorDetail { get; private set; }

    public event EventHandler? Changed;

    public int DelayedWrites => _channel?.Health.DelayedWrites ?? 0;

    public event EventHandler? WriteFailed;

    /// <summary>The live channel while connected (the tests call it directly); null otherwise.</summary>
    public RpcChannel? Channel => _channel;

    /// <summary>The identifier of the competition the station currently works on, for the version poll and the log
    /// batches. Set by the UI on selection (optional: it is also inferred from the change stream).</summary>
    public string? CurrentEvent
    {
        get => _currentEvent;
        set
        {
            _currentEvent = value;
            if (_listener is { } listener)
                listener.CurrentEvent = value;
        }
    }

    public (string Identifier, int? DayNumber)? HostCurrentCompetition { get; private set; }

    public void NotifyCurrentEvent(string? identifier) => CurrentEvent = identifier;

    public async Task<ConnectResult> ConnectAsync(string endpoint, string? pin, CancellationToken cancellationToken = default)
    {
        endpoint = (endpoint ?? string.Empty).Trim();
        if (_mode.Role == WorkspaceRole.Host)
            return Fail("HostRoleActive");
        if (endpoint.Length == 0)
            return Fail("Unreachable", "No address.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await TearDownAsync(forgetToken: false);
            State = StationState.Connecting;

            var channel = new RpcChannel(endpoint);
            try
            {
                var settings = await _appStore.GetNetworkSettingsAsync(cancellationToken);
                var stationName = StationNameOf(settings);

                // 1. /ping — is anybody there, and do we speak the same protocol?
                PingResponse ping;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, Wire.PingPath);
                    using var response = await channel.SendOnceAsync(request, PingTimeout, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    ping = await response.Content.ReadFromJsonAsync<PingResponse>(RpcJson.Options, cancellationToken)
                           ?? throw new InvalidOperationException("Empty ping answer.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    channel.Dispose();
                    return Fail("Unreachable", ex.Message);
                }
                if (ping.ProtocolVersion != NetworkProtocol.Version)
                {
                    channel.Dispose();
                    return Fail("Protocol", $"host {ping.ProtocolVersion} / station {NetworkProtocol.Version}");
                }

                // 2. /pair — a PIN, or the saved token when it belongs to this very host.
                // This machine's stable device id: generated once, so a host can trust the device (never the name).
                if (string.IsNullOrEmpty(settings.StationDeviceId))
                {
                    settings.StationDeviceId = Guid.NewGuid().ToString("N");
                    await _appStore.SaveNetworkSettingsAsync(settings, cancellationToken);
                }
                var sameHost = string.Equals(settings.StationHostId, ping.HostId, StringComparison.Ordinal);
                var savedToken = pin is null && sameHost ? settings.StationToken : string.Empty;
                // No PIN typed: the saved token, else the PIN last used for this host, else nothing — a trusted device
                // still gets in; the host answers 401 otherwise and the page asks for the PIN.
                var sentPin = pin ?? (sameHost ? settings.StationPin : string.Empty) ?? string.Empty;

                var enabledPlugins = _plugins.Plugins.Where(p => _plugins.IsEnabled(p.Info.Id)).Select(p => p.Info.Id).Order(StringComparer.Ordinal).ToList();
                var pairRequest = new PairRequest(sentPin, stationName, Wire.AppVersion, NetworkProtocol.Version, enabledPlugins,
                    string.IsNullOrEmpty(savedToken) ? null : savedToken, settings.StationDeviceId);
                PairResponse pair;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, Wire.PairPath)
                    {
                        Content = JsonContent.Create(pairRequest, options: RpcJson.Options),
                    };
                    using var response = await channel.SendOnceAsync(request, PairTimeout, cancellationToken);
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        if (!string.IsNullOrEmpty(savedToken))
                            await ForgetTokenAsync(cancellationToken);
                        channel.Dispose();
                        return Fail(pin is null ? "PinRequired" : "WrongPin");
                    }
                    if ((int)response.StatusCode == 429)
                    {
                        channel.Dispose();
                        return Fail("Locked");
                    }
                    if ((int)response.StatusCode == 409)
                    {
                        channel.Dispose();
                        var error = await response.Content.ReadFromJsonAsync<RpcError>(RpcJson.Options, cancellationToken);
                        return Fail("Protocol", error?.Message);
                    }
                    await channel.ThrowOnErrorAsync(response, cancellationToken);
                    pair = await response.Content.ReadFromJsonAsync<PairResponse>(RpcJson.Options, cancellationToken)
                           ?? throw new InvalidOperationException("Empty pair answer.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not PairingRefusedException)
                {
                    channel.Dispose();
                    return Fail("Unreachable", ex.Message);
                }

                // 3. Remember the host, build the proxies, switch the role.
                settings.StationHost = endpoint;
                settings.StationToken = pair.Token;
                settings.StationHostId = pair.HostId;
                if (!string.IsNullOrEmpty(pin))
                    settings.StationPin = pin; // remembered for this host, so the next connect needs no typing
                await _appStore.SaveNetworkSettingsAsync(settings, cancellationToken);

                channel.Token = pair.Token;
                channel.Log = _log.Info;
                channel.Unauthorized += OnUnauthorized;
                channel.WriteAbandoned += OnWriteAbandoned;
                channel.Health.Changed += OnHealthChanged;
                _channel = channel;
                _cache = new SnapshotCache(_bus);
                _remote.Set(
                    RemoteProxies.EventStore(channel, _cache),
                    RemoteProxies.FolderScanner(channel),
                    RemoteProxies.Catalog(channel),
                    new RemoteEventFiles(channel, pair.HostId),
                    new RemoteArchiveService(channel),
                    RemoteProxies.ResultsAppData(channel));

                Host = new StationInfo(pair.HostId, pair.HostName, endpoint, stationName);
                HostCurrentCompetition = string.IsNullOrEmpty(pair.CurrentIdentifier)
                    ? null
                    : (pair.CurrentIdentifier, pair.CurrentDayNumber);
                _mode.SetStation(Host);

                _listener = new RemoteChangeListener(channel, _bus, _log, pair.HostName) { CurrentEvent = _currentEvent };
                _listener.Unauthorized += OnUnauthorized;
                _listener.Start();
                channel.Health.Start();

                if (_log is FileActivityLog fileLog)
                {
                    _forwarder = new LogForwarder(fileLog, channel, () => _currentEvent);
                    _forwarder.Start();
                }

                LastErrorKey = null;
                LastErrorDetail = null;
                _log.Action($"Network: connected to host '{pair.HostName}' at {endpoint} as '{stationName}'");
                State = StationState.Connected;
                return ConnectResult.Ok(await WarningsAsync(pair, enabledPlugins, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                channel.Dispose();
                _log.Error("Network: connect failed", ex);
                return Fail("Unreachable", ex.Message);
            }
            finally
            {
                if (_channel is null)
                    State = StationState.Disconnected;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryReconnectSavedAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var settings = await _appStore.GetNetworkSettingsAsync(cancellationToken);
        if (string.IsNullOrEmpty(settings.StationHost) || string.IsNullOrEmpty(settings.StationToken))
            return false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return (await ConnectAsync(settings.StationHost, null, cts.Token)).Success;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LastErrorKey = "Unreachable";
            LastErrorDetail = null;
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_channel is { } channel)
            {
                _log.Action("Network: disconnected from the host");
                // Tell the host we are leaving for good (best-effort, 3 s): it drops our token and our row.
                await channel.TryPostAsync(Wire.UnpairPath, TimeSpan.FromSeconds(3));
            }
            await TearDownAsync(forgetToken: true);
            State = StationState.Disconnected;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Discovery

    public IReadOnlyList<DiscoveredHost> DiscoveredHosts => _discovery.Hosts;

    public void StartDiscovery() => _discovery.Start();

    public void StopDiscovery() => _discovery.Stop();

    public string? DecodeConnectionCode(string code)
        => ConnectionCode.Decode(code) is { } decoded ? $"{decoded.Address}:{decoded.Port}" : null;

    // ── internals

    private static string StationNameOf(NetworkSettings settings)
        => string.IsNullOrWhiteSpace(settings.StationName) ? Environment.MachineName : settings.StationName.Trim();

    private ConnectResult Fail(string key, string? detail = null)
    {
        LastErrorKey = key;
        LastErrorDetail = detail;
        if (key != "HostRoleActive")
            _log.Info($"Network: connect refused — {key}{(detail is null ? string.Empty : $" ({detail})")}");
        return ConnectResult.Fail(key, detail);
    }

    private async Task<IReadOnlyList<DiagnosticCheck>> WarningsAsync(PairResponse pair, IReadOnlyList<string> localPlugins, CancellationToken cancellationToken)
    {
        var warnings = new List<DiagnosticCheck>();

        var missing = pair.Plugins.Except(localPlugins, StringComparer.Ordinal).ToList();
        var extra = localPlugins.Except(pair.Plugins, StringComparer.Ordinal).ToList();
        if (missing.Count > 0 || extra.Count > 0)
        {
            var parts = new List<string>();
            if (missing.Count > 0)
                parts.Add($"missing: {string.Join(", ", missing)}");
            if (extra.Count > 0)
                parts.Add($"extra: {string.Join(", ", extra)}");
            warnings.Add(new DiagnosticCheck("plugins-match", CheckState.Warning, string.Join("; ", parts)));
        }

        try
        {
            var local = (int)await _settings.GetReadoutTypeAsync(cancellationToken);
            if (local != pair.ReadoutType)
                warnings.Add(new DiagnosticCheck("readout-type-match", CheckState.Warning, $"host: {pair.ReadoutType}, station: {local}"));
        }
        catch (Exception ex)
        {
            _log.Error("Network: readout type check", ex);
        }

        var skew = DateTimeOffset.UtcNow - pair.ServerTimeUtc;
        if (skew.Duration() > ClockSkewLimit)
            warnings.Add(new DiagnosticCheck("clock-skew", CheckState.Warning, $"{skew.TotalSeconds:+0.0;-0.0} s"));

        foreach (var w in warnings)
            _log.Info($"Network: check {w.Key} — {w.Detail}");
        return warnings;
    }

    // Stops everything of the current connection; optionally forgets the token (a deliberate disconnect / a 401).
    private async Task TearDownAsync(bool forgetToken)
    {
        _forwarder?.Dispose();
        _forwarder = null;
        if (_listener is { } listener)
        {
            listener.Unauthorized -= OnUnauthorized;
            listener.Dispose();
            _listener = null;
        }
        _remote.Clear();
        _cache?.Dispose();
        _cache = null;
        if (_channel is { } channel)
        {
            channel.Health.Changed -= OnHealthChanged;
            channel.WriteAbandoned -= OnWriteAbandoned;
            channel.Unauthorized -= OnUnauthorized;
            channel.Log = null;
            _channel = null;
            channel.Dispose();
        }
        if (_mode.Role == WorkspaceRole.Station)
            _mode.SetLocal();
        if (forgetToken)
            await ForgetTokenAsync(CancellationToken.None);
    }

    private async Task ForgetTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _appStore.GetNetworkSettingsAsync(cancellationToken);
            settings.StationToken = string.Empty;
            await _appStore.SaveNetworkSettingsAsync(settings, cancellationToken);
        }
        catch (Exception ex)
        {
            _log.Error("Network: forget token", ex);
        }
    }

    // The health decides: offline only after ConnectionHealth.OfflineGrace without any answer from the host (a dropped
    // stream or a blip of a few seconds never blocks editing); back the moment anything answers. Coming back refreshes
    // every page once — they were disabled and may show stale data. A change of the delayed-writes count only re-raises.
    private void OnHealthChanged()
    {
        if (_channel is not { } channel)
            return;
        var health = channel.Health;
        if (health.IsOffline && State == StationState.Connected)
        {
            _offlineSince = health.LastSuccess;
            LastErrorKey = "Unreachable";
            LastErrorDetail = health.LastFailure;
            _log.Info($"Network: no answer from the host for {ConnectionHealth.OfflineGrace.TotalSeconds:0} s — editing paused (last failure: {health.LastFailure})");
            State = StationState.Reconnecting;
        }
        else if (!health.IsOffline && State == StationState.Reconnecting)
        {
            var away = DateTimeOffset.UtcNow - _offlineSince;
            _log.Info($"Network: connection to the host restored after {away.TotalSeconds:0} s");
            LastErrorKey = null;
            LastErrorDetail = null;
            State = StationState.Connected;
            _bus.Replay(new DataChange(DataChangeBus.AnyEvent, DataArea.Any, 0, Host?.HostName ?? string.Empty));
        }
        else
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnWriteAbandoned(string call, Exception ex) => WriteFailed?.Invoke(this, EventArgs.Empty);

    private void OnUnauthorized()
    {
        if (_channel is null)
            return;
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                if (_channel is null)
                    return;
                _log.Info("Network: the host no longer accepts this station's token");
                await TearDownAsync(forgetToken: true);
                LastErrorKey = "Unauthorized";
                LastErrorDetail = null;
                State = StationState.Disconnected;
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    // Infers the current competition from the traffic: the last concrete event path seen on the bus.
    private void OnBusChanged(object? sender, DataChange change)
    {
        if (_listener is null || change.Area is DataArea.Any or DataArea.AppData || change.EventFolderPath == DataChangeBus.AnyEvent)
            return;
        if (_currentEvent is null)
            CurrentEvent = change.EventFolderPath;
    }

    public void Dispose()
    {
        _bus.Changed -= OnBusChanged;
        TearDownAsync(forgetToken: false).GetAwaiter().GetResult();
    }
}
