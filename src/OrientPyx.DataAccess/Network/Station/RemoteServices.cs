using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models.Network;

namespace OrientPyx.DataAccess.Network.Station;

/// <summary>
/// The holder of the station's remote implementations. Empty until <c>NetworkStation</c> connects and calls
/// <see cref="Set"/>; <see cref="Clear"/> on disconnect. Every getter throws <see cref="HostUnreachableException"/>
/// while empty, so a router that still forwards to it (role switched before the proxies were built) fails cleanly.
/// </summary>
public sealed class RemoteServices : IRemoteServices
{
    private sealed record Set_(
        IEventStore EventStore,
        IEventFolderScanner FolderScanner,
        IEventCatalogService Catalog,
        IEventFiles EventFiles,
        IEventArchiveService Archive,
        IResultsAppData ResultsAppData);

    private volatile Set_? _current;

    public bool IsSet => _current is not null;

    public void Set(
        IEventStore eventStore,
        IEventFolderScanner folderScanner,
        IEventCatalogService catalog,
        IEventFiles eventFiles,
        IEventArchiveService archive,
        IResultsAppData resultsAppData)
        => _current = new Set_(eventStore, folderScanner, catalog, eventFiles, archive, resultsAppData);

    public void Clear() => _current = null;

    private Set_ Current => _current ?? throw new HostUnreachableException("Not connected to a host.");

    public IEventStore EventStore => Current.EventStore;
    public IEventFolderScanner FolderScanner => Current.FolderScanner;
    public IEventCatalogService Catalog => Current.Catalog;
    public IEventFiles EventFiles => Current.EventFiles;
    public IEventArchiveService Archive => Current.Archive;
    public IResultsAppData ResultsAppData => Current.ResultsAppData;
}
