using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>Applies path defaults on top of the persisted app settings.</summary>
public sealed class AppSettingsService : IAppSettingsService
{
    private readonly IAppStore _appStore;

    public AppSettingsService(IAppStore appStore)
    {
        _appStore = appStore;
    }

    public async Task<AppPaths> GetPathsAsync(CancellationToken cancellationToken = default)
    {
        var defaults = _appStore.GetDefaultPaths();
        var stored = await _appStore.GetPathsAsync(cancellationToken);

        if (stored is null)
            return defaults;

        return new AppPaths
        {
            EventsPath = string.IsNullOrWhiteSpace(stored.EventsPath) ? defaults.EventsPath : stored.EventsPath
        };
    }

    public Task SavePathsAsync(AppPaths paths, CancellationToken cancellationToken = default)
        => _appStore.SavePathsAsync(paths, cancellationToken);

    public double MinFontScale => 0.8;
    public double MaxFontScale => 1.6;
    public double DefaultFontScale => 1.0;

    public async Task<double> GetFontScaleAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _appStore.GetFontScaleAsync(cancellationToken);
        return Clamp(stored ?? DefaultFontScale);
    }

    public Task SaveFontScaleAsync(double fontScale, CancellationToken cancellationToken = default)
        => _appStore.SaveFontScaleAsync(Clamp(fontScale), cancellationToken);

    private double Clamp(double value) => Math.Clamp(value, MinFontScale, MaxFontScale);

    public IReadOnlyList<int> ReceiptWidths { get; } = [56, 80];
    public int DefaultReceiptWidth => 80;

    public async Task<PrintSettings> GetPrintSettingsAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _appStore.GetPrintSettingsAsync(cancellationToken);
        if (stored is not { } s)
            return new PrintSettings(string.Empty, DefaultReceiptWidth);

        return new PrintSettings(s.PrinterName ?? string.Empty, ClampWidth(s.WidthMm));
    }

    public Task SavePrintSettingsAsync(PrintSettings settings, CancellationToken cancellationToken = default)
        => _appStore.SavePrintSettingsAsync(settings.PrinterName ?? string.Empty, ClampWidth(settings.WidthMm), cancellationToken);

    // Snaps any stored/incoming width to the nearest allowed roll width so a bad value can't leak through.
    private int ClampWidth(int width) => ReceiptWidths.Contains(width) ? width : DefaultReceiptWidth;
}
