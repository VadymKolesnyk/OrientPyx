using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OrientPyx.Presentation.Converters;

/// <summary>
/// MultiBinding converter for a resting sheet label that should fall back to a placeholder when its
/// value is blank: given <c>[value, placeholder]</c>, returns the value when it is non-blank, otherwise
/// the placeholder. Pairs with <see cref="PlaceholderForegroundConverter"/>, which greys the label while
/// the placeholder is showing.
/// </summary>
public sealed class PlaceholderTextConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var value = values.Count > 0 ? values[0] as string : null;
        var placeholder = values.Count > 1 ? values[1] as string : null;
        return string.IsNullOrWhiteSpace(value) ? (placeholder ?? string.Empty) : value;
    }
}

/// <summary>
/// MultiBinding converter that returns the muted foreground brush while a label is showing its
/// placeholder (the value is blank) and the normal text brush otherwise. Expects <c>[value, placeholder]</c>.
/// </summary>
public sealed class PlaceholderForegroundConverter : IMultiValueConverter
{
    /// <summary>Brush used for the real value (normal text).</summary>
    public IBrush? NormalBrush { get; set; }

    /// <summary>Brush used while the placeholder is showing (muted/grey).</summary>
    public IBrush? PlaceholderBrush { get; set; }

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var value = values.Count > 0 ? values[0] as string : null;
        var placeholder = values.Count > 1 ? values[1] as string : null;

        // Grey only when we are actually showing a (non-blank) placeholder in place of a blank value.
        var showingPlaceholder = string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(placeholder);
        return showingPlaceholder ? PlaceholderBrush : NormalBrush;
    }
}
