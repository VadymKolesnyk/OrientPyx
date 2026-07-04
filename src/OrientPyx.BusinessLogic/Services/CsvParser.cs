using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Default <see cref="ICsvParser"/>. A small RFC-4180-style reader that handles quoted fields
/// (<c>"…"</c>), escaped quotes (<c>""</c>) and newlines inside quotes, supporting both comma- and
/// semicolon-separated files (European exports use <c>;</c>). The delimiter is sniffed from the first
/// (header) line — whichever of <c>;</c> or <c>,</c> occurs more often outside quotes wins, defaulting
/// to comma. The header is taken from the first non-empty record; the remaining records become rows,
/// each padded to the header width so cells line up by column index.
/// </summary>
public sealed class CsvParser : ICsvParser
{
    public CsvParticipantData Parse(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            throw new CsvFormatException("The file is empty.");

        var delimiter = SniffDelimiter(csv);
        var records = ReadRecords(csv, delimiter);
        if (records.Count == 0)
            throw new CsvFormatException("The file has no header row.");

        var header = records[0];
        if (header.All(string.IsNullOrWhiteSpace))
            throw new CsvFormatException("The file's header row is empty.");

        var width = header.Count;
        var rows = new List<IReadOnlyList<string>>(records.Count - 1);
        for (var i = 1; i < records.Count; i++)
        {
            var record = records[i];
            // Skip wholly blank lines (trailing newline, separators).
            if (record.All(string.IsNullOrWhiteSpace))
                continue;
            rows.Add(Normalise(record, width));
        }

        return new CsvParticipantData { Header = header, Rows = rows };
    }

    // Pads/truncates a record to the header width so every row is indexable by column.
    private static IReadOnlyList<string> Normalise(IReadOnlyList<string> record, int width)
    {
        if (record.Count == width)
            return record;

        var cells = new string[width];
        for (var i = 0; i < width; i++)
            cells[i] = i < record.Count ? record[i] : string.Empty;
        return cells;
    }

    // Counts unquoted ; vs , on the first line; the more frequent wins (comma on a tie/none).
    private static char SniffDelimiter(string csv)
    {
        int semicolons = 0, commas = 0;
        var inQuotes = false;
        foreach (var ch in csv)
        {
            if (ch == '"')
                inQuotes = !inQuotes;
            else if (!inQuotes && ch == ';')
                semicolons++;
            else if (!inQuotes && ch == ',')
                commas++;
            else if (!inQuotes && (ch == '\n' || ch == '\r'))
                break; // only the first line decides
        }
        return semicolons > commas ? ';' : ',';
    }

    // Splits the whole text into records (each a list of field strings), honouring quotes.
    private static List<IReadOnlyList<string>> ReadRecords(string csv, char delimiter)
    {
        var records = new List<IReadOnlyList<string>>();
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    // A doubled quote is a literal quote; otherwise the quoted section ends.
                    if (i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
            }
            else if (ch == '"')
            {
                inQuotes = true;
            }
            else if (ch == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (ch == '\r')
            {
                // Swallow CR; the record ends on the following LF (or here if LF is absent).
                if (i + 1 < csv.Length && csv[i + 1] == '\n')
                    continue;
                EndRecord(records, fields, field);
            }
            else if (ch == '\n')
            {
                EndRecord(records, fields, field);
            }
            else
            {
                field.Append(ch);
            }
        }

        // Flush a final record with no trailing newline.
        if (field.Length > 0 || fields.Count > 0)
            EndRecord(records, fields, field);

        return records;
    }

    private static void EndRecord(
        List<IReadOnlyList<string>> records,
        List<string> fields,
        System.Text.StringBuilder field)
    {
        fields.Add(field.ToString());
        field.Clear();
        records.Add(fields.Select(f => f.Trim()).ToArray());
        fields.Clear();
    }
}
