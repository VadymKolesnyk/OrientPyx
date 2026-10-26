namespace OrientPyx.Plugins.Sdk;

/// <summary>How a result column's value is formatted.</summary>
public enum ColumnKind
{
    Number,
    Time,
    Text
}

/// <summary>An extra result column of a discipline. <see cref="Label"/> is a key of the plugin's strings or plain
/// text; <see cref="Value"/> reads the value from a result row.</summary>
public sealed record ResultColumnDef(string Key, string Label, ColumnKind Kind, Func<ResultRow, object?> Value, string? Format = null)
{
    /// <summary>The value of <paramref name="row"/> as display text (empty for null).</summary>
    public string FormatValue(ResultRow row)
    {
        object? value;
        try
        {
            value = Value(row);
        }
        catch
        {
            return string.Empty;
        }

        return value switch
        {
            null => string.Empty,
            TimeSpan t => Toolkit.TimeFormat.Span(t),
            IFormattable f when Format is not null => f.ToString(Format, System.Globalization.CultureInfo.CurrentCulture),
            _ => value.ToString() ?? string.Empty
        };
    }
}

/// <summary>Factories for <see cref="ResultColumnDef"/>.</summary>
public static class Column
{
    public static ResultColumnDef Number(string key, string label, Func<ResultRow, object?> value, string? format = null) =>
        new(key, label, ColumnKind.Number, value, format);

    public static ResultColumnDef Time(string key, string label, Func<ResultRow, object?> value) =>
        new(key, label, ColumnKind.Time, value);

    public static ResultColumnDef Text(string key, string label, Func<ResultRow, object?> value) =>
        new(key, label, ColumnKind.Text, value);
}
