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
}
