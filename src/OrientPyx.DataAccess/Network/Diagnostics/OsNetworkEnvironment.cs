using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using OrientPyx.BusinessLogic.Models.Network;
using OrientPyx.BusinessLogic.Serialization;
using OrientPyx.DataAccess.Network.Discovery;
using OrientPyx.DataAccess.Network.Protocol;

namespace OrientPyx.DataAccess.Network.Diagnostics;

/// <summary>
/// The real <see cref="INetworkEnvironment"/>: sockets, <c>netsh</c> / PowerShell / <c>socketfilterfw</c> run hidden
/// with a timeout, the elevated shell, the settings URLs, the HTTP ping and the discovery client. Anything
/// Windows-only is guarded by <see cref="OperatingSystem.IsWindows"/>, macOS-only by <see cref="OperatingSystem.IsMacOS"/>;
/// elsewhere the probes answer <c>null</c>.
/// </summary>
public sealed class OsNetworkEnvironment : INetworkEnvironment
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: the user declined the UAC prompt.
    private const string SocketFilterFw = "/usr/libexec/ApplicationFirewall/socketfilterfw";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PowerShellTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false });

    private readonly DiscoveryClient _discovery;

    public OsNetworkEnvironment(DiscoveryClient discovery) => _discovery = discovery;

    public NetworkOs Os =>
        OperatingSystem.IsWindows() ? NetworkOs.Windows
        : OperatingSystem.IsMacOS() ? NetworkOs.MacOS
        : NetworkOs.Other;

    public bool IsMacOS14OrLater => OperatingSystem.IsMacOSVersionAtLeast(14);

    public string? ApplicationPath => OperatingSystem.IsMacOS() ? MacBundlePath(Environment.ProcessPath) : Environment.ProcessPath;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public IReadOnlyList<LanAddress> Addresses()
        => LocalNetwork.Addresses().Select(a => new LanAddress(a.Ip.ToString(), a.IsLinkLocal, a.InterfaceName)).ToList();

    public bool IsTcpPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public async Task<bool?> WindowsFirewallRuleExistsAsync(string ruleName, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var result = await RunHiddenAsync("netsh", ["advfirewall", "firewall", "show", "rule", $"name={ruleName}"], CommandTimeout, cancellationToken);
        if (result is null)
            return null;
        // Exit 0 + the rule listed ⇒ exists; exit 1 ("No rules match the specified criteria.") ⇒ missing.
        return result.ExitCode == 0 && result.Output.Contains(ruleName, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string?> WindowsNetworkProfileAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        const string script = "(Get-NetConnectionProfile | Select-Object -ExpandProperty NetworkCategory | Sort-Object -Unique) -join ','";
        var result = await RunHiddenAsync("powershell", ["-NoProfile", "-NonInteractive", "-Command", script], PowerShellTimeout, cancellationToken);
        if (result is null || result.ExitCode != 0)
            return null;
        var profile = result.Output.Trim();
        return profile.Length == 0 ? null : profile;
    }

    public async Task<bool?> MacFirewallEnabledAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
            return null;
        var result = await RunHiddenAsync(SocketFilterFw, ["--getglobalstate"], CommandTimeout, cancellationToken);
        if (result is null || result.ExitCode != 0)
            return null;
        // "Firewall is disabled. (State = 0)" / "Firewall is enabled. (State = 1)" / "… (State = 2)" for block-all.
        if (result.Output.Contains("disabled", StringComparison.OrdinalIgnoreCase))
            return false;
        if (result.Output.Contains("enabled", StringComparison.OrdinalIgnoreCase))
            return true;
        return null;
    }

    public async Task<bool?> MacFirewallAllowsAsync(string applicationPath, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
            return null;
        var result = await RunHiddenAsync(SocketFilterFw, ["--listapps"], CommandTimeout, cancellationToken);
        if (result is null || result.ExitCode != 0)
            return null;
        // Each app takes two lines: "N :  /path/App.app" then "( Allow incoming connections )" or "( Block … )".
        var lines = result.Output.Split('\n', StringSplitOptions.TrimEntries);
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (lines[i].Contains(applicationPath, StringComparison.Ordinal))
                return lines[i + 1].Contains("Allow", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public async Task<CommandResult> RunElevatedAsync(string commandLine, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            // One UAC prompt; the console stays hidden. Output cannot be captured through ShellExecute.
            var info = new ProcessStartInfo("cmd.exe", $"/c \"{commandLine}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            };
            try
            {
                using var process = Process.Start(info) ?? throw new InvalidOperationException("The elevated process did not start.");
                await process.WaitForExitAsync(cancellationToken);
                return new CommandResult(process.ExitCode, string.Empty);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return new CommandResult(ex.NativeErrorCode, ex.Message, Cancelled: true);
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            var script = "do shell script \"" + commandLine.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\" with administrator privileges";
            var result = await RunHiddenAsync("osascript", ["-e", script], TimeSpan.FromMinutes(2), cancellationToken)
                         ?? new CommandResult(-1, "osascript did not finish");
            // "User canceled. (-128)" — the password dialog was dismissed.
            var cancelled = result.ExitCode != 0 && result.Output.Contains("-128", StringComparison.Ordinal);
            return cancelled ? result with { Cancelled = true } : result;
        }

        throw new PlatformNotSupportedException("Elevated commands are only supported on Windows and macOS.");
    }

    public void OpenUrl(string url)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start(new ProcessStartInfo("open") { ArgumentList = { url }, UseShellExecute = false });
        else
            Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { url }, UseShellExecute = false });
    }

    public async Task<PingResponse> PingAsync(string endpoint, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var url = (endpoint.Contains("://", StringComparison.Ordinal) ? endpoint : "http://" + endpoint).TrimEnd('/') + Wire.PingPath;
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PingResponse>(RpcJson.Options, cts.Token)
               ?? throw new InvalidOperationException("Empty /ping response.");
    }

    public Task<IReadOnlyList<DiscoveredHost>> ProbeHostsAsync(TimeSpan wait, CancellationToken cancellationToken)
        => _discovery.ProbeOnceAsync(wait, cancellationToken);

    /// <summary>The <c>.app</c> bundle that contains <paramref name="executable"/>, or the executable itself outside a bundle.</summary>
    public static string? MacBundlePath(string? executable)
    {
        if (string.IsNullOrEmpty(executable))
            return executable;
        // Walk the '/'-separated path by hand: Path.GetDirectoryName would rewrite it with the local separator.
        var segments = executable.Split('/');
        for (var i = segments.Length - 2; i >= 0; i--)
        {
            if (segments[i].EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return string.Join('/', segments.Take(i + 1));
        }
        return executable;
    }

    /// <summary>Runs a command with no window, capturing stdout+stderr; null when it could not start or ran past <paramref name="timeout"/>.</summary>
    private static async Task<CommandResult?> RunHiddenAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return null;
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                if (cancellationToken.IsCancellationRequested)
                    throw;
                return null;
            }
            return new CommandResult(process.ExitCode, await stdout + await stderr);
        }
        catch (Win32Exception)
        {
            // The tool is not installed / not on PATH.
            return null;
        }
    }
}
