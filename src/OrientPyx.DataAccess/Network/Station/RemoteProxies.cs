using System.Reflection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services.Store;

namespace OrientPyx.DataAccess.Network.Station;

/// <summary>
/// The remote implementations a connected station routes to. <see cref="IEventStore"/> gets two local overrides:
/// <see cref="EventStoreMethods.HostFileMethods"/> (<c>CheckpointAsync</c>/<c>ReleaseAsync</c> act on the host's
/// database files — a no-op here, П8) and <c>GetDaySnapshotAsync</c> through the <see cref="SnapshotCache"/>; the
/// scanner, the catalog and the results app-data are plain proxies.
/// </summary>
public static class RemoteProxies
{
    public static IEventStore EventStore(RpcChannel channel, SnapshotCache cache)
    {
        return RpcProxy<IEventStore>.Create(
            channel,
            intercept: (method, args) =>
            {
                if (EventStoreMethods.HostFileMethods.Contains(method.Name))
                    return Task.CompletedTask;
                if (method.Name == nameof(IEventStore.GetDaySnapshotAsync) && args is [string path, Guid dayId, ..])
                {
                    var (wireArgs, _, token) = RpcProxy<IEventStore>.Strip(method, args);
                    return cache.GetAsync(path, dayId, async () =>
                        (EventDaySnapshot)(await channel.CallAsync("EventStore", method.Name, wireArgs, RpcCallKind.Read,
                            typeof(EventDaySnapshot), null, token))!);
                }
                return null;
            },
            afterWrite: (_, args) =>
            {
                if (args.Length > 0 && args[0] is string path)
                    cache.Invalidate(path);
            });
    }

    public static IEventFolderScanner FolderScanner(RpcChannel channel) => RpcProxy<IEventFolderScanner>.Create(channel);

    public static IEventCatalogService Catalog(RpcChannel channel) => RpcProxy<IEventCatalogService>.Create(channel);

    public static IResultsAppData ResultsAppData(RpcChannel channel) => RpcProxy<IResultsAppData>.Create(channel);
}
