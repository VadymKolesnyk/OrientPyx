using OrientPyx.BusinessLogic.Models.Network;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// This computer as a station: pairs with a host and, while connected, routes every competition store call to it.
/// Implemented in DataAccess. Connecting switches <see cref="IWorkspaceMode"/> to <see cref="WorkspaceRole.Station"/>
/// and clears the session (the competition list is now the host's); disconnecting switches back to
/// <see cref="WorkspaceRole.Local"/>. Refuses to connect while this instance is a host.
/// </summary>
public interface INetworkClient
{
    StationState State { get; }

    /// <summary>The host this station is (or was last) connected to; null when never connected.</summary>
    StationInfo? Host { get; }

    /// <summary>A localisation key (<c>Network.Error.*</c>) of the last failure, or null.</summary>
    string? LastErrorKey { get; }

    /// <summary>Free-text detail of the last failure (an exception message), or null.</summary>
    string? LastErrorDetail { get; }

    /// <summary>Raised on every <see cref="State"/> change and when the host list changes.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Pairs with the host at <paramref name="endpoint"/> (<c>ip:port</c>). With a PIN a new token is requested; without
    /// one the saved token for that host is reused. On success the role switches to station.
    /// </summary>
    Task<ConnectResult> ConnectAsync(string endpoint, string? pin, CancellationToken cancellationToken = default);

    /// <summary>Reconnects to the saved last host with the saved token (startup restore). False when there is none or
    /// the host does not answer within <paramref name="timeout"/>.</summary>
    Task<bool> TryReconnectSavedAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>Leaves the host: forgets the token, switches back to local.</summary>
    Task DisconnectAsync();

    /// <summary>Tells the station which competition (identifier) the session works on, so its version poll and log
    /// batches name it. Optional — the station also infers it from the change stream.</summary>
    void NotifyCurrentEvent(string? identifier) { }

    // ── Discovery

    /// <summary>Hosts seen on the local network in the last few seconds (while discovery runs).</summary>
    IReadOnlyList<DiscoveredHost> DiscoveredHosts { get; }

    void StartDiscovery();

    void StopDiscovery();

    /// <summary>Decodes a spoken connection code (12 characters) into <c>ip:port</c>; null when malformed.</summary>
    string? DecodeConnectionCode(string code);
}
