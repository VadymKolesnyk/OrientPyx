namespace OrientPyx.BusinessLogic.Models.Mcp;

/// <summary>What the assistant (MCP) may do on this machine.</summary>
public enum McpMode
{
    /// <summary>Reading only: the write tools are not even listed.</summary>
    ReadOnly = 0,

    /// <summary>Every change waits for «Дозволити» in OrientPyx.</summary>
    ConfirmChanges = 1,

    /// <summary>Undoable changes go straight through; risky ones (not undoable, bulk) still wait for «Дозволити».</summary>
    AutoChanges = 2,

    /// <summary>Nothing waits for «Дозволити», risky changes included; only what is never done via MCP stays refused.</summary>
    NoConfirmation = 3,
}

/// <summary>
/// The local MCP server settings of this machine (app.db). Nothing is generated until the switch is turned on: the
/// port is chosen once on the first start and never changes by itself, the token lives until «Новий токен».
/// </summary>
public sealed class McpSettings
{
    public bool Enabled { get; set; }

    public McpMode Mode { get; set; } = McpMode.ReadOnly;

    /// <summary>The loopback port; 0 = not chosen yet (the first start picks 47833 or the next free one).</summary>
    public int Port { get; set; }

    /// <summary>The bearer token of the HTTP clients; blank = not generated yet.</summary>
    public string Token { get; set; } = string.Empty;

    public McpSettings Clone() => new() { Enabled = Enabled, Mode = Mode, Port = Port, Token = Token };
}
