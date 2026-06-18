using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OrientDesk.Presentation.Converters;

/// <summary>
/// Tints text red (DangerBrush) when the bound bool is true, otherwise leaves it at the default
/// foreground (returns null so the control inherits). Used to red-flag a non-OK finish status cell.
/// </summary>
public sealed class BoolToDangerBrushConverter : IValueConverter
{
    private static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#DC2626"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Danger : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
