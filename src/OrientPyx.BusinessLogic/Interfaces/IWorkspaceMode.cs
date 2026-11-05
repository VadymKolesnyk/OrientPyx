using OrientPyx.BusinessLogic.Models.Network;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>How this running instance works with competition data.</summary>
public enum WorkspaceRole
{
    /// <summary>Alone, on its own <c>events</c> folder (the default).</summary>
    Local,
    /// <summary>On its own folder and serving it to stations over the local network.</summary>
    Host,
    /// <summary>On a host's data over the network; nothing competition-related is on this disk.</summary>
    Station,
}

/// <summary>
/// The role of this instance plus what a page needs to know about it. The routers in DataAccess read
/// <see cref="Role"/> on every call to pick the local or the remote implementation, so the role can change without a
/// restart. <see cref="Changed"/> is raised on the thread that switched the role.
/// </summary>
public interface IWorkspaceMode
{
    WorkspaceRole Role { get; }

    /// <summary>The name writes of this instance are stamped with: the station name, the host name, or <c>"local"</c>.</summary>
    string SelfOrigin { get; }

    /// <summary>Set while <see cref="Role"/> is <see cref="WorkspaceRole.Station"/>.</summary>
    StationInfo? Station { get; }

    /// <summary>Set while <see cref="Role"/> is <see cref="WorkspaceRole.Host"/>.</summary>
    HostInfo? Host { get; }

    bool IsStation => Role == WorkspaceRole.Station;
    bool IsHost => Role == WorkspaceRole.Host;

    event EventHandler? Changed;
}
