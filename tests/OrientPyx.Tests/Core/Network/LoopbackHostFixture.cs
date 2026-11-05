using System.Net;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models.Network;
using OrientPyx.DataAccess.DependencyInjection;
using OrientPyx.DataAccess.Network.Host;
using OrientPyx.DataAccess.Network.Station;
using OrientPyx.Localization.DependencyInjection;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core.Network;

/// <summary>
/// The network host of the shared test environment listening on the loopback (a free port) plus a second DI
/// container acting as a station connected to it with the PIN. Both containers run over the same data root — the
/// station only needs <c>app.db</c> for its own settings; its competition data comes from the host over HTTP.
/// </summary>
public sealed class LoopbackHostFixture : IAsyncLifetime
{
    public const string StationName = "test-station";

    private LastSession? _savedPointer;

    public NetworkHost Host { get; private set; } = null!;

    public IServiceProvider HostServices => TestEnvironment.Instance.Services;

    public IServiceProvider StationServices { get; private set; } = null!;

    public NetworkStation Station { get; private set; } = null!;

    public string Endpoint => $"127.0.0.1:{Host.Info!.Port}";

    public async Task InitializeAsync()
    {
        var env = TestEnvironment.Instance;
        _savedPointer = await env.Services.GetRequiredService<IAppStore>().GetLastSessionAsync();

        Host = (NetworkHost)env.Services.GetRequiredService<INetworkHost>();
        await Host.StartAsync(new HostStartOptions(IPAddress.Loopback, 0));

        var services = new ServiceCollection();
        services.AddOrientPyxLocalization();
        services.AddOrientPyxBusinessLogic();
        services.AddOrientPyxDataAccess();
        StationServices = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        StationServices.InitializeOrientPyxDatabase();

        var appStore = StationServices.GetRequiredService<IAppStore>();
        var settings = await appStore.GetNetworkSettingsAsync();
        settings.StationName = StationName;
        await appStore.SaveNetworkSettingsAsync(settings);

        Station = (NetworkStation)StationServices.GetRequiredService<INetworkClient>();
        await ConnectAsync();
    }

    /// <summary>Pairs the station with the host using the current PIN (also used after the host revoked the token).</summary>
    public async Task ConnectAsync()
    {
        var result = await Station.ConnectAsync(Endpoint, Host.Pin);
        if (!result.Success)
            throw new InvalidOperationException($"Station could not connect: {result.ErrorKey} {result.Detail}");
        StationServices.GetRequiredService<ISessionService>().Clear();
    }

    public async Task DisposeAsync()
    {
        try
        {
            await Station.DisconnectAsync();
        }
        catch
        {
            // best effort
        }
        await Host.StopAsync();
        // The station's session writes a station-mode pointer into the shared app.db; put the host's one back.
        if (_savedPointer is not null)
            await HostServices.GetRequiredService<IAppStore>().SaveLastSessionAsync(_savedPointer);
    }
}
