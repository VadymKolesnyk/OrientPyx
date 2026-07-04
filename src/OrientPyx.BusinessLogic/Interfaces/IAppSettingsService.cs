using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>Reads/writes configurable application paths, applying defaults when unset.</summary>
public interface IAppSettingsService
{
    /// <summary>Returns the configured events path, falling back to the default (./events).</summary>
    Task<AppPaths> GetPathsAsync(CancellationToken cancellationToken = default);

    Task SavePathsAsync(AppPaths paths, CancellationToken cancellationToken = default);

    /// <summary>Smallest and largest allowed UI font scale.</summary>
    double MinFontScale { get; }
    double MaxFontScale { get; }
    double DefaultFontScale { get; }

    /// <summary>Returns the stored UI font scale, falling back to the default, clamped to range.</summary>
    Task<double> GetFontScaleAsync(CancellationToken cancellationToken = default);

    Task SaveFontScaleAsync(double fontScale, CancellationToken cancellationToken = default);

    /// <summary>Allowed thermal-roll widths (mm) for split printouts, and the default.</summary>
    IReadOnlyList<int> ReceiptWidths { get; }
    int DefaultReceiptWidth { get; }

    /// <summary>Returns the stored split-printout printer name (blank if unset) and roll width (clamped to an allowed value).</summary>
    Task<PrintSettings> GetPrintSettingsAsync(CancellationToken cancellationToken = default);

    Task SavePrintSettingsAsync(PrintSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Returns the stored results-protocol settings, applying defaults when never saved or unreadable.</summary>
    Task<ResultProtocolSettings> GetResultProtocolSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveResultProtocolSettingsAsync(ResultProtocolSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Returns the app-level default start-protocol settings for the kind, applying the kind's
    /// built-in default when never saved or unreadable.</summary>
    Task<StartProtocolSettings> GetStartProtocolSettingsAsync(StartProtocolKind kind, CancellationToken cancellationToken = default);

    Task SaveStartProtocolSettingsAsync(StartProtocolKind kind, StartProtocolSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Returns the app-level online live-results connection settings (Supabase URL, service-role key,
    /// public frontend base URL, publish interval), applying defaults when never saved.</summary>
    Task<OnlineApiSettings> GetOnlineApiSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveOnlineApiSettingsAsync(OnlineApiSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Returns the configured timing-system readout format, defaulting to SPORTident when never set.</summary>
    Task<ReadoutType> GetReadoutTypeAsync(CancellationToken cancellationToken = default);

    Task SaveReadoutTypeAsync(ReadoutType readoutType, CancellationToken cancellationToken = default);
}
