using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.DataAccess.Network.Station;

namespace OrientPyx.DataAccess.Network;

public static class StationServiceCollectionExtensions
{
    /// <summary>Registers the network station (<c>INetworkClient</c>, the RPC channel and the remote proxies).</summary>
    public static IServiceCollection AddNetworkStation(this IServiceCollection services)
    {
        // The channel, the proxies and the change listener are built per connection inside NetworkStation and
        // handed to RemoteServices (registered by AddOrientPyxDataAccess), which the routers forward to.
        services.AddSingleton<NetworkStation>();
        services.AddSingleton<INetworkClient>(sp => sp.GetRequiredService<NetworkStation>());
        return services;
    }
}
