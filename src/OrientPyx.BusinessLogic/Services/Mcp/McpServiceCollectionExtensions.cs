using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Services.Mcp.Tools;

namespace OrientPyx.BusinessLogic.Services.Mcp;

/// <summary>The transport-independent core of the assistant (the tools, the policy, the confirmations, the plans).</summary>
public static class McpServiceCollectionExtensions
{
    public static IServiceCollection AddOrientPyxMcpCore(this IServiceCollection services)
    {
        services.AddSingleton<McpRuntime>();
        services.AddSingleton(sp => new McpConfirmations(() => sp.GetRequiredService<IMcpUi>(), sp.GetRequiredService<McpRuntime>()));
        services.AddSingleton<McpPlanStore>();
        services.AddSingleton(sp => new McpContext(
            sp, sp.GetRequiredService<McpRuntime>(), sp.GetRequiredService<McpConfirmations>(), sp.GetRequiredService<McpPlanStore>()));

        services.AddSingleton<IMcpToolHandler, ContextTools>();
        services.AddSingleton<IMcpToolHandler, FindTools>();
        services.AddSingleton<IMcpToolHandler, DataTools>();
        services.AddSingleton<IMcpToolHandler, ResultTools>();
        services.AddSingleton<IMcpToolHandler, SettingsTools>();
        services.AddSingleton<IMcpToolHandler, ParticipantTools>();
        services.AddSingleton<IMcpToolHandler, CourseTools>();
        services.AddSingleton<IMcpToolHandler, ManageTools>();
        services.AddSingleton<IMcpToolHandler, OperationsTools>();
        services.AddSingleton<IMcpToolHandler, ExportTools>();
        services.AddSingleton<IMcpToolHandler, ReadoutTools>();
        services.AddSingleton<IMcpToolHandler, DuplicateTools>();
        services.AddSingleton<IMcpToolHandler, FileTools>();
        services.AddSingleton<IMcpToolHandler, GroupRuleTools>();
        services.AddSingleton<McpToolDispatcher>();
        return services;
    }
}
