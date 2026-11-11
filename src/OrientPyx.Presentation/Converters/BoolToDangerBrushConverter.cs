using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OrientPyx.Presentation.Converters;

/// <summary>
/// Tints text red (DangerBrush) when the bound bool is true, otherwise leaves it at the default foreground.
/// "Otherwise" is <see cref="AvaloniaProperty.UnsetValue"/>, never null: a null brush is a real value and draws
/// the text invisible, while an unset one lets the control inherit its theme colour. Used to red-flag an
/// additional field's value outside its choice list / a non-number.
/// </summary>
public sealed class BoolToDangerBrushConverter : IValueConverter
{
    private static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#DC2626"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Danger : AvaloniaProperty.UnsetValue;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
