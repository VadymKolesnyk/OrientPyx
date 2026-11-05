namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// A station's session token issued by this host when it paired with the PIN. Kept in the app database so stations
/// reconnect after a host restart without entering the PIN again; «Відключити» deletes the row.
/// </summary>
public class StationTokenRow
{
    /// <summary>The bearer token (a random 32-byte value, base64url).</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>The station's name as sent on pairing (shown in the host's station list, stamped on its log lines).</summary>
    public string StationName { get; set; } = string.Empty;

    public DateTimeOffset IssuedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}
