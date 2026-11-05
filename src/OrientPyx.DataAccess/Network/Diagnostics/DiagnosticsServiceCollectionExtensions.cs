using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.DataAccess.Network.Diagnostics;

namespace OrientPyx.DataAccess.Network;

public static class DiagnosticsServiceCollectionExtensions
{
    /// <summary>Registers the self-checks (<c>IStationDiagnostics</c>) over the real OS environment.</summary>
    public static IServiceCollection AddNetworkDiagnostics(this IServiceCollection services)
    {
        services.AddSingleton<INetworkEnvironment, OsNetworkEnvironment>();
        services.AddSingleton<IStationDiagnostics, StationDiagnostics>();
        return services;
    }
}
