using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Behaviors;

/// <summary>
/// Maps a participant table's <see cref="GroupCellTint"/> to the group cell's background: amber for «без групи»
/// (<see cref="DayColumnBuilder.NoGroupBrush"/>), the age-window red for a relay leg number without a usable course,
/// else transparent — never null, so the cell stays hit-testable.
/// </summary>
public sealed class GroupCellHighlight : IValueConverter
{
    public static readonly GroupCellHighlight Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        GroupCellTint.NoGroup => DayColumnBuilder.NoGroupBrush,
        GroupCellTint.RelayCourse => RowHighlight.FlagBrush,
        _ => Brushes.Transparent
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
