using ClosedXML.Excel;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.DataAccess.Documents;

/// <summary>
/// Default <see cref="ISpreadsheetParser"/>. Reads the first worksheet of an .xlsx workbook with
/// ClosedXML into the layer-neutral <see cref="CsvParticipantData"/> the CSV path also produces, so an
/// Excel import reuses the whole column-mapping + import flow. The first non-empty row is the header;
/// every following used row (up to the last cell of any column the header spans) becomes a record, with
/// each cell stringified — dates as <c>dd.MM.yyyy</c> and plain numbers without a trailing ".0".
/// </summary>
public sealed class XlsxParser : ISpreadsheetParser
{
    public CsvParticipantData Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
            throw new SpreadsheetFormatException("The file is empty.");

        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(new MemoryStream(bytes));
        }
        catch (Exception ex)
        {
            throw new SpreadsheetFormatException("The file is not a readable .xlsx workbook.", ex);
        }

        using (workbook)
        {
            var sheet = workbook.Worksheets.FirstOrDefault()
                ?? throw new SpreadsheetFormatException("The workbook has no worksheets.");

            // The used range bounds the data; an empty sheet has none.
            var range = sheet.RangeUsed();
            if (range is null)
                throw new SpreadsheetFormatException("The worksheet is empty.");

            var rows = range.RowsUsed().ToList();
            if (rows.Count == 0)
                throw new SpreadsheetFormatException("The worksheet has no header row.");

            // The header spans the used columns; remember its width so every row lines up by index.
            var firstColumn = range.FirstColumn().ColumnNumber();
            var lastColumn = range.LastColumn().ColumnNumber();
            var width = lastColumn - firstColumn + 1;

            var header = ReadRow(rows[0], firstColumn, width);
            if (header.All(string.IsNullOrWhiteSpace))
                throw new SpreadsheetFormatException("The worksheet's header row is empty.");

            var dataRows = new List<IReadOnlyList<string>>(rows.Count - 1);
            for (var i = 1; i < rows.Count; i++)
            {
                var cells = ReadRow(rows[i], firstColumn, width);
                if (cells.All(string.IsNullOrWhiteSpace))
                    continue; // skip wholly blank rows
                dataRows.Add(cells);
            }

            return new CsvParticipantData { Header = header, Rows = dataRows };
        }
    }

    // Reads `width` cells starting at `firstColumn`, stringifying each.
    private static IReadOnlyList<string> ReadRow(IXLRangeRow row, int firstColumn, int width)
    {
        var cells = new string[width];
        for (var i = 0; i < width; i++)
            cells[i] = Stringify(row.Worksheet.Cell(row.RowNumber(), firstColumn + i));
        return cells;
    }

    // Turns a cell into the text the mapping/import expects: dates as dd.MM.yyyy, whole numbers without
    // a ".0" tail, everything else via its formatted string. Blank cells become "".
    private static string Stringify(IXLCell cell)
    {
        if (cell.IsEmpty())
            return string.Empty;

        if (cell.DataType == XLDataType.DateTime && cell.Value.IsDateTime)
            return cell.Value.GetDateTime().ToString("dd.MM.yyyy");

        if (cell.DataType == XLDataType.Number && cell.Value.IsNumber)
        {
            var n = cell.Value.GetNumber();
            // Keep integers integral ("123" not "123.0"); preserve real fractions.
            if (n == Math.Floor(n) && !double.IsInfinity(n))
                return ((long)n).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return cell.GetFormattedString().Trim();
    }
}
