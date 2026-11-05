using System.Net.Sockets;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models.Network;
using OrientPyx.DataAccess.Network.Diagnostics;
using OrientPyx.DataAccess.Network.Protocol;

namespace OrientPyx.Tests.Core.Network;

/// <summary>The self-checks and fixes of «Спільна робота» over a fake OS environment: no sockets, no netsh, no UAC.</summary>
public sealed class DiagnosticsTests
{
    private const int Port = NetworkProtocol.DefaultApiPort;
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    // ---- host ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Host_NoIpv4_LanAddressFailedWithHotspotFix()
    {
        var env = Windows() with { Lan = [] };
        var checks = await Run(env).RunHostChecksAsync(Port);

        var lan = Find(checks, "lan-address");
        Assert.Equal(CheckState.Failed, lan.State);
        Assert.Equal("open-hotspot-settings", lan.Fix?.Key);
        Assert.False(lan.Fix!.NeedsElevation);
        Assert.Equal(CheckState.Skipped, Find(checks, "self-reach").State);
    }

    [Fact]
    public async Task Host_LinkLocalOnly_LanAddressFailed()
    {
        var env = Windows() with { Lan = [new LanAddress("169.254.10.7", IsLinkLocal: true, "Ethernet")] };
        var checks = await Run(env).RunHostChecksAsync(Port);

        var lan = Find(checks, "lan-address");
        Assert.Equal(CheckState.Failed, lan.State);
        Assert.Contains("169.254.10.7", lan.Detail);
        Assert.Equal("open-hotspot-settings", lan.Fix?.Key);
    }

    [Fact]
    public async Task Host_PortBusyByAnotherProgram_PortFreeFailedWithoutFix()
    {
        var env = Windows() with { FreePorts = [], Ping = _ => throw new HttpRequestException("refused") };
        var checks = await Run(env).RunHostChecksAsync(Port);

        var port = Find(checks, "port-free");
        Assert.Equal(CheckState.Failed, port.State);
        Assert.Null(port.Fix);
        Assert.Contains("next free port", port.Detail);
    }

    [Fact]
    public async Task Host_FirewallRuleMissing_FailedWithElevatedFix()
    {
        var env = Windows() with { FirewallRuleExists = false };
        var checks = await Run(env).RunHostChecksAsync(Port);

        var firewall = Find(checks, "firewall-rule");
        Assert.Equal(CheckState.Failed, firewall.State);
        Assert.Equal("add-firewall-rule", firewall.Fix?.Key);
        Assert.True(firewall.Fix!.NeedsElevation);
    }

    [Fact]
    public async Task Host_SelfReachFails_NamesFirewallAsCulprit()
    {
        // The host is running (the port is taken and answers on loopback) but not through the LAN address.
        var env = Windows() with
        {
            FreePorts = [],
            FirewallRuleExists = false,
            Profile = "Public",
            Ping = endpoint => endpoint.StartsWith("127.0.0.1", StringComparison.Ordinal)
                ? Pong()
                : throw new HttpRequestException("timeout", new SocketException((int)SocketError.TimedOut)),
        };
        var checks = await Run(env).RunHostChecksAsync(Port);

        Assert.Equal(CheckState.Ok, Find(checks, "port-free").State);
        var reach = Find(checks, "self-reach");
        Assert.Equal(CheckState.Failed, reach.State);
        Assert.Contains("likely: firewall", reach.Detail);
        Assert.Contains("Public", reach.Detail);
        Assert.Equal("add-firewall-rule", reach.Fix?.Key);
    }

    [Fact]
    public async Task Host_SelfReachFailsWithRule_NamesPublicProfile()
    {
        var env = Windows() with
        {
            FreePorts = [],
            Profile = "Public",
            Ping = endpoint => endpoint.StartsWith("127.0.0.1", StringComparison.Ordinal) ? Pong() : throw new HttpRequestException("timeout"),
        };
        var reach = Find(await Run(env).RunHostChecksAsync(Port), "self-reach");

        Assert.Equal(CheckState.Failed, reach.State);
        Assert.Contains("public network profile", reach.Detail);
        Assert.Equal("open-firewall-settings", reach.Fix?.Key);
    }

    [Fact]
    public async Task Host_NotStartedYet_SelfReachIsInfo()
    {
        var checks = await Run(Windows()).RunHostChecksAsync(Port);

        Assert.Equal(CheckState.Ok, Find(checks, "port-free").State);
        var reach = Find(checks, "self-reach");
        Assert.Equal(CheckState.Info, reach.State);
        Assert.Contains("after start", reach.Detail);
    }

    [Fact]
    public async Task Host_AllFine_EveryCheckOkInOrder()
    {
        var env = Windows() with { FreePorts = [], Ping = _ => Pong() };
        var log = new FakeLog();
        var checks = await Run(env, log).RunHostChecksAsync(Port);

        Assert.Equal(["lan-address", "port-free", "firewall-rule", "network-profile", "self-reach"], checks.Select(c => c.Key));
        Assert.All(checks.Where(c => c.Key != "network-profile"), c => Assert.Equal(CheckState.Ok, c.State));
        var profile = Find(checks, "network-profile");
        Assert.Equal(CheckState.Info, profile.State);
        Assert.Equal("Private", profile.Detail);
        Assert.Null(profile.Fix);
        Assert.Contains("Головний-ноут", Find(checks, "self-reach").Detail);
        Assert.Equal(5, log.Lines.Count(l => l.StartsWith("Network diagnostics:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Host_MacFirewallOn_WarningWithAllowFix()
    {
        var env = Mac() with { MacFirewallEnabled = true, MacFirewallAllows = false };
        var checks = await Run(env).RunHostChecksAsync(Port);

        var firewall = Find(checks, "firewall-rule");
        Assert.Equal(CheckState.Warning, firewall.State);
        Assert.Equal("allow-mac-firewall", firewall.Fix?.Key);
        Assert.True(firewall.Fix!.NeedsElevation);
        Assert.Equal(CheckState.Skipped, Find(checks, "network-profile").State);
    }

    [Fact]
    public async Task Host_ProbeThrows_CheckSkippedNotCrash()
    {
        var env = Windows() with { FirewallRuleExists = null, ThrowOnFirewall = true };
        var checks = await Run(env).RunHostChecksAsync(Port);

        var firewall = Find(checks, "firewall-rule");
        Assert.Equal(CheckState.Skipped, firewall.State);
        Assert.Contains("netsh exploded", firewall.Detail);
    }

    // ---- station -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Station_Mac14_NoBroadcastAndHostUnreachable_PermissionFailedWithFix()
    {
        var env = Mac() with
        {
            Hosts = [],
            Ping = _ => throw new HttpRequestException("unreachable", new SocketException((int)SocketError.HostUnreachable)),
        };
        var checks = await Run(env).RunStationChecksAsync("192.168.1.5:47832");

        var permission = Find(checks, "local-network-permission");
        Assert.Equal(CheckState.Failed, permission.State);
        Assert.Equal("open-local-network-settings", permission.Fix?.Key);
        Assert.Equal(CheckState.Warning, Find(checks, "host-discovered").State);
        Assert.Equal(CheckState.Failed, Find(checks, "host-reachable").State);
        Assert.Equal(CheckState.Skipped, Find(checks, "version-compatible").State);
    }

    [Fact]
    public async Task Station_Windows_PermissionCheckSkipped()
    {
        var checks = await Run(Windows() with { Ping = _ => Pong() }).RunStationChecksAsync("192.168.1.5:47832");
        Assert.Equal(CheckState.Skipped, Find(checks, "local-network-permission").State);
    }

    [Fact]
    public async Task Station_BroadcastAnsweredButPingFails_HostReachableBlamesHostFirewall()
    {
        var env = Windows() with { Ping = _ => throw new HttpRequestException("timeout", new SocketException((int)SocketError.TimedOut)) };
        var checks = await Run(env).RunStationChecksAsync("192.168.1.5:47832");

        Assert.Equal(CheckState.Ok, Find(checks, "host-discovered").State);
        var reach = Find(checks, "host-reachable");
        Assert.Equal(CheckState.Failed, reach.State);
        Assert.Contains("firewall", reach.Detail);
    }

    [Fact]
    public async Task Station_ProtocolMismatch_VersionFailed()
    {
        var env = Windows() with { Ping = _ => Pong() with { ProtocolVersion = NetworkProtocol.Version + 1, AppVersion = "9.9.9" } };
        var version = Find(await Run(env).RunStationChecksAsync("192.168.1.5:47832"), "version-compatible");

        Assert.Equal(CheckState.Failed, version.State);
        Assert.Contains("9.9.9", version.Detail);
        Assert.Contains(NetworkProtocol.Version.ToString(), version.Detail);
    }

    [Fact]
    public async Task Station_ClockSkewOverTwoSeconds_Warning()
    {
        var env = Windows() with { Ping = _ => Pong() with { Time = Now.AddSeconds(3.5) } };
        var clock = Find(await Run(env).RunStationChecksAsync("192.168.1.5:47832"), "clock-skew");

        Assert.Equal(CheckState.Warning, clock.State);
        Assert.Contains("+3.5 s", clock.Detail);
    }

    [Fact]
    public async Task Station_AllFine_OkInOrder()
    {
        var env = Windows() with { Ping = _ => Pong() with { Time = Now.AddSeconds(-1) } };
        var checks = await Run(env).RunStationChecksAsync("192.168.1.5:47832");

        Assert.Equal(["local-network-permission", "host-discovered", "host-reachable", "version-compatible", "clock-skew"], checks.Select(c => c.Key));
        Assert.Equal(CheckState.Skipped, checks[0].State);
        Assert.All(checks.Skip(1), c => Assert.Equal(CheckState.Ok, c.State));
        Assert.Contains("Головний-ноут", Find(checks, "host-discovered").Detail);
    }

    [Fact]
    public async Task Station_NoEndpoint_OnlyDiscoveryRuns()
    {
        var env = Windows() with { Ping = _ => throw new InvalidOperationException("must not ping") };
        var checks = await Run(env).RunStationChecksAsync(null);

        Assert.Equal(CheckState.Ok, Find(checks, "host-discovered").State);
        Assert.Equal(CheckState.Skipped, Find(checks, "host-reachable").State);
        Assert.Equal(CheckState.Skipped, Find(checks, "clock-skew").State);
    }

    // ---- fixes ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Fix_AddFirewallRule_RunsBothNetshRulesElevatedAndRerunsCheck()
    {
        var env = Windows() with { FirewallRuleExists = false };
        var log = new FakeLog();
        var diagnostics = Run(env, log);

        var result = await diagnostics.ApplyFixAsync("add-firewall-rule", 47833);

        var command = Assert.Single(env.Elevated);
        Assert.Contains("netsh advfirewall firewall add rule name=\"OrientPyx\" dir=in action=allow protocol=TCP localport=47833", command);
        Assert.Contains("protocol=UDP localport=47831", command);
        Assert.Contains("program=\"C:\\Apps\\OrientPyx.exe\"", command);
        Assert.Contains("profile=any", command);
        Assert.Equal("firewall-rule", result.Key);
        Assert.Equal(CheckState.Ok, result.State);
        Assert.Contains(log.Lines, l => l.Contains("fix 'add-firewall-rule'"));
    }

    [Fact]
    public async Task Fix_AddFirewallRule_CancelledUac_Failed()
    {
        var env = Windows() with { FirewallRuleExists = false, ElevatedResult = new CommandResult(1223, "cancelled", Cancelled: true) };
        var result = await Run(env).ApplyFixAsync("add-firewall-rule", Port);

        Assert.Equal("firewall-rule", result.Key);
        Assert.Equal(CheckState.Failed, result.State);
        Assert.Equal("cancelled", result.Detail);
        Assert.Equal("add-firewall-rule", result.Fix?.Key);
    }

    [Fact]
    public async Task Fix_RemoveFirewallRule_RunsDelete()
    {
        var env = Windows();
        var result = await Run(env).ApplyFixAsync("remove-firewall-rule", Port);
        Assert.Equal("netsh advfirewall firewall delete rule name=\"OrientPyx\"", Assert.Single(env.Elevated));
        Assert.Equal(CheckState.Failed, result.State);
        Assert.Equal("add-firewall-rule", result.Fix?.Key);
    }

    [Fact]
    public async Task Fix_OpenHotspotSettings_OpensUrlAndReportsInfo()
    {
        var env = Windows() with { Lan = [] };
        var result = await Run(env).ApplyFixAsync("open-hotspot-settings", Port);

        Assert.Equal("ms-settings:network-mobilehotspot", Assert.Single(env.Opened));
        Assert.Equal("lan-address", result.Key);
        Assert.Equal(CheckState.Info, result.State);
    }

    [Fact]
    public async Task Fix_OpenLocalNetworkSettings_MacOnly()
    {
        var mac = Mac();
        var result = await Run(mac).ApplyFixAsync("open-local-network-settings", Port);
        Assert.Equal("x-apple.systempreferences:com.apple.preference.security?Privacy_LocalNetwork", Assert.Single(mac.Opened));
        Assert.Equal("local-network-permission", result.Key);

        var win = Windows();
        var skipped = await Run(win).ApplyFixAsync("open-local-network-settings", Port);
        Assert.Empty(win.Opened);
        Assert.Equal(CheckState.Skipped, skipped.State);
    }

    [Fact]
    public async Task Fix_AllowMacFirewall_RunsSocketFilterFwWithAppBundle()
    {
        var env = Mac() with { MacFirewallEnabled = true, MacFirewallAllows = true };
        var result = await Run(env).ApplyFixAsync("allow-mac-firewall", Port);

        var command = Assert.Single(env.Elevated);
        Assert.Contains("socketfilterfw --add '/Applications/OrientPyx.app'", command);
        Assert.Contains("--unblockapp '/Applications/OrientPyx.app'", command);
        Assert.Equal(CheckState.Ok, result.State);
    }

    [Fact]
    public async Task Fix_Unknown_Failed()
    {
        var result = await Run(Windows()).ApplyFixAsync("make-coffee", Port);
        Assert.Equal(CheckState.Failed, result.State);
    }

    [Fact]
    public void MacBundlePath_WalksUpToTheAppBundle()
    {
        Assert.Equal("/Applications/OrientPyx.app", OsNetworkEnvironment.MacBundlePath("/Applications/OrientPyx.app/Contents/MacOS/OrientPyx"));
        Assert.Equal("/opt/OrientPyx", OsNetworkEnvironment.MacBundlePath("/opt/OrientPyx"));
        Assert.Null(OsNetworkEnvironment.MacBundlePath(null));
    }

    // ---- support -------------------------------------------------------------------------------------------

    private static StationDiagnostics Run(FakeEnvironment env, FakeLog? log = null) => new(env, log ?? new FakeLog());

    private static DiagnosticCheck Find(IReadOnlyList<DiagnosticCheck> checks, string key) => Assert.Single(checks, c => c.Key == key);

    private static PingResponse Pong() => new("host-1", "Головний-ноут", "0.8.0", NetworkProtocol.Version, Now);

    /// <summary>A healthy Windows laptop: one LAN address, the port free, the rule present, a private profile, a host in the LAN.</summary>
    private static FakeEnvironment Windows() => new()
    {
        Os = NetworkOs.Windows,
        ApplicationPath = @"C:\Apps\OrientPyx.exe",
        Lan = [new LanAddress("192.168.1.10", IsLinkLocal: false, "Wi-Fi")],
        FreePorts = [Port, 47833],
        FirewallRuleExists = true,
        Profile = "Private",
        Hosts = [new DiscoveredHost("host-1", "Головний-ноут", "0.8.0", ["192.168.1.5"], Port, "Кубок міста", Now)],
    };

    private static FakeEnvironment Mac() => Windows() with
    {
        Os = NetworkOs.MacOS,
        IsMacOS14OrLater = true,
        ApplicationPath = "/Applications/OrientPyx.app",
        FirewallRuleExists = null,
        Profile = null,
        MacFirewallEnabled = false,
    };

    private sealed record FakeEnvironment : INetworkEnvironment
    {
        public NetworkOs Os { get; init; }
        public bool IsMacOS14OrLater { get; init; }
        public string? ApplicationPath { get; init; }
        public DateTimeOffset UtcNow => Now;
        public IReadOnlyList<LanAddress> Lan { get; init; } = [];
        public IReadOnlyList<int> FreePorts { get; init; } = [];
        /// <summary>Settable: the fake "creates"/"drops" the rule when the elevated netsh command runs.</summary>
        public bool? FirewallRuleExists { get; set; }
        public bool ThrowOnFirewall { get; init; }
        public string? Profile { get; init; }
        public bool? MacFirewallEnabled { get; init; }
        public bool? MacFirewallAllows { get; init; }
        public CommandResult ElevatedResult { get; init; } = new(0, string.Empty);
        public Func<string, PingResponse> Ping { get; init; } = _ => throw new HttpRequestException("no host");
        public IReadOnlyList<DiscoveredHost> Hosts { get; init; } = [];

        public List<string> Elevated { get; } = [];
        public List<string> Opened { get; } = [];

        public IReadOnlyList<LanAddress> Addresses() => Lan;
        public bool IsTcpPortFree(int port) => FreePorts.Contains(port);

        public Task<bool?> WindowsFirewallRuleExistsAsync(string ruleName, CancellationToken cancellationToken)
            => ThrowOnFirewall ? throw new InvalidOperationException("netsh exploded") : Task.FromResult(FirewallRuleExists);

        public Task<string?> WindowsNetworkProfileAsync(CancellationToken cancellationToken) => Task.FromResult(Profile);
        public Task<bool?> MacFirewallEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(MacFirewallEnabled);
        public Task<bool?> MacFirewallAllowsAsync(string applicationPath, CancellationToken cancellationToken) => Task.FromResult(MacFirewallAllows);

        public Task<CommandResult> RunElevatedAsync(string commandLine, CancellationToken cancellationToken)
        {
            Elevated.Add(commandLine);
            if (ElevatedResult.Succeeded && commandLine.Contains("add rule", StringComparison.Ordinal))
                FirewallRuleExists = true;
            if (ElevatedResult.Succeeded && commandLine.Contains("delete rule", StringComparison.Ordinal))
                FirewallRuleExists = false;
            return Task.FromResult(ElevatedResult);
        }

        public void OpenUrl(string url) => Opened.Add(url);
        public Task<PingResponse> PingAsync(string endpoint, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(Ping(endpoint));
        public Task<IReadOnlyList<DiscoveredHost>> ProbeHostsAsync(TimeSpan wait, CancellationToken cancellationToken) => Task.FromResult(Hosts);
    }

    private sealed class FakeLog : IActivityLog
    {
        public List<string> Lines { get; } = [];
        public void Action(string message) => Lines.Add(message);
        public void Info(string message) => Lines.Add(message);
        public void Error(string context, Exception exception) => Lines.Add($"{context}: {exception.Message}");
        public void UseEventFolder(string eventFolderPath) { }
        public void UseSharedFolder() { }
        public string LogFilePath => string.Empty;
    }
}
