using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OrientPyx.Presentation.Behaviors;

/// <summary>
/// Maps a row-level bool (bound via <see cref="OrientPyx.Presentation.Controls.SheetTable.RowHighlightPath"/>)
/// to a cell background: true ⇒ a red tint, false ⇒ transparent. Applied to every cell in the row so the
/// whole row reads as flagged (e.g. an unrecognised chip on the finish-read log), not just one column.
/// Returns <see cref="Brushes.Transparent"/> (never null) for the no-tint case so the cell keeps a
/// non-null, hit-testable background.
/// </summary>
public sealed class RowHighlight : IValueConverter
{
    public static readonly RowHighlight Instance = new();

    private static readonly IBrush FlagBrush = new SolidColorBrush(Color.FromRgb(0xF6, 0xCB, 0xC8));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? FlagBrush : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
