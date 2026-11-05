using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models.Network;

namespace OrientPyx.BusinessLogic.Services.Network;

/// <summary>
/// The mutable <see cref="IWorkspaceMode"/>: the network host / station services switch it; everything else only reads
/// it through the interface.
/// </summary>
public sealed class WorkspaceMode : IWorkspaceMode
{
    public const string LocalOrigin = "local";

    private readonly object _gate = new();

    public WorkspaceRole Role { get; private set; } = WorkspaceRole.Local;
    public StationInfo? Station { get; private set; }
    public HostInfo? Host { get; private set; }

    public string SelfOrigin => Role switch
    {
        WorkspaceRole.Station => Station?.StationName ?? LocalOrigin,
        WorkspaceRole.Host => Host?.HostName ?? LocalOrigin,
        _ => LocalOrigin,
    };

    public event EventHandler? Changed;

    public void SetLocal()
    {
        lock (_gate)
        {
            Role = WorkspaceRole.Local;
            Station = null;
            Host = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetHost(HostInfo host)
    {
        ArgumentNullException.ThrowIfNull(host);
        lock (_gate)
        {
            Role = WorkspaceRole.Host;
            Host = host;
            Station = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Updates the host card (new PIN, station count) without leaving the host role.</summary>
    public void UpdateHost(HostInfo host)
    {
        ArgumentNullException.ThrowIfNull(host);
        lock (_gate)
        {
            if (Role != WorkspaceRole.Host)
                return;
            Host = host;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetStation(StationInfo station)
    {
        ArgumentNullException.ThrowIfNull(station);
        lock (_gate)
        {
            Role = WorkspaceRole.Station;
            Station = station;
            Host = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
