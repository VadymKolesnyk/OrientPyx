using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Publishes a competition's live results to the online (Supabase) service. Implemented in DataAccess
/// (HTTP / PostgREST lives there, not in BusinessLogic). One instance serves one running publish session:
/// it tracks which metadata it has already uploaded so meta is sent once and only the result rows are
/// re-sent each tick.
/// </summary>
public interface IResultPublisher
{
    /// <summary>
    /// Pushes one snapshot to the service for the given competition slug, using the app-level connection
    /// settings. On the first call for a slug it also uploads the competition + day metadata and the
    /// published day's group metadata; later calls upload only the result rows (unless
    /// <see cref="ResetMetadata"/> was called). Throws on a transport / server error so the caller can
    /// surface it in the publish log.
    /// </summary>
    Task PublishAsync(
        OnlinePublishSettings publish,
        OnlineApiSettings api,
        OnlineResultsSnapshot snapshot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets which metadata has been uploaded, so the next <see cref="PublishAsync"/> re-sends the
    /// competition / day / group metadata. Call after the publish options change (title, slug, day…).
    /// </summary>
    void ResetMetadata();
}
