using System.Globalization;
using Avalonia.Data.Converters;

namespace OrientPyx.Presentation.Converters;

/// <summary>
/// Maps the protocol's landscape flag to a fixed display size for the document-preview "sheet" so the mock-up
/// looks like a real page at true A4 proportions (210 × 297 mm). The sheet has a fixed on-screen size — it does
/// <b>not</b> grow or shrink with content; content that overflows the page is clipped, just like print. Bind the
/// sheet's <c>Width</c> with <c>ConverterParameter=width</c> and its <c>Height</c> with <c>ConverterParameter=height</c>.
/// </summary>
public sealed class LandscapeToPageWidthConverter : IValueConverter
{
    // A4 aspect ratio (long side / short side). The portrait page is laid out at PortraitShortSide wide; the
    // landscape page swaps the two so the same paper turned sideways keeps its true proportions.
    private const double A4Ratio = 297.0 / 210.0;

    /// <summary>The short side of the page in device pixels (portrait width / landscape height).</summary>
    public double PortraitShortSide { get; set; } = 720;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var landscape = value is true;
        var wantsHeight = parameter is string s &&
                          s.Equals("height", StringComparison.OrdinalIgnoreCase);

        var shortSide = PortraitShortSide;
        var longSide = PortraitShortSide * A4Ratio;

        // Portrait: width = short, height = long. Landscape: width = long, height = short.
        return wantsHeight
            ? (landscape ? shortSide : longSide)
            : (landscape ? longSide : shortSide);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
