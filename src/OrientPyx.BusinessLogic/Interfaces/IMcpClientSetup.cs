namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>Connecting the user's chat clients to the assistant (plan §4): the Claude Desktop extension and its fallback.</summary>
public interface IMcpClientSetup
{
    /// <summary>The program file a client starts as the stdio bridge (<c>… --mcp-bridge</c>); null when unknown.</summary>
    string? BridgeCommand { get; }

    /// <summary>Writes the Claude Desktop extension (<c>OrientPyx.mcpb</c>, command = this program + <c>--mcp-bridge</c>) to a
    /// temporary folder and returns its path; opening it makes Claude Desktop offer to install it.</summary>
    Task<string> CreateClaudeDesktopExtensionAsync(CancellationToken cancellationToken = default);

    /// <summary>The fallback: adds <c>mcpServers.orientpyx</c> to Claude Desktop's config (a backup copy first). Returns the
    /// config path; Claude Desktop must be restarted.</summary>
    Task<string> AddToClaudeDesktopConfigAsync(CancellationToken cancellationToken = default);
}
