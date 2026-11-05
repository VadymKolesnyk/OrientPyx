using OrientPyx.BusinessLogic.Models.Network;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// The self-checks of the «Спільна робота» page: is there a LAN, is the port free, does the firewall let connections
/// in, can the host reach itself through its LAN address; on a station — is the host visible, reachable, compatible.
/// Each failed check may carry a one-click fix. Implemented in DataAccess (OS calls); every result is logged.
/// </summary>
public interface IStationDiagnostics
{
    /// <summary>Checks this computer as a host for <paramref name="port"/>.</summary>
    Task<IReadOnlyList<DiagnosticCheck>> RunHostChecksAsync(int port, CancellationToken cancellationToken = default);

    /// <summary>Checks this computer as a station against the host at <paramref name="endpoint"/> (null ⇒ only the
    /// network-level checks).</summary>
    Task<IReadOnlyList<DiagnosticCheck>> RunStationChecksAsync(string? endpoint, CancellationToken cancellationToken = default);

    /// <summary>Applies a fix by its <see cref="DiagnosticFix.Key"/> and returns the re-run check.</summary>
    Task<DiagnosticCheck> ApplyFixAsync(string fixKey, int port, CancellationToken cancellationToken = default);
}
