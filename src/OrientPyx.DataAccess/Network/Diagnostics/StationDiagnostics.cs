using System.Net.Sockets;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models.Network;
using OrientPyx.DataAccess.Network.Protocol;

namespace OrientPyx.DataAccess.Network.Diagnostics;

/// <summary>
/// The self-checks of «Спільна робота» (plan §8.2–8.3) over an <see cref="INetworkEnvironment"/>. Nothing is changed
/// silently: a check only reports and offers a <see cref="DiagnosticFix"/>; the change happens in
/// <see cref="ApplyFixAsync"/> on «Виправити», is written to the activity log and has an inverse (the firewall rule can
/// be removed again). An OS probe that throws becomes a <see cref="CheckState.Skipped"/> check, never an exception.
/// </summary>
public sealed class StationDiagnostics : IStationDiagnostics
{
    /// <summary>The name of the Windows firewall rule the fix creates (both TCP and UDP rules share it).</summary>
    public const string FirewallRuleName = "OrientPyx";

    public static class Checks
    {
        public const string LanAddress = "lan-address";
        public const string PortFree = "port-free";
        public const string FirewallRule = "firewall-rule";
        public const string NetworkProfile = "network-profile";
        public const string SelfReach = "self-reach";
        public const string LocalNetworkPermission = "local-network-permission";
        public const string HostDiscovered = "host-discovered";
        public const string HostReachable = "host-reachable";
        public const string VersionCompatible = "version-compatible";
        public const string ClockSkew = "clock-skew";
    }

    public static class Fixes
    {
        public const string OpenHotspotSettings = "open-hotspot-settings";
        public const string AddFirewallRule = "add-firewall-rule";
        public const string RemoveFirewallRule = "remove-firewall-rule";
        public const string AllowMacFirewall = "allow-mac-firewall";
        public const string OpenFirewallSettings = "open-firewall-settings";
        public const string OpenLocalNetworkSettings = "open-local-network-settings";
    }

    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LoopbackPingTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ProbeWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromSeconds(2);

    private readonly INetworkEnvironment _env;
    private readonly IActivityLog _log;

    public StationDiagnostics(INetworkEnvironment environment, IActivityLog log)
    {
        _env = environment;
        _log = log;
    }

    // ---- host ----------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<DiagnosticCheck>> RunHostChecksAsync(int port, CancellationToken cancellationToken = default)
    {
        var lan = await GuardAsync(Checks.LanAddress, _ => Task.FromResult(CheckLanAddress()), cancellationToken);
        var ownHost = await OwnHostAsync(port, cancellationToken);
        var portFree = await GuardAsync(Checks.PortFree, _ => Task.FromResult(CheckPortFree(port, ownHost)), cancellationToken);
        var firewall = await GuardAsync(Checks.FirewallRule, CheckFirewallRuleAsync, cancellationToken);
        var profile = await GuardAsync(Checks.NetworkProfile, CheckNetworkProfileAsync, cancellationToken);
        var reach = await GuardAsync(Checks.SelfReach, ct => CheckSelfReachAsync(port, ownHost, firewall, profile, ct), cancellationToken);
        return [lan, portFree, firewall, profile, reach];
    }

    private DiagnosticCheck CheckLanAddress()
    {
        var addresses = _env.Addresses();
        var usable = addresses.Where(a => !a.IsLinkLocal).ToList();
        if (usable.Count > 0)
            return new DiagnosticCheck(Checks.LanAddress, CheckState.Ok, string.Join(", ", usable.Select(a => $"{a.Ip} ({a.InterfaceName})")));

        var fix = _env.Os is NetworkOs.Windows or NetworkOs.MacOS ? new DiagnosticFix(Fixes.OpenHotspotSettings, NeedsElevation: false) : null;
        var linkLocal = string.Join(", ", addresses.Select(a => a.Ip));
        return addresses.Count > 0
            ? new DiagnosticCheck(Checks.LanAddress, CheckState.Failed, "link-local only: " + linkLocal, fix)
                { DetailKey = "Network.Check.Lan.LinkLocal", DetailArgs = [linkLocal] }
            : new DiagnosticCheck(Checks.LanAddress, CheckState.Failed, "no IPv4 address", fix) { DetailKey = "Network.Check.Lan.None" };
    }

    /// <summary>The ping answer of the OrientPyx host already listening on <paramref name="port"/> on this machine
    /// (the checks run after the host started), or null when the port is free or taken by something else.</summary>
    private async Task<PingResponse?> OwnHostAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            if (_env.IsTcpPortFree(port))
                return null;
            return await _env.PingAsync($"127.0.0.1:{port}", LoopbackPingTimeout, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private DiagnosticCheck CheckPortFree(int port, PingResponse? ownHost)
    {
        if (ownHost is not null)
            return new DiagnosticCheck(Checks.PortFree, CheckState.Ok, $"{port}: this host is listening ({ownHost.HostName})")
                { DetailKey = "Network.Check.Port.OwnHost", DetailArgs = [port.ToString(), ownHost.HostName] };
        if (_env.IsTcpPortFree(port))
            return new DiagnosticCheck(Checks.PortFree, CheckState.Ok, port.ToString());
        return new DiagnosticCheck(Checks.PortFree, CheckState.Failed, $"{port} is busy; the host takes the next free port")
            { DetailKey = "Network.Check.Port.Busy", DetailArgs = [port.ToString()] };
    }

    private async Task<DiagnosticCheck> CheckFirewallRuleAsync(CancellationToken cancellationToken)
    {
        switch (_env.Os)
        {
            case NetworkOs.Windows:
            {
                var exists = await _env.WindowsFirewallRuleExistsAsync(FirewallRuleName, cancellationToken);
                return exists switch
                {
                    true => new DiagnosticCheck(Checks.FirewallRule, CheckState.Ok, $"rule \"{FirewallRuleName}\" exists",
                        new DiagnosticFix(Fixes.RemoveFirewallRule, NeedsElevation: true))
                        { DetailKey = "Network.Check.Firewall.RuleExists", DetailArgs = [FirewallRuleName] },
                    false => new DiagnosticCheck(Checks.FirewallRule, CheckState.Failed, $"no inbound rule \"{FirewallRuleName}\"",
                        new DiagnosticFix(Fixes.AddFirewallRule, NeedsElevation: true))
                        { DetailKey = "Network.Check.Firewall.NoRule", DetailArgs = [FirewallRuleName] },
                    null => new DiagnosticCheck(Checks.FirewallRule, CheckState.Skipped, "could not query the firewall (netsh)",
                        new DiagnosticFix(Fixes.OpenFirewallSettings, NeedsElevation: false))
                        { DetailKey = "Network.Check.Firewall.QueryFailed", DetailArgs = ["netsh"] },
                };
            }
            case NetworkOs.MacOS:
            {
                var enabled = await _env.MacFirewallEnabledAsync(cancellationToken);
                if (enabled is null)
                    return new DiagnosticCheck(Checks.FirewallRule, CheckState.Skipped, "could not query the firewall (socketfilterfw)",
                        new DiagnosticFix(Fixes.OpenFirewallSettings, NeedsElevation: false))
                        { DetailKey = "Network.Check.Firewall.QueryFailed", DetailArgs = ["socketfilterfw"] };
                if (enabled == false)
                    return new DiagnosticCheck(Checks.FirewallRule, CheckState.Ok, "firewall off") { DetailKey = "Network.Check.Firewall.Off" };
                var app = _env.ApplicationPath;
                var allowed = app is null ? null : await _env.MacFirewallAllowsAsync(app, cancellationToken);
                return allowed == true
                    ? new DiagnosticCheck(Checks.FirewallRule, CheckState.Ok, "firewall on, OrientPyx allowed")
                        { DetailKey = "Network.Check.Firewall.MacAllowed" }
                    : new DiagnosticCheck(Checks.FirewallRule, CheckState.Warning, "firewall on; macOS asks on the first listen",
                        new DiagnosticFix(Fixes.AllowMacFirewall, NeedsElevation: true)) { DetailKey = "Network.Check.Firewall.MacAsks" };
            }
            default:
                return new DiagnosticCheck(Checks.FirewallRule, CheckState.Skipped, "not supported on this platform")
                    { DetailKey = "Network.Check.NotSupported" };
        }
    }

    private async Task<DiagnosticCheck> CheckNetworkProfileAsync(CancellationToken cancellationToken)
    {
        if (_env.Os != NetworkOs.Windows)
            return new DiagnosticCheck(Checks.NetworkProfile, CheckState.Skipped, "not supported on this platform")
                { DetailKey = "Network.Check.NotSupported" };
        var profile = await _env.WindowsNetworkProfileAsync(cancellationToken);
        return profile is null
            ? new DiagnosticCheck(Checks.NetworkProfile, CheckState.Skipped, "could not query the profile (Get-NetConnectionProfile)")
                { DetailKey = "Network.Check.Profile.QueryFailed" }
            : new DiagnosticCheck(Checks.NetworkProfile, CheckState.Info, profile);
    }

    private async Task<DiagnosticCheck> CheckSelfReachAsync(int port, PingResponse? ownHost, DiagnosticCheck firewall, DiagnosticCheck profile, CancellationToken cancellationToken)
    {
        var ip = _env.Addresses().FirstOrDefault(a => !a.IsLinkLocal)?.Ip;
        if (ip is null)
            return new DiagnosticCheck(Checks.SelfReach, CheckState.Skipped, "no LAN address") { DetailKey = "Network.Check.NoLan" };
        if (ownHost is null)
            return new DiagnosticCheck(Checks.SelfReach, CheckState.Info, "will be verified after start")
                { DetailKey = "Network.Check.SelfReach.AfterStart" };

        var endpoint = $"{ip}:{port}";
        try
        {
            var answer = await _env.PingAsync(endpoint, PingTimeout, cancellationToken);
            return new DiagnosticCheck(Checks.SelfReach, CheckState.Ok, $"{endpoint}: {answer.HostName}");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var isPublic = profile.State == CheckState.Info && profile.Detail.Contains("Public", StringComparison.OrdinalIgnoreCase);
            var (culprit, fix) = firewall.State switch
            {
                CheckState.Failed => ("firewall", firewall.Fix),
                CheckState.Warning => ("firewall", firewall.Fix),
                _ when isPublic => ("public network profile", OpenFirewallSettingsFix()),
                _ => ("unknown", OpenFirewallSettingsFix()),
            };
            var detail = $"{endpoint}: {RootMessage(ex)}; likely: {culprit}";
            if (isPublic && culprit == "firewall")
                detail += " (network profile is Public)";
            var detailKey = culprit switch
            {
                "firewall" when isPublic => "Network.Check.SelfReach.FirewallPublic",
                "firewall" => "Network.Check.SelfReach.Firewall",
                "public network profile" => "Network.Check.SelfReach.PublicProfile",
                _ => "Network.Check.SelfReach.Unknown",
            };
            return new DiagnosticCheck(Checks.SelfReach, CheckState.Failed, detail, fix)
                { DetailKey = detailKey, DetailArgs = [endpoint, RootMessage(ex)] };
        }
    }

    private DiagnosticFix? OpenFirewallSettingsFix()
        => _env.Os is NetworkOs.Windows or NetworkOs.MacOS ? new DiagnosticFix(Fixes.OpenFirewallSettings, NeedsElevation: false) : null;

    // ---- station -------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<DiagnosticCheck>> RunStationChecksAsync(string? endpoint, CancellationToken cancellationToken = default)
    {
        // One broadcast round and one ping, shared by every check below.
        IReadOnlyList<DiscoveredHost> hosts = [];
        Exception? probeError = null;
        try
        {
            hosts = await _env.ProbeHostsAsync(ProbeWait, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            probeError = ex;
        }

        PingResponse? ping = null;
        Exception? pingError = null;
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            try
            {
                ping = await _env.PingAsync(endpoint.Trim(), PingTimeout, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                pingError = ex;
            }
        }

        var permission = Log(CheckLocalNetworkPermission(endpoint, hosts, ping, pingError));
        var discovered = Log(CheckHostDiscovered(hosts, probeError));
        var reachable = Log(CheckHostReachable(endpoint, hosts, ping, pingError));
        var version = Log(CheckVersion(ping));
        var clock = Log(CheckClock(ping));
        return [permission, discovered, reachable, version, clock];
    }

    private DiagnosticCheck CheckLocalNetworkPermission(string? endpoint, IReadOnlyList<DiscoveredHost> hosts, PingResponse? ping, Exception? pingError)
    {
        if (_env.Os != NetworkOs.MacOS || !_env.IsMacOS14OrLater)
            return new DiagnosticCheck(Checks.LocalNetworkPermission, CheckState.Skipped, "not applicable on this platform")
                { DetailKey = "Network.Check.NotApplicable" };
        if (hosts.Count > 0 || ping is not null)
            return new DiagnosticCheck(Checks.LocalNetworkPermission, CheckState.Ok, "local network reachable")
                { DetailKey = "Network.Check.LocalNetwork.Reachable" };
        if (string.IsNullOrWhiteSpace(endpoint))
            return new DiagnosticCheck(Checks.LocalNetworkPermission, CheckState.Skipped, "no host answered; enter the host's address to tell")
                { DetailKey = "Network.Check.LocalNetwork.Unknown" };
        if (pingError is not null && IsLocalNetworkDenied(pingError))
            return new DiagnosticCheck(Checks.LocalNetworkPermission, CheckState.Failed,
                "no answer to the broadcast and the host is unreachable: " + RootMessage(pingError),
                new DiagnosticFix(Fixes.OpenLocalNetworkSettings, NeedsElevation: false))
                { DetailKey = "Network.Check.LocalNetwork.Denied", DetailArgs = [RootMessage(pingError)] };
        return new DiagnosticCheck(Checks.LocalNetworkPermission, CheckState.Ok, "connections allowed")
            { DetailKey = "Network.Check.LocalNetwork.Allowed" };
    }

    /// <summary>EHOSTUNREACH / ENETUNREACH / EPERM (mapped to AccessDenied) — what macOS answers when the «Local Network» permission is denied.</summary>
    private static bool IsLocalNetworkDenied(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException!)
        {
            if (e is SocketException { SocketErrorCode: SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.AccessDenied })
                return true;
        }
        return false;
    }

    private static DiagnosticCheck CheckHostDiscovered(IReadOnlyList<DiscoveredHost> hosts, Exception? probeError)
    {
        if (hosts.Count > 0)
            return new DiagnosticCheck(Checks.HostDiscovered, CheckState.Ok, string.Join(", ", hosts.Select(h => $"{h.Name} ({h.Endpoint})")));
        var detail = "no answer to the broadcast: enter the connection code; same Wi-Fi? try a phone hot-spot";
        if (probeError is not null)
            detail += "; " + RootMessage(probeError);
        return new DiagnosticCheck(Checks.HostDiscovered, CheckState.Warning, detail)
        {
            DetailKey = probeError is null ? "Network.Check.Discovery.None" : "Network.Check.Discovery.NoneError",
            DetailArgs = probeError is null ? [] : [RootMessage(probeError)],
        };
    }

    private static DiagnosticCheck CheckHostReachable(string? endpoint, IReadOnlyList<DiscoveredHost> hosts, PingResponse? ping, Exception? pingError)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return new DiagnosticCheck(Checks.HostReachable, CheckState.Skipped, "no host address") { DetailKey = "Network.Check.NoHostAddress" };
        if (ping is not null)
            return new DiagnosticCheck(Checks.HostReachable, CheckState.Ok, $"{endpoint}: {ping.HostName} v{ping.AppVersion}");
        var detail = $"{endpoint}: {RootMessage(pingError)}";
        if (hosts.Count == 0)
            return new DiagnosticCheck(Checks.HostReachable, CheckState.Failed, detail);
        detail += "; the host answered the broadcast but not TCP — its firewall blocks the port (run the checks on the host)";
        return new DiagnosticCheck(Checks.HostReachable, CheckState.Failed, detail)
            { DetailKey = "Network.Check.Reach.BlockedTcp", DetailArgs = [endpoint, RootMessage(pingError)] };
    }

    private static DiagnosticCheck CheckVersion(PingResponse? ping)
    {
        if (ping is null)
            return new DiagnosticCheck(Checks.VersionCompatible, CheckState.Skipped, "host not reached") { DetailKey = "Network.Check.HostNotReached" };
        return ping.ProtocolVersion == NetworkProtocol.Version
            ? new DiagnosticCheck(Checks.VersionCompatible, CheckState.Ok, $"protocol {ping.ProtocolVersion}, host app {ping.AppVersion}")
                { DetailKey = "Network.Check.Version.Ok", DetailArgs = [ping.ProtocolVersion.ToString(), ping.AppVersion] }
            : new DiagnosticCheck(Checks.VersionCompatible, CheckState.Failed,
                $"host protocol {ping.ProtocolVersion} (app {ping.AppVersion}), this one {NetworkProtocol.Version}; update the older program")
                {
                    DetailKey = "Network.Check.Version.Mismatch",
                    DetailArgs = [ping.ProtocolVersion.ToString(), ping.AppVersion, NetworkProtocol.Version.ToString()],
                };
    }

    private DiagnosticCheck CheckClock(PingResponse? ping)
    {
        if (ping is null)
            return new DiagnosticCheck(Checks.ClockSkew, CheckState.Skipped, "host not reached") { DetailKey = "Network.Check.HostNotReached" };
        var skew = ping.Time - _env.UtcNow;
        var seconds = skew.TotalSeconds.ToString("+0.0;-0.0", System.Globalization.CultureInfo.InvariantCulture);
        var detail = $"{seconds} s (host minus this computer)";
        return new DiagnosticCheck(Checks.ClockSkew, skew.Duration() > MaxClockSkew ? CheckState.Warning : CheckState.Ok, detail)
            { DetailKey = "Network.Check.Clock", DetailArgs = [seconds] };
    }

    // ---- fixes ---------------------------------------------------------------------------------------------

    public async Task<DiagnosticCheck> ApplyFixAsync(string fixKey, int port, CancellationToken cancellationToken = default)
    {
        _log.Action($"Network diagnostics: fix '{fixKey}' (port {port})");
        try
        {
            return fixKey switch
            {
                Fixes.OpenHotspotSettings => Open(Checks.LanAddress, _env.Os switch
                {
                    NetworkOs.Windows => "ms-settings:network-mobilehotspot",
                    NetworkOs.MacOS => "x-apple.systempreferences:com.apple.Network-Settings.extension",
                    _ => null,
                }),
                Fixes.OpenFirewallSettings => Open(Checks.FirewallRule, _env.Os switch
                {
                    NetworkOs.Windows => "ms-settings:windowsdefender",
                    NetworkOs.MacOS => "x-apple.systempreferences:com.apple.preference.security?Firewall",
                    _ => null,
                }),
                Fixes.OpenLocalNetworkSettings => Open(Checks.LocalNetworkPermission,
                    _env.Os == NetworkOs.MacOS ? "x-apple.systempreferences:com.apple.preference.security?Privacy_LocalNetwork" : null),
                Fixes.AddFirewallRule => await WindowsFirewallAsync(AddFirewallRuleCommand(port), cancellationToken),
                Fixes.RemoveFirewallRule => await WindowsFirewallAsync(RemoveFirewallRuleCommand(), cancellationToken),
                Fixes.AllowMacFirewall => await AllowMacFirewallAsync(cancellationToken),
                _ => new DiagnosticCheck(fixKey, CheckState.Failed, "unknown fix") { DetailKey = "Network.Check.UnknownFix" },
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _log.Error($"Network diagnostics: fix '{fixKey}'", ex);
            return Log(new DiagnosticCheck(fixKey, CheckState.Failed, RootMessage(ex)));
        }
    }

    /// <summary>The Windows command of <see cref="Fixes.AddFirewallRule"/>: allow inbound TCP <paramref name="port"/> and the
    /// UDP discovery port for the current exe, in every network profile (so a «Public» network needs no profile change).</summary>
    public string AddFirewallRuleCommand(int port)
    {
        var program = _env.ApplicationPath is { Length: > 0 } exe ? $" program=\"{exe}\"" : string.Empty;
        return $"netsh advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP localport={port}{program} profile=any"
               + $" && netsh advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=UDP localport={NetworkProtocol.DiscoveryPort}{program} profile=any";
    }

    /// <summary>The inverse of <see cref="AddFirewallRuleCommand"/>: drops every rule named <see cref="FirewallRuleName"/>.</summary>
    public static string RemoveFirewallRuleCommand()
        => $"netsh advfirewall firewall delete rule name=\"{FirewallRuleName}\"";

    private DiagnosticCheck Open(string checkKey, string? url)
    {
        if (url is null)
            return Log(new DiagnosticCheck(checkKey, CheckState.Skipped, "not supported on this platform") { DetailKey = "Network.Check.NotSupported" });
        _env.OpenUrl(url);
        return Log(new DiagnosticCheck(checkKey, CheckState.Info, "settings opened; re-run the checks afterwards")
            { DetailKey = "Network.Check.SettingsOpened" });
    }

    private async Task<DiagnosticCheck> WindowsFirewallAsync(string commandLine, CancellationToken cancellationToken)
    {
        if (_env.Os != NetworkOs.Windows)
            return Log(new DiagnosticCheck(Checks.FirewallRule, CheckState.Skipped, "not supported on this platform") { DetailKey = "Network.Check.NotSupported" });
        var result = await _env.RunElevatedAsync(commandLine, cancellationToken);
        _log.Info($"Network diagnostics: elevated `{commandLine}` → exit {result.ExitCode}{(result.Cancelled ? " (cancelled)" : string.Empty)}");
        if (result.Cancelled)
            return Log(new DiagnosticCheck(Checks.FirewallRule, CheckState.Failed, "cancelled", new DiagnosticFix(Fixes.AddFirewallRule, NeedsElevation: true))
                { DetailKey = "Network.Check.Cancelled" });
        if (result.ExitCode != 0)
            return Log(new DiagnosticCheck(Checks.FirewallRule, CheckState.Failed, $"netsh exit code {result.ExitCode}", new DiagnosticFix(Fixes.AddFirewallRule, NeedsElevation: true))
                { DetailKey = "Network.Check.NetshExit", DetailArgs = [result.ExitCode.ToString()] });
        return await GuardAsync(Checks.FirewallRule, CheckFirewallRuleAsync, cancellationToken);
    }

    private async Task<DiagnosticCheck> AllowMacFirewallAsync(CancellationToken cancellationToken)
    {
        if (_env.Os != NetworkOs.MacOS)
            return Log(new DiagnosticCheck(Checks.FirewallRule, CheckState.Skipped, "not supported on this platform") { DetailKey = "Network.Check.NotSupported" });
        if (_env.ApplicationPath is not { Length: > 0 } app)
            return Log(new DiagnosticCheck(Checks.FirewallRule, CheckState.Failed, "application path unknown") { DetailKey = "Network.Check.AppPathUnknown" });
        var commandLine = $"/usr/libexec/ApplicationFirewall/socketfilterfw --add '{app}' && /usr/libexec/ApplicationFirewall/socketfilterfw --unblockapp '{app}'";
        var result = await _env.RunElevatedAsync(commandLine, cancellationToken);
        _log.Info($"Network diagnostics: elevated `{commandLine}` → exit {result.ExitCode}{(result.Cancelled ? " (cancelled)" : string.Empty)}");
        if (result.Cancelled)
            return Log(new DiagnosticCheck(Checks.FirewallRule, CheckState.Failed, "cancelled", new DiagnosticFix(Fixes.AllowMacFirewall, NeedsElevation: true)) { DetailKey = "Network.Check.Cancelled" });
        if (result.ExitCode != 0)
            return Log(new DiagnosticCheck(Checks.FirewallRule, CheckState.Failed, $"socketfilterfw exit code {result.ExitCode}: {result.Output.Trim()}",
                new DiagnosticFix(Fixes.AllowMacFirewall, NeedsElevation: true)));
        return await GuardAsync(Checks.FirewallRule, CheckFirewallRuleAsync, cancellationToken);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    /// <summary>Runs one check; an OS probe that throws becomes a Skipped check with the message. Every result is logged.</summary>
    private async Task<DiagnosticCheck> GuardAsync(string key, Func<CancellationToken, Task<DiagnosticCheck>> check, CancellationToken cancellationToken)
    {
        try
        {
            return Log(await check(cancellationToken));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _log.Error($"Network diagnostics: check '{key}'", ex);
            return Log(new DiagnosticCheck(key, CheckState.Skipped, RootMessage(ex)));
        }
    }

    private DiagnosticCheck Log(DiagnosticCheck check)
    {
        _log.Info($"Network diagnostics: {check.Key} = {check.State}: {check.Detail}{(check.Fix is null ? string.Empty : $" [fix {check.Fix.Key}]")}");
        return check;
    }

    /// <summary>The innermost exception's message — what the user can act on (the socket error, not the HttpClient wrapper).</summary>
    private static string RootMessage(Exception? error)
    {
        if (error is null)
            return "no answer";
        while (error.InnerException is not null)
            error = error.InnerException;
        return error.Message;
    }
}
