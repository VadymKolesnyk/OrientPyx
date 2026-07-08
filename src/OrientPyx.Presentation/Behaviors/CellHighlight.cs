using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OrientPyx.Presentation.Behaviors;

/// <summary>
/// Tints one column's cell from a bool flag on the row. As a plain <see cref="IValueConverter"/> it maps a
/// single cell-level bool (bound via a column's <see cref="OrientPyx.Presentation.Controls.SheetColumn.CellBackgroundPath"/>)
/// to that column's <c>CellBackgroundBrush</c> passed as the converter parameter (true), else transparent.
///
/// As an <see cref="IMultiValueConverter"/> it also composes with a whole-row tint: given
/// <c>[rowFlag, cellFlag]</c>, the row flag wins (the row's red <see cref="RowHighlight"/> brush) so a
/// flagged row stays uniformly tinted across every column, and only when the row isn't flagged does the
/// cell flag paint the column's own brush. This keeps a per-column highlight (e.g. the amber "collect this
/// rental chip" cell) from punching a hole in an unrecognised-chip row's red.
///
/// Always returns a non-null brush (<see cref="Brushes.Transparent"/> for no tint) so the cell keeps a
/// hit-testable background.
/// </summary>
public sealed class CellHighlight : IValueConverter, IMultiValueConverter
{
    public static readonly CellHighlight Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is IBrush brush ? brush : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    // [rowFlag, cellFlag]; parameter = the column's brush. Row tint wins over the cell tint.
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var rowFlag = values.Count > 0 && values[0] is true;
        var cellFlag = values.Count > 1 && values[1] is true;
        if (rowFlag)
            return RowHighlight.FlagBrush;
        return cellFlag && parameter is IBrush brush ? brush : Brushes.Transparent;
    }
}
