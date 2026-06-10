using System.Globalization;
using Avalonia.Data.Converters;

namespace OrientDesk.Presentation.Converters;

/// <summary>
/// Maps a boolean to an opacity: <c>true</c> → fully opaque, <c>false</c> → dimmed. Used to grey out
/// grid cells that aren't relevant for a row's discipline while keeping the column in place.
/// </summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    /// <summary>Opacity applied when the bound value is <c>false</c>.</summary>
    public double DimmedOpacity { get; set; } = 0.35;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 1.0 : DimmedOpacity;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
