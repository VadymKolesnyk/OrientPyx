using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Interfaces;

/// <summary>
/// Abstraction over the shared application database (settings + last-session pointer).
/// Implemented in DataAccess.
/// </summary>
public interface IAppStore
{
    /// <summary>Default paths resolved by the infrastructure (./data, ./events).</summary>
    AppPaths GetDefaultPaths();

    /// <summary>Returns configured paths, or null if never set (caller applies defaults).</summary>
    Task<AppPaths?> GetPathsAsync(CancellationToken cancellationToken = default);

    Task SavePathsAsync(AppPaths paths, CancellationToken cancellationToken = default);

    /// <summary>Returns the stored UI font scale, or null if never set (caller applies default 1.0).</summary>
    Task<double?> GetFontScaleAsync(CancellationToken cancellationToken = default);

    Task SaveFontScaleAsync(double fontScale, CancellationToken cancellationToken = default);

    /// <summary>Returns the last opened competition identifier + day number, if any.</summary>
    Task<(string? Identifier, int? DayNumber)> GetLastSessionAsync(CancellationToken cancellationToken = default);

    Task SaveLastSessionAsync(string? identifier, int? dayNumber, CancellationToken cancellationToken = default);
}
