using System.Text;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Default CSV <see cref="ITabularWriter"/>. Serialises the table to RFC-4180 text — fields separated by
/// a semicolon (the European convention, which <see cref="CsvParser"/> sniffs and which Excel opens
/// correctly in a uk/ru locale), rows by CRLF. A field is quoted only when it contains the delimiter, a
/// quote or a newline, with embedded quotes doubled. The bytes are UTF-8 with a BOM so Excel detects the
/// encoding (and Cyrillic text survives the round-trip).
/// </summary>
public sealed class CsvTabularWriter : ITabularWriter
{
    private const char Delimiter = ';';

    public ExportFormat Format => ExportFormat.Csv;

    public byte[] Write(CsvParticipantData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var sb = new StringBuilder();
        AppendRecord(sb, data.Header);
        foreach (var row in data.Rows)
            AppendRecord(sb, row);

        // UTF-8 *with* BOM so Excel reads it as UTF-8 rather than the system ANSI code page.
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(sb.ToString());
    }

    private static void AppendRecord(StringBuilder sb, IReadOnlyList<string> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
                sb.Append(Delimiter);
            sb.Append(Escape(fields[i] ?? string.Empty));
        }
        sb.Append("\r\n");
    }

    // Quotes the field (doubling any embedded quote) only when it carries a character that would
    // otherwise break the row/field boundaries; plain values are written verbatim.
    private static string Escape(string field)
    {
        var mustQuote = field.IndexOf(Delimiter) >= 0
            || field.IndexOf('"') >= 0
            || field.IndexOf('\n') >= 0
            || field.IndexOf('\r') >= 0;

        if (!mustQuote)
            return field;

        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}
