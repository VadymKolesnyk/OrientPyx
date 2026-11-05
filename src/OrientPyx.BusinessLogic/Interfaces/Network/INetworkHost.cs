using OrientPyx.BusinessLogic.Models.Network;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// This computer as the host: serves its competition data to stations over HTTP on the local network. Implemented in
/// DataAccess (Kestrel). Starting switches <see cref="IWorkspaceMode"/> to <see cref="WorkspaceRole.Host"/>; stopping
/// switches it back to <see cref="WorkspaceRole.Local"/>. Refuses to start while this instance is a station.
/// </summary>
public interface INetworkHost
{
    bool IsRunning { get; }

    /// <summary>Addresses, port, PIN; null while stopped.</summary>
    HostInfo? Info { get; }

    /// <summary>The paired stations (token rows) with their last request time.</summary>
    IReadOnlyList<ConnectedStation> Stations { get; }

    /// <summary>The spoken connection code (12 Base32 characters = LAN address + port) a station can type instead of
    /// picking the host from the list; empty while stopped or when the implementation has none. The page shows it
    /// only when non-empty.</summary>
    string ConnectionCode => string.Empty;

    /// <summary>Raised after start/stop and after a station pairs, is disconnected or makes a request.</summary>
    event EventHandler? Changed;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();

    /// <summary>Generates a new PIN (invalidates the previous one for pairing; existing tokens stay valid).</summary>
    string RegeneratePin();

    /// <summary>Revokes one station's token; its next request fails with 401 and it must pair again.</summary>
    Task DisconnectStationAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>Revokes every token.</summary>
    Task DisconnectAllAsync(CancellationToken cancellationToken = default);

    /// <summary>The names of the stations that made a request in the last minute on the given competition (by identifier);
    /// the catalog refuses to rename or delete a competition stations are working on.</summary>
    IReadOnlyList<string> ActiveStationsOn(string identifier);
}
