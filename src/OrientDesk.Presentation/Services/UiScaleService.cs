using System.ComponentModel;
using OrientDesk.BusinessLogic.Interfaces;

namespace OrientDesk.Presentation.Services;

public sealed class UiScaleService : IUiScaleService
{
    private readonly IAppSettingsService _settings;
    private double _scale = 1.0;

    public UiScaleService(IAppSettingsService settings)
    {
        _settings = settings;
        _scale = settings.DefaultFontScale;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public double Scale
    {
        get => _scale;
        private set
        {
            if (Math.Abs(_scale - value) < 0.0001)
                return;

            _scale = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Scale)));
        }
    }

    // Fixed design type ramp. The whole UI is scaled by the root LayoutTransform, so these
    // stay constant — multiplying them by Scale here would double-scale text.
    public double ScaledFontSize => 14.0;
    public double TitleFontSize => 26.0;
    public double SectionFontSize => 16.0;
    public double BrandFontSize => 22.0;
    public double SmallFontSize => 13.0;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Scale = await _settings.GetFontScaleAsync(cancellationToken);
    }

    public async Task SetScaleAsync(double scale, CancellationToken cancellationToken = default)
    {
        Scale = scale;
        await _settings.SaveFontScaleAsync(scale, cancellationToken);
    }
}
