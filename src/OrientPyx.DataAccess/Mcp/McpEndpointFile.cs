using System.Diagnostics;
using System.Text.Json;
using OrientPyx.BusinessLogic.Models.Mcp;
using OrientPyx.DataAccess.Persistence;

namespace OrientPyx.DataAccess.Mcp;

/// <summary>What a running window with the assistant on tells the bridge (and the other windows).</summary>
public sealed record McpEndpoint(
    int Port,
    string Token,
    int Pid,
    DateTimeOffset ProcessStart,
    McpMode Mode,
    string AppVersion,
    string? Competition)
{
    public string Url => $"http://127.0.0.1:{Port}/mcp";
}

/// <summary>
/// <c>&lt;data root&gt;/mcp-endpoint.json</c> (plan §4.2 / М15): written by the serving window, read by the stdio bridge on
/// every request and by the other windows of this data root. Left in place when the program closes (the dead pid reads
/// as «OrientPyx не запущено», its mode still lists the tools), deleted when the assistant is switched off (the bridge
/// answers «MCP вимкнено»). Atomic replace; owner-only on Unix (it holds the token).
/// </summary>
public static class McpEndpointFile
{
    public const string FileName = "mcp-endpoint.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string PathIn(string dataRoot) => Path.Combine(dataRoot, FileName);

    public static string DefaultPath => PathIn(AppDatabasePaths.BaseDirectory);

    public static McpEndpoint? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<McpEndpoint>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write(string path, McpEndpoint endpoint)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(endpoint, Json));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another process may hold it for a moment; the next start rewrites it anyway.
        }
    }

    /// <summary>True when the process that wrote <paramref name="endpoint"/> still runs (same pid AND start time — pids are reused).</summary>
    public static bool IsAlive(McpEndpoint endpoint)
    {
        try
        {
            using var process = Process.GetProcessById(endpoint.Pid);
            if (process.HasExited)
                return false;
            var started = new DateTimeOffset(process.StartTime);
            return Math.Abs((started - endpoint.ProcessStart).TotalSeconds) < 2;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>This process, as an endpoint's pid + start time.</summary>
    public static (int Pid, DateTimeOffset Start) Self()
    {
        using var process = Process.GetCurrentProcess();
        return (process.Id, new DateTimeOffset(process.StartTime));
    }
}
