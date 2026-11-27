using System.Diagnostics;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Mcp;

namespace OrientPyx.BusinessLogic.Services.Mcp;

/// <summary>A group of tools implemented together (they share helpers). Returns any value; it is sent as JSON.</summary>
public interface IMcpToolHandler
{
    IReadOnlyCollection<string> Tools { get; }

    Task<object?> InvokeAsync(string tool, McpArgs args, McpCall call, CancellationToken cancellationToken);
}

/// <summary>The text the assistant receives for one call.</summary>
public sealed record McpToolResult(string Text, bool IsError)
{
    /// <summary>Answers longer than this are cut (with a hint to aggregate) — a client would choke on more.</summary>
    public const int MaxChars = 400_000;

    public static McpToolResult Ok(object? value)
    {
        var text = value as string ?? McpJson.Serialize(value);
        if (text.Length > MaxChars)
            text = text[..MaxChars] + "\n… [answer cut: too large — ask for less (limit, groups, a day) or aggregate with query]";
        return new McpToolResult(text, false);
    }

    public static McpToolResult Error(string message) => new(message, true);
}

/// <summary>
/// The server-independent core of the assistant: lists the tools the mode allows and runs one call — policy, error
/// translation, the activity log. The HTTP server and the tests both call it; it knows nothing about MCP transports.
/// </summary>
public sealed class McpToolDispatcher
{
    private readonly Dictionary<string, IMcpToolHandler> _handlers = new(StringComparer.Ordinal);

    public McpToolDispatcher(IEnumerable<IMcpToolHandler> handlers, McpContext context)
    {
        Context = context;
        foreach (var handler in handlers)
            foreach (var tool in handler.Tools)
                _handlers[tool] = handler;
    }

    public McpContext Context { get; }

    /// <summary>The tools of the catalogue that have an implementation.</summary>
    public IEnumerable<string> Implemented => _handlers.Keys;

    /// <summary>The tools the assistant may see now.</summary>
    public IReadOnlyList<McpToolInfo> ListedTools() =>
        McpToolCatalog.All.Where(t => _handlers.ContainsKey(t.Name) && McpToolPolicy.IsListed(t.Class, Context.Runtime.Mode)).ToList();

    public async Task<McpToolResult> CallAsync(string name, McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        McpToolResult result;
        try
        {
            result = McpToolResult.Ok(await InvokeAsync(name, args, call, cancellationToken));
        }
        catch (McpToolException ex)
        {
            result = McpToolResult.Error(ex.Message);
        }
        catch (DayLockedException ex)
        {
            result = McpToolResult.Error(
                $"Day {ex.DayNumber} is closed for edits (закритий день); nothing was changed. Only the user can open it in OrientPyx: «Змагання → Дні».");
        }
        catch (AnalyticsQueryException ex)
        {
            result = McpToolResult.Error("SQL error: " + ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Context.Log?.Error($"MCP tool {name}", ex);
            result = McpToolResult.Error($"OrientPyx could not do it ({ex.GetType().Name}): {ex.Message}");
        }

        watch.Stop();
        Context.Log?.Info($"MCP {name} {Short(args)} → {(result.IsError ? "error: " + Short(result.Text) : "ok")} {watch.ElapsedMilliseconds} ms");
        try
        {
            Context.Find<IMcpUi>()?.CallFinished(new McpCallRecord(DateTimeOffset.Now, name, !result.IsError, watch.Elapsed, call.Client));
        }
        catch (Exception ex)
        {
            Context.Log?.Error("MCP: activity update", ex);
        }
        return result;
    }

    private async Task<object?> InvokeAsync(string name, McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        if (McpToolCatalog.Find(name) is not { } info || !_handlers.TryGetValue(name, out var handler))
            throw new McpToolException($"Unknown tool '{name}'.");
        if (!McpToolPolicy.IsListed(info.Class, Context.Runtime.Mode))
            throw new McpToolException(
                $"'{name}' is switched off: OrientPyx lets the assistant only read now (mode «Лише читання»). The user can allow " +
                "changes in «Налаштування → Асистент (MCP)».");
        return await handler.InvokeAsync(name, args, call, cancellationToken);
    }

    private static string Short(object? value)
    {
        var s = value?.ToString() ?? string.Empty;
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= 200 ? s : s[..200] + "…";
    }
}
