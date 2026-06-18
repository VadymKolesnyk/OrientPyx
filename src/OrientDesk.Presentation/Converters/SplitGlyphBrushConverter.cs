using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OrientDesk.Presentation.Converters;

/// <summary>
/// Colours a splits-panel status glyph: ✓ (correct / visited) green, ✗ (wrong / extra punch) red,
/// anything else (— missing / not visited) muted grey. Colours mirror the app palette
/// (SuccessBrush / DangerBrush / TextMuted) so the panel matches the rest of the UI.
/// </summary>
public sealed class SplitGlyphBrushConverter : IValueConverter
{
    private static readonly IBrush Correct = new SolidColorBrush(Color.Parse("#059669"));
    private static readonly IBrush Wrong = new SolidColorBrush(Color.Parse("#DC2626"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#94A3B8"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) switch
        {
            "✓" => Correct,
            "✗" => Wrong,
            _ => Muted
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
