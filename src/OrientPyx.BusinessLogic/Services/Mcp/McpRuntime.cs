using System.Collections.Concurrent;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models.Mcp;

namespace OrientPyx.BusinessLogic.Services.Mcp;

/// <summary>
/// The in-memory state of the assistant for this run: the mode the server applies and the tools the user allowed
/// «до кінця сесії» in the confirmation bar (forgotten when the server restarts or the program closes).
/// </summary>
public sealed class McpRuntime
{
    private readonly ConcurrentDictionary<string, bool> _allowed = new(StringComparer.Ordinal);

    public McpMode Mode { get; private set; } = McpMode.ReadOnly;

    /// <summary>Raised after <see cref="Mode"/> changed (the server re-lists the tools).</summary>
    public event EventHandler? ModeChanged;

    public void SetMode(McpMode mode)
    {
        if (Mode == mode)
            return;
        Mode = mode;
        if (mode == McpMode.ReadOnly)
            _allowed.Clear();
        ModeChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsAllowedForSession(string tool) => _allowed.ContainsKey(tool);

    public void AllowForSession(string tool) => _allowed[tool] = true;

    public void ResetSession() => _allowed.Clear();
}

/// <summary>
/// The origin of the assistant's writes on the change bus (plan М2): <c>"&lt;SelfOrigin&gt;·mcp"</c>. Pages skip only events
/// stamped exactly with their machine's <see cref="IWorkspaceMode.SelfOrigin"/>, so they reload after the assistant's
/// change; on a station the tag travels to the host (<c>X-OrientPyx-Origin-Tag</c>) and comes back as "&lt;station&gt;·mcp".
/// </summary>
public static class McpOrigin
{
    public const string Suffix = "·mcp";

    /// <summary>The value of the station → host tag header.</summary>
    public const string Tag = "mcp";

    public static string For(IWorkspaceMode mode) => mode.SelfOrigin + Suffix;

    /// <summary>True when the write in progress is the assistant's.</summary>
    public static bool IsCurrent => Network.DataChangeOrigin.Current?.EndsWith(Suffix, StringComparison.Ordinal) == true;

    /// <summary>The origin a host stamps on a station's tagged write.</summary>
    public static string ForStation(string stationName) => stationName + Suffix;
}
