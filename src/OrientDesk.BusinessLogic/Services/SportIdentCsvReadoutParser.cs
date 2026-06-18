using System.Globalization;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>
/// Default <see cref="IReadoutParser"/> for SPORTident readout CSV exports (the "SIRap"/Config+
/// punch log): a semicolon-separated file whose first line is a header row. Columns are matched by
/// <b>header name</b>, not position, so the export's column order can vary.
///
/// The relevant columns are:
/// <list type="bullet">
///   <item><c>SIID</c> — the chip number (the only field rental-chip import needs).</item>
///   <item><c>Start time</c> / <c>Finish time</c> — times of day; combined with the row's
///   <c>Read on</c> date when present, otherwise left null.</item>
///   <item>a variable tail of <c>&lt;n&gt; CN</c> / <c>&lt;n&gt; DOW</c> / <c>&lt;n&gt; time</c>
///   triplets after <c>No. of records</c> — the controls the chip punched.</item>
/// </list>
/// Rows are emitted as-is, including duplicate chip numbers (this is a raw log); de-duplication is
/// left to the consumer.
/// </summary>
public sealed class SportIdentCsvReadoutParser : IReadoutParser
{
    private const char Delimiter = ';';
    private const string SourceFormat = "SportIdent-CSV";

    // Header names this parser keys on. A file missing SIID is not a chip readout.
    private const string ChipColumn = "SIID";
    private const string ReadOnColumn = "Read on";
    private const string StartTimeColumn = "Start time";
    private const string FinishTimeColumn = "Finish time";

    // The course punches are NOT the named "* CN" columns (Clear / Check / Start / Finish and their
    // "_r" siblings are the unit's housekeeping/station boxes, not controls). They are the headerless
    // tail of "<code> ; <DOW> ; <time>" triplets that each data row appends after this column, whose
    // value is the punch count.
    private const string RecordCountColumn = "No. of records";

    public bool CanParse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var header = FirstLine(content);
        if (header is null)
            return false;

        var columns = SplitLine(header);
        return IndexOf(columns, ChipColumn) >= 0;
    }

    public ChipReadData Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ReadoutFormatException("The file is empty.");

        // SPORTident exports are UTF-8 and may carry a BOM; strip it so the first header matches.
        var text = content.TrimStart('﻿');
        var lines = text.Split('\n');

        if (lines.Length == 0)
            throw new ReadoutFormatException("The file has no header row.");

        var header = SplitLine(lines[0]);
        var chipIndex = IndexOf(header, ChipColumn);
        if (chipIndex < 0)
            throw new ReadoutFormatException($"The file has no '{ChipColumn}' column; it is not a SPORTident readout.");

        var readOnIndex = IndexOf(header, ReadOnColumn);
        var startIndex = IndexOf(header, StartTimeColumn);
        var finishIndex = IndexOf(header, FinishTimeColumn);

        // The course punches are the headerless "<code> ; <DOW> ; <time>" triplets each row appends
        // right after the "No. of records" column; that column's value is the punch count. The first
        // punch field is therefore the column immediately after it.
        var recordCountIndex = IndexOf(header, RecordCountColumn);
        var firstPunchIndex = recordCountIndex >= 0 ? recordCountIndex + 1 : -1;

        var records = new List<ChipReadRecord>();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0)
                continue;

            var fields = SplitLine(line);
            var chip = Field(fields, chipIndex).Trim();
            if (chip.Length == 0)
                continue;

            var readOn = readOnIndex >= 0 ? ParseReadOn(Field(fields, readOnIndex)) : null;
            records.Add(new ChipReadRecord
            {
                ChipNumber = chip,
                StartTime = CombineDateAndTime(readOn, Field(fields, startIndex)),
                FinishTime = CombineDateAndTime(readOn, Field(fields, finishIndex)),
                Punches = ReadPunches(fields, firstPunchIndex, readOn)
            });
        }

        return new ChipReadData { SourceFormat = SourceFormat, Records = records };
    }

    // --- Punches -------------------------------------------------------------

    // Reads the trailing punch triplets — "<code> ; <DOW> ; <time>", repeated — that start at
    // <paramref name="firstPunchIndex"/> and run to the end of the row. A triplet with a blank code
    // ends the list (trailing empty fields from the row's final ';'). The middle DOW field is ignored;
    // the time is combined with the row's "Read on" date into a real timestamp.
    private static IReadOnlyList<ChipPunch> ReadPunches(
        IReadOnlyList<string> fields,
        int firstPunchIndex,
        DateTimeOffset? date)
    {
        if (firstPunchIndex < 0)
            return [];

        var punches = new List<ChipPunch>();
        for (var i = firstPunchIndex; i < fields.Count; i += 3)
        {
            var code = fields[i].Trim();
            if (code.Length == 0)
                break;
            // The time is the third field of the triplet; tolerate a truncated final triplet.
            punches.Add(new ChipPunch(code, CombineDateAndTime(date, Field(fields, i + 2))));
        }
        return punches;
    }

    // --- Time parsing --------------------------------------------------------

    // Readout times are a day-of-week + time-of-day (e.g. "Sa; 11:21:23.117"); after SplitLine the
    // value is just the time part. We combine it with the row's "Read on" date when available so a
    // bare time of day becomes a real timestamp; otherwise we return null (the date is unknown).
    private static DateTimeOffset? CombineDateAndTime(DateTimeOffset? date, string? timeValue)
    {
        var time = ParseTimeOfDay(timeValue);
        if (time is null)
            return null;
        if (date is null)
            return null;
        return date.Value.Date + time.Value;
    }

    private static TimeSpan? ParseTimeOfDay(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var t) ? t : null;
    }

    private static DateTimeOffset? ParseReadOn(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
            ? d
            : null;
    }

    // --- CSV helpers ---------------------------------------------------------

    private static string? FirstLine(string content)
    {
        var newline = content.IndexOf('\n');
        var line = (newline < 0 ? content : content[..newline]).TrimStart('﻿').TrimEnd('\r');
        return line.Length == 0 ? null : line;
    }

    // The SPORTident export quotes nothing, so a plain split on ';' is correct and avoids pulling
    // in a CSV dependency. Trailing empty fields (the file ends each row with ';') are harmless.
    private static string[] SplitLine(string line) => line.TrimEnd('\r').Split(Delimiter);

    private static string Field(IReadOnlyList<string> fields, int index) =>
        index >= 0 && index < fields.Count ? fields[index] : string.Empty;

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
            if (string.Equals(columns[i].Trim(), name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}
