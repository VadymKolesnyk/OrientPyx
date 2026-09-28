using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Carries the application-level data a competition refers to (points rules, sports ranks) across a
/// competition export/import, so an imported competition doesn't point at rules/ranks missing locally.
/// </summary>
public interface IEventSharedDataService
{
    /// <summary>Collects the app-level points rules and ranks the competition at <paramref name="eventFolderPath"/> uses.</summary>
    Task<EventSharedData> CollectAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes <paramref name="data"/> available locally for the (just imported) competition at
    /// <paramref name="eventFolderPath"/>: a rule already present by id is kept; a rule with identical
    /// content under another id is reused and the competition's references are re-pointed to it; anything
    /// else is added to the app database. Ranks are added by name when missing.
    /// </summary>
    Task<EventSharedDataImportResult> ApplyAsync(string eventFolderPath, EventSharedData data, CancellationToken cancellationToken = default);
}
