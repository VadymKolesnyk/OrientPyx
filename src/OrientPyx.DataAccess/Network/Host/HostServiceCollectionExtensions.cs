using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.DataAccess.Network.Host;

namespace OrientPyx.DataAccess.Network;

public static class HostServiceCollectionExtensions
{
    /// <summary>Registers the network host (<c>INetworkHost</c> and its endpoints).</summary>
    public static IServiceCollection AddNetworkHost(this IServiceCollection services)
    {
        // One host per process; the Kestrel server, the dispatcher and the station registry live inside it. Services
        // behind the endpoints (the stores, the catalog, the session) are resolved lazily per request to keep the DI
        // graph free of cycles (the catalog takes the host to refuse renames while stations work on a competition).
        services.AddSingleton<NetworkHost>();
        services.AddSingleton<INetworkHost>(sp => sp.GetRequiredService<NetworkHost>());
        return services;
    }
}
