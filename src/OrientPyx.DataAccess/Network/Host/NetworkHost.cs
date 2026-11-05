using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models.Network;
using OrientPyx.BusinessLogic.Services.Network;
using OrientPyx.DataAccess.Network.Discovery;
using OrientPyx.DataAccess.Network.Protocol;

namespace OrientPyx.DataAccess.Network.Host;

/// <summary>Where the host listens: the bind address and the port (0 ⇒ any free port; used by the tests).</summary>
public sealed record HostStartOptions(IPAddress Address, int Port);

/// <summary>
/// This computer as the host (<see cref="INetworkHost"/>): an in-process Kestrel on the LAN port serving the
/// competition data to stations (<see cref="HostEndpoints"/>), the UDP discovery responder, the PIN and the station
/// tokens. Starting switches the workspace to <see cref="WorkspaceRole.Host"/>; stopping switches it back to local.
/// </summary>
public sealed class NetworkHost : INetworkHost
{
    private const int PortProbeRange = 20;

    private readonly IServiceProvider _services;
    private readonly IAppStore _appStore;
    private readonly IActivityLog _log;
    private readonly WorkspaceMode _mode;
    private readonly IDataChangeBus _bus;
    private readonly DiscoveryResponder _discovery;
    private readonly StationRegistry _stations;
    private readonly HostEndpoints _endpoints;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WebApplication? _app;
    private string _hostId = string.Empty;
    private string _hostName = string.Empty;
    private int _port;
    private int _lastStationCount = -1;

    public NetworkHost(
        IServiceProvider services,
        IAppStore appStore,
        IAppSettingsService settings,
        IActivityLog log,
        WorkspaceMode mode,
        IDataChangeBus bus,
        DiscoveryResponder discovery)
    {
        _services = services;
        _appStore = appStore;
        _log = log;
        _mode = mode;
        _bus = bus;
        _discovery = discovery;
        _stations = new StationRegistry(appStore);
        var mapper = new EventPathMapper(settings);
        var jobs = new JobRegistry();
        _endpoints = new HostEndpoints(
            services,
            _stations,
            new RpcDispatcher(services, mapper, jobs),
            new IdempotencyCache(),
            jobs,
            new ChangeStream(bus),
            mapper,
            bus,
            log,
            () => _hostId,
            () => _hostName,
            OnRequest);
    }

    public bool IsRunning => _app is not null;

    public HostInfo? Info => _mode.Host;

    /// <summary>The spoken fallback for finding the host: the first usable LAN address + port as a 12-character code.</summary>
    public string ConnectionCode
    {
        get
        {
            if (_mode.Host is not { } host)
                return string.Empty;
            var address = Discovery.LocalNetwork.Addresses().FirstOrDefault(a => !a.IsLinkLocal);
            return address is null ? string.Empty : Discovery.ConnectionCode.Encode(address.Ip, host.Port);
        }
    }

    public IReadOnlyList<ConnectedStation> Stations => _stations.Snapshot();

    public event EventHandler? Changed;

    public Task StartAsync(CancellationToken cancellationToken = default)
        => StartAsync(null, cancellationToken);

    /// <summary>Starts on an explicit address/port (the tests bind the loopback on a free port).</summary>
    public async Task StartAsync(HostStartOptions? options, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_app is not null)
                return;
            if (_mode.Role == WorkspaceRole.Station)
                throw new InvalidOperationException("This instance is connected to a host as a station.");

            var settings = await _appStore.GetNetworkSettingsAsync(cancellationToken);
            if (string.IsNullOrEmpty(settings.HostId))
            {
                settings.HostId = Guid.NewGuid().ToString("N");
                await _appStore.SaveNetworkSettingsAsync(settings, cancellationToken);
            }
            _hostId = settings.HostId;
            _hostName = string.IsNullOrWhiteSpace(settings.HostName) ? Environment.MachineName : settings.HostName.Trim();

            await _stations.LoadAsync(cancellationToken);
            _stations.RegeneratePin();

            var address = options?.Address ?? IPAddress.Any;
            var wantedPort = options?.Port ?? (settings.HostPort > 0 ? settings.HostPort : NetworkProtocol.DefaultApiPort);
            (_app, _port) = await BindAsync(address, wantedPort, cancellationToken);

            _discovery.Describe = Describe;
            _discovery.Start();

            _lastStationCount = _stations.Count;
            _mode.SetHost(BuildInfo());
            _log.Action($"Network host started on port {_port} as '{_hostName}'");
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_app is null)
                return;
            _discovery.Describe = null;
            _discovery.Stop();
            try
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _app.StopAsync(stop.Token);
            }
            catch (Exception ex)
            {
                _log.Error("Network host stop", ex);
            }
            await _app.DisposeAsync();
            _app = null;
            _mode.SetLocal();
            _log.Action("Network host stopped");
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string RegeneratePin()
    {
        var pin = _stations.RegeneratePin();
        if (_app is not null)
            _mode.UpdateHost(BuildInfo());
        Changed?.Invoke(this, EventArgs.Empty);
        return pin;
    }

    public async Task DisconnectStationAsync(string token, CancellationToken cancellationToken = default)
    {
        await _stations.RevokeAsync(token, cancellationToken);
        _log.Action("Network host: a station was disconnected");
        RefreshInfo();
    }

    public async Task DisconnectAllAsync(CancellationToken cancellationToken = default)
    {
        await _stations.RevokeAllAsync(cancellationToken);
        _log.Action("Network host: every station was disconnected");
        RefreshInfo();
    }

    public async Task SetStationTrustedAsync(string token, bool trusted, CancellationToken cancellationToken = default)
    {
        await _stations.SetTrustedAsync(token, trusted, cancellationToken);
        _log.Action(trusted ? "Network host: a station's device was marked trusted" : "Network host: a station's device is no longer trusted");
        RefreshInfo();
    }

    public IReadOnlyList<string> ActiveStationsOn(string identifier)
        => _app is null ? [] : _stations.ActiveOn(identifier);

    /// <summary>The PIN stations must enter (for the tests and the page).</summary>
    public string Pin => _stations.Pin;

    // ── internals

    private async Task<(WebApplication App, int Port)> BindAsync(IPAddress address, int wantedPort, CancellationToken cancellationToken)
    {
        Exception? last = null;
        var attempts = wantedPort == 0 ? 1 : PortProbeRange;
        for (var i = 0; i < attempts; i++)
        {
            var port = wantedPort == 0 ? 0 : wantedPort + i;
            var app = Build(address, port);
            try
            {
                await app.StartAsync(cancellationToken);
                var bound = port != 0 ? port : BoundPort(app);
                if (i > 0)
                    _log.Info($"Network host: port {wantedPort} is taken, listening on {bound}");
                return (app, bound);
            }
            catch (IOException ex)
            {
                last = ex;
                await app.DisposeAsync();
            }
        }
        throw new InvalidOperationException($"No free port from {wantedPort} to {wantedPort + attempts - 1}.", last);
    }

    private WebApplication Build(IPAddress address, int port)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(address, port);
            kestrel.Limits.MaxRequestBodySize = null; // .opyx uploads and large imports from stations
            kestrel.AddServerHeader = false;
        });
        builder.Services.AddResponseCompression(options =>
        {
            options.EnableForHttps = false;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            // Only JSON is compressed: the change stream must go out unbuffered, archives are already zipped.
            options.MimeTypes = [Wire.JsonContentType];
        });

        var app = builder.Build();
        app.UseResponseCompression();
        app.Run(_endpoints.HandleAsync);
        return app;
    }

    private static int BoundPort(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        foreach (var address in addresses ?? [])
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
                return uri.Port;
        throw new InvalidOperationException("Kestrel reported no bound address.");
    }

    private DiscoveryResponse? Describe()
    {
        if (_app is null)
            return null;
        // Resolved lazily: the session depends on services that depend on this host.
        var competition = _services.GetService<ISessionService>()?.CurrentEvent?.Name;
        return new DiscoveryResponse(_hostId, _hostName, Wire.AppVersion, LocalNetwork.UsableAddressStrings(), _port, competition);
    }

    private HostInfo BuildInfo()
        => new(_hostId, _hostName, _port, _stations.Pin, LocalNetwork.UsableAddressStrings(), _stations.Count);

    // A station paired, was revoked or made a request: refresh the host card when the count moved and tell the page.
    private void OnRequest()
    {
        if (_app is null)
            return;
        if (_stations.Count != _lastStationCount)
            RefreshInfo();
        else
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshInfo()
    {
        _lastStationCount = _stations.Count;
        if (_app is not null)
            _mode.UpdateHost(BuildInfo());
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
