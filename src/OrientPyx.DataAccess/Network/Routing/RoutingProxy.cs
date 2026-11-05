using System.Reflection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Services.Store;

namespace OrientPyx.DataAccess.Network.Routing;

/// <summary>
/// The switch between the local and the remote implementation of a competition-data interface: every call reads
/// <see cref="IWorkspaceMode.Role"/> and forwards to the local object (Local/Host) or the station's remote proxy
/// (Station). Because every service is a singleton with constructor injection, this is the one place the
/// implementation can change at run time — which is what lets a machine become a station and back without a restart.
/// </summary>
public class RoutingProxy<T> : DispatchProxy where T : class
{
    private IWorkspaceMode _mode = null!;
    private Func<T> _local = null!;
    private Func<T> _remote = null!;

    public static T Create(IWorkspaceMode mode, Func<T> local, Func<T> remote)
    {
        var proxy = Create<T, RoutingProxy<T>>();
        var self = (RoutingProxy<T>)(object)proxy;
        self._mode = mode;
        self._local = local;
        self._remote = remote;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var target = _mode.Role == WorkspaceRole.Station ? _remote() : _local();
        // A call from the UI thread runs on the thread pool (SQLite completes synchronously — see UiThreadOffload).
        return UiThreadOffload.Invoke(target, targetMethod, args ?? []);
    }
}
