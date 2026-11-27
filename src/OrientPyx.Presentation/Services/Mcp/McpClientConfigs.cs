using System.Text.Json;
using System.Text.Json.Nodes;
using OrientPyx.BusinessLogic.Services.Mcp;

namespace OrientPyx.Presentation.Services.Mcp;

/// <summary>
/// «Скопіювати налаштування для…» (plan §4.1): a ready piece of each client's own config — not UI text, the format of
/// that client. HTTP + bearer header; for a client without one, the stdio bridge command.
/// </summary>
public static class McpClientConfigs
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string For(string client, string url, string token, string? bridgeCommand) => client switch
    {
        "claude_code" => $"claude mcp add --transport http {McpInstructions.ServerName} {url} --header \"Authorization: Bearer {token}\"",
        "gemini_cli" => Json(new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [McpInstructions.ServerName] = new JsonObject
                {
                    ["httpUrl"] = url,
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + token },
                    ["timeout"] = 120000,
                },
            },
        }),
        "vscode" => Json(new JsonObject
        {
            ["servers"] = new JsonObject
            {
                [McpInstructions.ServerName] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = url,
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + token },
                },
            },
        }),
        _ => $"URL: {url}\nHeader: Authorization: Bearer {token}" +
             (bridgeCommand is null ? string.Empty : $"\n\nstdio: \"{bridgeCommand}\" --mcp-bridge"),
    };

    private static string Json(JsonNode node) => node.ToJsonString(Indented);
}
