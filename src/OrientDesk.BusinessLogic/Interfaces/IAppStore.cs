using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Interfaces;

/// <summary>
/// Abstraction over the shared application database (settings + last-session pointer).
/// Implemented in DataAccess.
/// </summary>
public interface IAppStore
{
    /// <summary>Default events path resolved by the infrastructure (./events).</summary>
    AppPaths GetDefaultPaths();

    /// <summary>Returns configured paths, or null if never set (caller applies defaults).</summary>
    Task<AppPaths?> GetPathsAsync(CancellationToken cancellationToken = default);

    Task SavePathsAsync(AppPaths paths, CancellationToken cancellationToken = default);

    /// <summary>Returns the stored UI font scale, or null if never set (caller applies default 1.0).</summary>
    Task<double?> GetFontScaleAsync(CancellationToken cancellationToken = default);

    Task SaveFontScaleAsync(double fontScale, CancellationToken cancellationToken = default);

    /// <summary>Returns the stored split-printout printer name + roll width, or null if never set.</summary>
    Task<(string PrinterName, int WidthMm)?> GetPrintSettingsAsync(CancellationToken cancellationToken = default);

    Task SavePrintSettingsAsync(string printerName, int widthMm, CancellationToken cancellationToken = default);

    /// <summary>Returns the last opened competition identifier + day number, if any.</summary>
    Task<(string? Identifier, int? DayNumber)> GetLastSessionAsync(CancellationToken cancellationToken = default);

    Task SaveLastSessionAsync(string? identifier, int? dayNumber, CancellationToken cancellationToken = default);

    // ── Sports ranks (application-level, shared across competitions) ────────────────────────────────

    /// <summary>Seeds the given ranks only when the ranks table is empty (first run). A no-op otherwise.</summary>
    Task SeedRanksIfEmptyAsync(IReadOnlyList<SportRank> ranks, CancellationToken cancellationToken = default);

    /// <summary>Loads all ranks, ordered by their display order then name.</summary>
    Task<IReadOnlyList<SportRank>> GetRanksAsync(CancellationToken cancellationToken = default);

    /// <summary>Appends a new, blank rank after the last one and returns it.</summary>
    Task<SportRank> AddRankAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves an edited rank (name, points). A change to a name already used by another rank is ignored
    /// (the previous name is kept), keeping names unique.
    /// </summary>
    Task UpdateRankAsync(SportRank rank, CancellationToken cancellationToken = default);

    /// <summary>Removes a rank. Participants keep their stored rank text; it just stops matching a known rank.</summary>
    Task DeleteRankAsync(Guid rankId, CancellationToken cancellationToken = default);
}
