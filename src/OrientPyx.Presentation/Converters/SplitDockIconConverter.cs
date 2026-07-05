using System.Globalization;
using Avalonia.Data.Converters;

namespace OrientPyx.Presentation.Converters;

/// <summary>
/// Picks the dock-toggle button's icon for the finish-splits panel: when the panel is docked to the
/// right, show a "move down" (chevron-down) arrow; when it is docked below, show a "move right"
/// (chevron-right) arrow — i.e. the icon points at where the button would move the panel to.
/// Returns a Lucide icon name (a <see cref="OrientPyx.Presentation.Controls.Icon"/> Kind).
/// </summary>
public sealed class SplitDockIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "ChevronDown" : "ChevronRight";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
