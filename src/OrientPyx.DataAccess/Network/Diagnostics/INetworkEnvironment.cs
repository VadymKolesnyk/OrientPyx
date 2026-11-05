using OrientPyx.BusinessLogic.Models.Network;
using OrientPyx.DataAccess.Network.Protocol;

namespace OrientPyx.DataAccess.Network.Diagnostics;

/// <summary>The operating system the diagnostics run on, as far as the checks care.</summary>
public enum NetworkOs
{
    Windows,
    MacOS,
    Other,
}

/// <summary>One IPv4 address of this machine (see <c>LocalNetwork.Address</c>).</summary>
public sealed record LanAddress(string Ip, bool IsLinkLocal, string InterfaceName);

/// <summary>The outcome of an OS command: its exit code and its combined output. <see cref="Cancelled"/> ⇒ the
/// user declined the elevation prompt (UAC «Ні» / the macOS password dialog «Cancel»).</summary>
public sealed record CommandResult(int ExitCode, string Output, bool Cancelled = false)
{
    public bool Succeeded => !Cancelled && ExitCode == 0;
}

/// <summary>
/// Everything <see cref="StationDiagnostics"/> asks the operating system — addresses, sockets, firewall state, the
/// elevated shell, the settings pages, the HTTP ping and the discovery broadcast — behind one interface, so the
/// checks and the fixes are testable over a fake. The real one is <see cref="OsNetworkEnvironment"/>. A probe that
/// cannot be answered on this platform returns <c>null</c>; a probe that fails may throw — the diagnostics turn
/// the exception into a <c>Skipped</c> check.
/// </summary>
public interface INetworkEnvironment
{
    NetworkOs Os { get; }

    /// <summary>macOS 14 (Sonoma) or newer — where the «Local Network» privacy permission exists.</summary>
    bool IsMacOS14OrLater { get; }

    /// <summary>The path a firewall rule names: the exe on Windows, the <c>.app</c> bundle on macOS; null when unknown.</summary>
    string? ApplicationPath { get; }

    DateTimeOffset UtcNow { get; }

    /// <summary>The IPv4 addresses of every interface that is up and not loopback, link-local ones last.</summary>
    IReadOnlyList<LanAddress> Addresses();

    /// <summary>True when nothing listens on TCP <paramref name="port"/> (a bind to any address succeeds).</summary>
    bool IsTcpPortFree(int port);

    /// <summary>Windows: does an inbound firewall rule named <paramref name="ruleName"/> exist? Null ⇒ could not tell.</summary>
    Task<bool?> WindowsFirewallRuleExistsAsync(string ruleName, CancellationToken cancellationToken);

    /// <summary>Windows: the network category of the connected profiles (<c>Public</c>, <c>Private</c>,
    /// <c>DomainAuthenticated</c>; several joined by a comma). Null ⇒ could not tell.</summary>
    Task<string?> WindowsNetworkProfileAsync(CancellationToken cancellationToken);

    /// <summary>macOS: is the application firewall switched on? Null ⇒ could not tell.</summary>
    Task<bool?> MacFirewallEnabledAsync(CancellationToken cancellationToken);

    /// <summary>macOS: does the application firewall list <paramref name="applicationPath"/> as allowed? Null ⇒ could not tell.</summary>
    Task<bool?> MacFirewallAllowsAsync(string applicationPath, CancellationToken cancellationToken);

    /// <summary>Runs <paramref name="commandLine"/> with administrator rights after the OS consent prompt: on Windows
    /// <c>cmd /c "…"</c> with <c>Verb = runas</c> (one UAC dialog), on macOS <c>osascript … with administrator
    /// privileges</c> (the password dialog). Elsewhere it throws <see cref="PlatformNotSupportedException"/>.</summary>
    Task<CommandResult> RunElevatedAsync(string commandLine, CancellationToken cancellationToken);

    /// <summary>Opens a settings page (<c>ms-settings:</c> / <c>x-apple.systempreferences:</c> URL) in the OS.</summary>
    void OpenUrl(string url);

    /// <summary><c>GET http://&lt;endpoint&gt;/ping</c>; throws on any failure (connection, timeout, bad body).</summary>
    Task<PingResponse> PingAsync(string endpoint, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>One discovery broadcast round, waiting <paramref name="wait"/> for the hosts that answer.</summary>
    Task<IReadOnlyList<DiscoveredHost>> ProbeHostsAsync(TimeSpan wait, CancellationToken cancellationToken);
}
