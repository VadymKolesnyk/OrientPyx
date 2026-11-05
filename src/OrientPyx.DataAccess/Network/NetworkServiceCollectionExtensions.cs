using Microsoft.Extensions.DependencyInjection;
using OrientPyx.DataAccess.Network.Discovery;

namespace OrientPyx.DataAccess.Network;

/// <summary>
/// Registers the network mode («Спільна робота»): the host (Kestrel + endpoints), the station (RPC channel, remote
/// proxies, change listener), discovery and the diagnostics. Called from <c>AddOrientPyxDataAccess</c>; each part keeps
/// its registrations in its own file so they can evolve independently.
/// </summary>
public static class NetworkServiceCollectionExtensions
{
    public static IServiceCollection AddOrientPyxNetwork(this IServiceCollection services)
    {
        services.AddSingleton<DiscoveryResponder>();
        services.AddSingleton<DiscoveryClient>();

        services.AddNetworkHost();
        services.AddNetworkStation();
        services.AddNetworkDiagnostics();
        return services;
    }
}
