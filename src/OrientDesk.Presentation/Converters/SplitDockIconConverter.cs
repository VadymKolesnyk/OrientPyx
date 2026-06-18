using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OrientDesk.Presentation.Converters;

/// <summary>
/// Picks the dock-toggle button's icon for the finish-splits panel: when the panel is docked to the
/// right, show a "move down" (chevron-down) arrow; when it is docked below, show a "move right"
/// (chevron-right) arrow — i.e. the icon points at where the button would move the panel to.
/// </summary>
public sealed class SplitDockIconConverter : IValueConverter
{
    // 14×14 chevrons.
    private static readonly Geometry Right = Geometry.Parse("M5,2 L11,7 L5,12");
    private static readonly Geometry Down = Geometry.Parse("M2,5 L7,11 L12,5");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Down : Right;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
