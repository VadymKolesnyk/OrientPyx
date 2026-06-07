using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Interfaces;

/// <summary>Reads/writes configurable application paths, applying defaults when unset.</summary>
public interface IAppSettingsService
{
    /// <summary>Returns configured paths, falling back to defaults (./data, ./events).</summary>
    Task<AppPaths> GetPathsAsync(CancellationToken cancellationToken = default);

    Task SavePathsAsync(AppPaths paths, CancellationToken cancellationToken = default);

    /// <summary>Smallest and largest allowed UI font scale.</summary>
    double MinFontScale { get; }
    double MaxFontScale { get; }
    double DefaultFontScale { get; }

    /// <summary>Returns the stored UI font scale, falling back to the default, clamped to range.</summary>
    Task<double> GetFontScaleAsync(CancellationToken cancellationToken = default);

    Task SaveFontScaleAsync(double fontScale, CancellationToken cancellationToken = default);
}
