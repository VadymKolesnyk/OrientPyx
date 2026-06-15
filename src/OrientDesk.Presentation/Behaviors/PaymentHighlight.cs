using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Behaviors;

/// <summary>
/// Maps a payment («Оплата») <see cref="PaymentStatus"/> to the cell background brush: blank ⇒ amber,
/// more than the fee ⇒ green, less ⇒ red, non-numeric ⇒ blue, exactly equal ⇒ transparent (no tint).
/// Used as a value converter so the whole cell (a stretched <c>SheetCell</c>) is tinted — including
/// empty cells — rather than only the inner label's text footprint.
///
/// Returns <see cref="Brushes.Transparent"/> (never null) for the no-tint case so the cell keeps a
/// non-null, hit-testable background (a click anywhere in the cell still reaches the editor).
/// </summary>
public sealed class PaymentHighlight : IValueConverter
{
    public static readonly PaymentHighlight Instance = new();

    // Pastel tints matching the Google-Sheets-style palette: amber (empty), green (over), red (under),
    // blue (not-a-number). Equal ⇒ transparent.
    private static readonly IBrush EmptyBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xC4));
    private static readonly IBrush OverBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xE7, 0xD2));
    private static readonly IBrush UnderBrush = new SolidColorBrush(Color.FromRgb(0xF6, 0xCB, 0xC8));
    private static readonly IBrush NotANumberBrush = new SolidColorBrush(Color.FromRgb(0xCF, 0xE0, 0xF3));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is PaymentStatus status
            ? status switch
            {
                PaymentStatus.Empty => EmptyBrush,
                PaymentStatus.Over => OverBrush,
                PaymentStatus.Under => UnderBrush,
                PaymentStatus.NotANumber => NotANumberBrush,
                _ => Brushes.Transparent // Equal ⇒ no tint
            }
            : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
