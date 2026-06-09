using System.Globalization;
using Avalonia.Data.Converters;

namespace OrientDesk.Presentation.Converters;

/// <summary>
/// Bridges a view-model <see cref="DateTimeOffset"/>? to the <see cref="DateTime"/>? that
/// <c>CalendarDatePicker.SelectedDate</c> expects. These are calendar days, not instants, so we
/// treat the wall-clock date as-is in both directions — no time-zone conversion — to avoid the
/// off-by-one-day shift that <c>LocalDateTime</c> would introduce near midnight.
/// </summary>
public sealed class DateTimeOffsetToDateTimeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            DateTimeOffset dto => dto.DateTime.Date,
            DateTime dt => dt.Date,
            _ => null
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            DateTime dt => new DateTimeOffset(dt.Date, TimeSpan.Zero),
            DateTimeOffset dto => dto,
            _ => null
        };
}
