using ClosedXML.Excel;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.DataAccess.Documents;

/// <summary>
/// .xlsx <see cref="ITabularWriter"/>. Writes the table to a single-worksheet workbook with ClosedXML —
/// the header row in bold, then one row per record — and returns the saved bytes. The inverse of
/// <see cref="XlsxParser"/>; like it, it lives in DataAccess because it needs the spreadsheet library
/// (BusinessLogic must not reference ClosedXML). Every cell is written as text so values the table
/// already formatted for display (dates as dd.MM.yyyy, numbers without a stray ".0") survive verbatim.
/// </summary>
public sealed class XlsxTabularWriter : ITabularWriter
{
    public ExportFormat Format => ExportFormat.Excel;

    public byte[] Write(CsvParticipantData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Учасники");

        for (var c = 0; c < data.Header.Count; c++)
            sheet.Cell(1, c + 1).SetValue(data.Header[c] ?? string.Empty);
        sheet.Row(1).Style.Font.Bold = true;

        var r = 2;
        foreach (var row in data.Rows)
        {
            for (var c = 0; c < row.Count; c++)
                // Write as text so the table's already-formatted strings aren't re-interpreted by Excel
                // (e.g. a chip "012" keeping its leading zero, a date staying dd.MM.yyyy).
                sheet.Cell(r, c + 1).SetValue(row[c] ?? string.Empty);
            r++;
        }

        sheet.Columns().AdjustToContents();

        using var memory = new MemoryStream();
        workbook.SaveAs(memory);
        return memory.ToArray();
    }
}
