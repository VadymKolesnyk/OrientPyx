using System.ComponentModel;

namespace OrientDesk.Presentation.Services;

/// <summary>
/// Holds the live UI scale for the running app. The root window wraps its content in a
/// <c>LayoutTransformControl</c> whose <c>ScaleTransform</c> binds to <see cref="Scale"/>;
/// that single transform scales the WHOLE interface at once — fonts, inputs, paddings,
/// spacing, icons and borders all grow/shrink together. Changing the scale raises
/// PropertyChanged so the binding updates live, without a restart.
///
/// The font-size properties below are fixed design sizes (a type ramp): they keep the
/// relative hierarchy between titles, sections and body text. They are intentionally NOT
/// multiplied by <see cref="Scale"/> — the layout transform already applies the scale, so
/// scaling them here too would double-scale text.
/// </summary>
public interface IUiScaleService : INotifyPropertyChanged
{
    /// <summary>Current scale multiplier (e.g. 1.0) applied to the whole interface.</summary>
    double Scale { get; }

    /// <summary>Base body font size (design size, unscaled).</summary>
    double ScaledFontSize { get; }

    /// <summary>Page/screen title size (design size, unscaled).</summary>
    double TitleFontSize { get; }

    /// <summary>Section / sub-header size (design size, unscaled).</summary>
    double SectionFontSize { get; }

    /// <summary>Sidebar brand size (design size, unscaled).</summary>
    double BrandFontSize { get; }

    /// <summary>Secondary / smaller body text size (design size, unscaled).</summary>
    double SmallFontSize { get; }

    /// <summary>Loads the persisted scale once at startup.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Updates the live scale and persists it.</summary>
    Task SetScaleAsync(double scale, CancellationToken cancellationToken = default);
}
