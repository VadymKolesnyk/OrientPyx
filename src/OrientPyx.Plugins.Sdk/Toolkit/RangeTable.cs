using System.Globalization;

namespace OrientPyx.Plugins.Sdk.Toolkit;

/// <summary>
/// A «range × key → value» lookup, e.g. age × gender → coefficient. Each row has a range («8–10», «18», «70+») and a
/// numeric value per key column. A number below the first range takes the first row, above the last range the last
/// row (its upper end is open — «70+»), and one in a gap between rows the nearest row (Р-47).
/// </summary>
public sealed class RangeTable
{
    private readonly List<Row> _rows;

    private RangeTable(List<Row> rows) => _rows = rows;

    /// <summary>The number of usable rows (a row whose range does not parse is skipped).</summary>
    public int Count => _rows.Count;

    /// <summary>
    /// Builds the table from a table setting: <paramref name="rangeColumn"/> holds each row's range, every other
    /// column a value keyed by its column key.
    /// </summary>
    public static RangeTable FromSetting(TableValue? value, string rangeColumn = "range")
    {
        var rows = new List<Row>();
        if (value is null)
            return new RangeTable(rows);

        foreach (var cells in value.Rows)
        {
            if (!cells.TryGetValue(rangeColumn, out var rangeText) || !TryParseRange(rangeText, out var min, out var max))
                continue;
            var values = new Dictionary<string, decimal>(TextKey.Comparer);
            foreach (var (key, text) in cells)
                if (key != rangeColumn && TableValue.ParseNumber(text) is { } number)
                    values[key] = number;
            rows.Add(new Row(min, max, values));
        }

        rows.Sort((a, b) => a.Min.CompareTo(b.Min));
        return new RangeTable(rows);
    }

    /// <summary>
    /// The value for <paramref name="number"/> in column <paramref name="key"/> (matched ignoring case and look-alike
    /// letters); null when the table is empty or the chosen row has no value for the key.
    /// </summary>
    public double? Lookup(int number, string key)
    {
        if (_rows.Count == 0)
            return null;

        var row = _rows.FirstOrDefault(r => number >= r.Min && number <= r.Max) ?? Nearest(number);
        return row.Values.TryGetValue(key, out var value) ? (double)value : null;
    }

    // Below the first row → the first; above the last → the last (open upward); in a gap → the closer row
    // (the lower one on a tie).
    private Row Nearest(int number)
    {
        if (number < _rows[0].Min)
            return _rows[0];

        var best = _rows[0];
        var bestDistance = int.MaxValue;
        foreach (var row in _rows)
        {
            var distance = number < row.Min ? row.Min - number : number - row.Max;
            if (distance < bestDistance)
            {
                best = row;
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary>Parses «8–10», «8-10», «18», «70+» (open upward).</summary>
    public static bool TryParseRange(string? text, out int min, out int max)
    {
        min = max = 0;
        var t = (text ?? string.Empty).Trim().Replace('–', '-').Replace('—', '-').Replace(" ", string.Empty);
        if (t.Length == 0)
            return false;

        if (t.EndsWith('+'))
        {
            max = int.MaxValue;
            return int.TryParse(t[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out min);
        }

        var dash = t.IndexOf('-', 1);
        if (dash < 0)
        {
            if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out min))
                return false;
            max = min;
            return true;
        }

        if (!int.TryParse(t[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out min)
            || !int.TryParse(t[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out max))
            return false;
        if (max < min)
            (min, max) = (max, min);
        return true;
    }

    private sealed record Row(int Min, int Max, Dictionary<string, decimal> Values);
}
