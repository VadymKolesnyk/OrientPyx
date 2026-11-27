using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Services.Mcp;

namespace OrientPyx.DataAccess.Mcp;

/// <summary>The infrastructure of the assistant: the HTTP server, the SQL sandbox, local files, the client setup.</summary>
public static class McpDataAccessServiceCollectionExtensions
{
    /// <summary>Registers the assistant (core + server). The window registers its own <see cref="IMcpUi"/>.</summary>
    public static IServiceCollection AddOrientPyxMcp(this IServiceCollection services, McpHostOptions? options = null)
    {
        services.AddOrientPyxMcpCore();
        services.AddSingleton<IAnalyticsSandbox, AnalyticsSandbox>();
        services.AddSingleton<IMcpFileAccess, McpFileAccess>();
        services.AddSingleton<IMcpClientSetup, McpClientSetup>();
        services.AddSingleton(options ?? new McpHostOptions());
        services.AddSingleton<McpHttpServer>(sp => new McpHttpServer(
            sp.GetRequiredService<IAppStore>(),
            sp.GetRequiredService<McpToolDispatcher>(),
            sp.GetRequiredService<McpRuntime>(),
            sp.GetRequiredService<McpConfirmations>(),
            sp.GetRequiredService<ISessionService>(),
            sp,
            sp.GetService<IActivityLog>(),
            sp.GetRequiredService<McpHostOptions>()));
        services.AddSingleton<IMcpServer>(sp => sp.GetRequiredService<McpHttpServer>());
        return services;
    }
}
