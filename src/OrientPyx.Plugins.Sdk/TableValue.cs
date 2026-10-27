using System.Globalization;

namespace OrientPyx.Plugins.Sdk;

/// <summary>How a table setting's column is edited and read.</summary>
public enum TableColumnKind
{
    Text,
    Number
}

/// <summary>One column of a table setting. <see cref="Label"/> is a key of the plugin's strings or plain text.</summary>
public sealed record TableColumnDef(string Key, string Label, TableColumnKind Kind = TableColumnKind.Text)
{
    public static TableColumnDef Text(string key, string label) => new(key, label, TableColumnKind.Text);

    public static TableColumnDef Number(string key, string label) => new(key, label, TableColumnKind.Number);
}

/// <summary>
/// The value of a table setting: rows of cells keyed by column key. Cells are stored as text; <see cref="Number"/>
/// reads a numeric cell (comma or dot as the decimal separator).
/// </summary>
public sealed class TableValue
{
    public static readonly TableValue Empty = new([]);

    public TableValue(IEnumerable<IReadOnlyDictionary<string, string?>> rows) =>
        Rows = rows.Select(r => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>(r, StringComparer.Ordinal))
            .ToList();

    /// <summary>Builds a table from positional rows: the cells of each row in the order of <paramref name="columns"/>.</summary>
    public static TableValue FromRows(IReadOnlyList<TableColumnDef> columns, params string?[][] rows) =>
        new(rows.Select(cells =>
        {
            var row = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var i = 0; i < columns.Count && i < cells.Length; i++)
                row[columns[i].Key] = cells[i];
            return (IReadOnlyDictionary<string, string?>)row;
        }));

    public IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows { get; }

    /// <summary>The text of a cell; null when blank or missing.</summary>
    public string? Cell(int row, string column) =>
        row >= 0 && row < Rows.Count && Rows[row].TryGetValue(column, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v.Trim()
            : null;

    /// <summary>A numeric cell; null when blank or not a number.</summary>
    public decimal? Number(int row, string column) => ParseNumber(Cell(row, column));

    /// <summary>Parses a number written with a comma or a dot.</summary>
    public static decimal? ParseNumber(string? text) =>
        decimal.TryParse(text?.Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
}
