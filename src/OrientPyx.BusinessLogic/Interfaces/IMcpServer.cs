using OrientPyx.BusinessLogic.Models.Mcp;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>The state of the local MCP server of this window.</summary>
public enum McpServerState
{
    /// <summary>Switched off (the default): no port, no discovery file.</summary>
    Off,

    /// <summary>Serving on <see cref="IMcpServer.Port"/>.</summary>
    Running,

    /// <summary>Another OrientPyx window of this computer serves; this one takes over when that one closes.</summary>
    ServedElsewhere,

    /// <summary>The remembered port is taken by another program.</summary>
    PortBusy,

    /// <summary>Starting failed (see <see cref="IMcpServer.Error"/>).</summary>
    Failed,
}

/// <summary>
/// The local MCP server («Асистент»): Streamable HTTP on 127.0.0.1 inside the running window (plan §3.1). Switched on
/// and off without a restart; the settings live in app.db (<see cref="IAppStore.GetMcpSettingsAsync"/>).
/// </summary>
public interface IMcpServer
{
    McpServerState State { get; }

    /// <summary>The port in use (Running) or remembered; null before the first start.</summary>
    int? Port { get; }

    /// <summary><c>http://127.0.0.1:port/mcp</c> while running.</summary>
    string? Url { get; }

    McpMode Mode { get; }

    /// <summary>ServedElsewhere: the name of the competition open in the serving window.</summary>
    string? ServedBy { get; }

    /// <summary>Failed / PortBusy: what went wrong (English, for the log).</summary>
    string? Error { get; }

    /// <summary>Clients with an open session (HTTP sessions; the bridge counts as one).</summary>
    int ConnectedClients { get; }

    /// <summary>Reads the settings and starts, stops or re-moods the server accordingly. Never throws.</summary>
    Task ApplySettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops serving (the window closes); the settings stay as they are.</summary>
    Task StopAsync();

    event EventHandler? Changed;
}
