using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Services.Mcp;

namespace OrientPyx.DataAccess.Mcp;

/// <summary>
/// The Claude Desktop connection (plan §4.3): an <c>.mcpb</c> extension whose command is this program with
/// <c>--mcp-bridge</c> (the installed Velopack path <c>…\OrientPyx\current\OrientPyx.exe</c> is stable across updates), or —
/// when Claude Desktop does not take an extension whose command lives outside the archive — an entry in its config.
/// </summary>
public sealed class McpClientSetup : IMcpClientSetup
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public string? BridgeCommand => Environment.ProcessPath;

    public async Task<string> CreateClaudeDesktopExtensionAsync(CancellationToken cancellationToken = default)
    {
        var command = BridgeCommand ?? throw new InvalidOperationException("The program path is unknown.");
        var manifest = new JsonObject
        {
            ["manifest_version"] = "0.2",
            ["name"] = McpInstructions.ServerName,
            ["display_name"] = McpInstructions.ServerTitle,
            ["version"] = McpContext.AppVersion,
            ["description"] = "OrientPyx — orienteering competitions: analytics, changes and setup from the chat (local).",
            ["author"] = new JsonObject { ["name"] = "OrientPyx" },
            ["server"] = new JsonObject
            {
                ["type"] = "binary",
                ["entry_point"] = command,
                ["mcp_config"] = new JsonObject
                {
                    ["command"] = command,
                    ["args"] = new JsonArray(McpBridge.Argument),
                },
            },
            ["tools_generated"] = true,
            ["prompts_generated"] = true,
        };

        var folder = Path.Combine(Path.GetTempPath(), "OrientPyx-mcp");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "OrientPyx.mcpb");
        await using (var file = File.Create(path))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("manifest.json");
            await using var stream = entry.Open();
            await JsonSerializer.SerializeAsync(stream, manifest, Indented, cancellationToken);
        }
        return path;
    }

    public async Task<string> AddToClaudeDesktopConfigAsync(CancellationToken cancellationToken = default)
    {
        var command = BridgeCommand ?? throw new InvalidOperationException("The program path is unknown.");
        var path = ClaudeDesktopConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonObject root;
        if (File.Exists(path))
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken);
            File.Copy(path, $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: false);
            root = (string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject) ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        if (root["mcpServers"] is not JsonObject servers)
            root["mcpServers"] = servers = new JsonObject();
        servers[McpInstructions.ServerName] = new JsonObject
        {
            ["command"] = command,
            ["args"] = new JsonArray(McpBridge.Argument),
        };
        await File.WriteAllTextAsync(path, root.ToJsonString(Indented), cancellationToken);
        return path;
    }

    /// <summary>Claude Desktop's config file on this OS.</summary>
    public static string ClaudeDesktopConfigPath() =>
        OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Claude", "claude_desktop_config.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");
}
