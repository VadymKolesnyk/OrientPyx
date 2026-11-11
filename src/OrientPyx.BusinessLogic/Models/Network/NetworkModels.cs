using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.BusinessLogic.Models.Network;

/// <summary>The protocol this build speaks. Bump on any change to a network interface's signatures or the wire format;
/// a host and a station with different versions refuse to pair.</summary>
public static class NetworkProtocol
{
    public const int Version = 4;

    /// <summary>UDP port of the host discovery broadcast (the same on every machine).</summary>
    public const int DiscoveryPort = 47831;

    /// <summary>The TCP port the host tries first; the next free one is used when it is taken.</summary>
    public const int DefaultApiPort = 47832;
}

/// <summary>What a station knows about the host it is connected to.</summary>
public sealed record StationInfo(string HostId, string HostName, string Endpoint, string StationName);

/// <summary>What the host shows about itself: how a station finds it and the PIN it must enter.</summary>
public sealed record HostInfo(
    string HostId,
    string HostName,
    int Port,
    string Pin,
    IReadOnlyList<string> Addresses,
    int StationCount = 0);

/// <summary>A station paired with the host (its token row), with the time of its last request.</summary>
public sealed record ConnectedStation(
    string Token,
    string Name,
    string Address,
    DateTimeOffset IssuedAt,
    DateTimeOffset LastSeenAt,
    bool IsOnline,
    /// <summary>The station's stable device id (sent on pairing); empty for stations of an older build.</summary>
    string DeviceId = "",
    /// <summary>Marked «довірений» on the host: this device pairs again without a PIN.</summary>
    bool IsTrusted = false);

/// <summary>A host answering the discovery broadcast.</summary>
public sealed record DiscoveredHost(
    string HostId,
    string Name,
    string Version,
    IReadOnlyList<string> Addresses,
    int Port,
    string? Competition,
    DateTimeOffset SeenAt)
{
    /// <summary>The <c>ip:port</c> a station connects to (the first address).</summary>
    public string Endpoint => Addresses.Count > 0 ? $"{Addresses[0]}:{Port}" : string.Empty;
}

/// <summary>The station's connection life cycle.</summary>
public enum StationState
{
    Disconnected,
    Connecting,
    Connected,
    /// <summary>The host has not answered anything for a while (a short blip does not count); the station keeps trying
    /// and blocks edits meanwhile.</summary>
    Reconnecting,
}

/// <summary>The result of a pairing attempt. <see cref="Warnings"/> are the checks the station could only make with
/// the host's answer (plugins that differ, another «Тип відмітки», a clock skew) — shown on the page, not fatal.</summary>
public sealed record ConnectResult(bool Success, string? ErrorKey = null, string? Detail = null)
{
    public IReadOnlyList<DiagnosticCheck> Warnings { get; init; } = [];

    public static ConnectResult Ok(IReadOnlyList<DiagnosticCheck>? warnings = null) => new(true) { Warnings = warnings ?? [] };
    public static ConnectResult Fail(string errorKey, string? detail = null) => new(false, errorKey, detail);
}

/// <summary>The app-level network settings (<c>app.db</c>): the host's identity and the station's last host.</summary>
public sealed class NetworkSettings
{
    /// <summary>Start the host automatically on launch (it was switched on last time).</summary>
    public bool HostEnabled { get; set; }

    /// <summary>The TCP port the host listens on; 0 ⇒ the default, falling back to the next free port.</summary>
    public int HostPort { get; set; }

    /// <summary>The name stations see; blank ⇒ the machine name.</summary>
    public string HostName { get; set; } = string.Empty;

    /// <summary>Generated once; identifies this host to its stations across restarts.</summary>
    public string HostId { get; set; } = string.Empty;

    /// <summary>This machine's name when it is a station; blank ⇒ the machine name.</summary>
    public string StationName { get; set; } = string.Empty;

    /// <summary>The last host this station connected to (<c>ip:port</c>), for auto-reconnect.</summary>
    public string StationHost { get; set; } = string.Empty;

    /// <summary>The session token the last host issued; blank ⇒ pair again with a PIN.</summary>
    public string StationToken { get; set; } = string.Empty;

    /// <summary>The id of the last host, so a restored session is only resumed on the same host.</summary>
    public string StationHostId { get; set; } = string.Empty;

    /// <summary>This machine's stable device id (generated once), by which a host can trust it across restarts.</summary>
    public string StationDeviceId { get; set; } = string.Empty;

    /// <summary>The PIN last used for <see cref="StationHostId"/>, re-sent on the next connect so it need not be typed.</summary>
    public string StationPin { get; set; } = string.Empty;
}

/// <summary>A station token issued by the host (kept so stations survive a host restart without a new PIN).</summary>
public sealed class StationToken
{
    public string Token { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;

    /// <summary>The station's stable device id; empty for an older station.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>The host trusts this device: it pairs again without a PIN.</summary>
    public bool IsTrusted { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>The last-session pointer: which competition/day to restore and in which role it was open.</summary>
public sealed record LastSession(string? Identifier, int? DayNumber, WorkspaceRole Mode = WorkspaceRole.Local, string? HostId = null)
{
    public void Deconstruct(out string? identifier, out int? dayNumber)
    {
        identifier = Identifier;
        dayNumber = DayNumber;
    }
}

/// <summary>The outcome of one self-check on the «Спільна робота» page.</summary>
public enum CheckState
{
    Ok,
    Warning,
    Failed,
    /// <summary>Purely informational (e.g. the Windows network profile).</summary>
    Info,
    /// <summary>Could not be determined on this platform.</summary>
    Skipped,
}

/// <summary>A one-click fix the page can offer for a failed check. <see cref="NeedsElevation"/> ⇒ the OS asks for
/// administrator consent.</summary>
public sealed record DiagnosticFix(string Key, bool NeedsElevation);

/// <summary>One self-check: a key (localised by the page as <c>Network.Check.&lt;Key&gt;</c>), its state, a free-text detail
/// (addresses, port numbers, the error) and an optional fix.</summary>
public sealed record DiagnosticCheck(string Key, CheckState State, string Detail, DiagnosticFix? Fix = null)
{
    /// <summary>Localization key of <see cref="Detail"/> for the screen (formatted with <see cref="DetailArgs"/>); null when
    /// the detail is plain data (addresses, a port, an OS error). <see cref="Detail"/> stays the English text for the log.</summary>
    public string? DetailKey { get; init; }

    public string[] DetailArgs { get; init; } = [];
}
