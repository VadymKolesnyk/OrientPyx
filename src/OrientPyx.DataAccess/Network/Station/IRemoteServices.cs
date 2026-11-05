using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.DataAccess.Network.Station;

/// <summary>
/// The remote (host-backed) implementations the routers forward to while this instance is a station. They exist only
/// while a connection is up; a call before pairing throws <see cref="BusinessLogic.Models.Network.HostUnreachableException"/>.
/// Set by <c>NetworkStation</c> after a successful pairing.
/// </summary>
public interface IRemoteServices
{
    IEventStore EventStore { get; }
    IEventFolderScanner FolderScanner { get; }
    IEventCatalogService Catalog { get; }
    IEventFiles EventFiles { get; }
    IEventArchiveService Archive { get; }
    IResultsAppData ResultsAppData { get; }
}
