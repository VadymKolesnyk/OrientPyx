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
///
/// Two real-world shapes of this export exist and both are handled:
/// <list type="bullet">
///   <item><b>Header mode</b> — the first line is a header row naming the columns (<c>SIID</c>,
///   <c>Start time</c>, …). Columns are matched by name, so their order can vary.</item>
///   <item><b>Positional mode</b> — the export carries <b>no</b> header line; the very first line is
///   already a data record (<c>&lt;no&gt;;&lt;read on&gt;;&lt;SIID&gt;;…</c>). The fields then sit at
///   fixed positions. SI-Config and some SI readers produce this headerless layout.</item>
/// </list>
/// The parser auto-detects: a first line containing the <c>SIID</c> token is treated as a header,
/// otherwise every line (including the first) is read positionally.
/// </summary>
public sealed class SportIdentCsvReadoutParser : IReadoutParser
{
    private const char Delimiter = ';';
    private const string SourceFormat = "SportIdent-CSV";

    // Header names this parser keys on (header mode). A file missing SIID is not a chip readout.
    private const string ChipColumn = "SIID";
    private const string ReadOnColumn = "Read on";
    private const string StartTimeColumn = "Start time";
    private const string FinishTimeColumn = "Finish time";

    // The course punches are NOT the named "* CN" columns (Clear / Check / Start / Finish and their
    // "_r" siblings are the unit's housekeeping/station boxes, not controls). They are the headerless
    // tail of "<code> ; <DOW> ; <time>" triplets that each data row appends after this column, whose
    // value is the punch count.
    private const string RecordCountColumn = "No. of records";

    // Fixed field positions (0-based) for the HEADERLESS positional layout. These mirror the SI-Config
    // CSV export: <no>;<read on>;<SIID>;... with the Clear / Check / Start / Finish station triplets
    // ("<code>;<DOW>;<time>") and "No. of records" at fixed offsets, followed by the variable tail of
    // "<code>;<DOW>;<time>" course-punch triplets.
    private const int PositionalChipIndex = 2;          // SIID
    private const int PositionalReadOnIndex = 1;         // "Read on" date+time
    private const int PositionalClearCodeIndex = 4;      // Clear station triplet's code field
    private const int PositionalStartCodeIndex = 10;     // Start station triplet's code field
    private const int PositionalStartTimeIndex = 12;     // Start station triplet's time field
    private const int PositionalFinishTimeIndex = 21;    // Finish station triplet's time field
    private const int PositionalRecordCountIndex = 44;   // "No. of records"; punch triplets follow it

    public bool CanParse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var firstLine = FirstLine(content);
        if (firstLine is null)
            return false;

        var fields = SplitLine(firstLine);

        // Header mode: a real header names the SIID column. Positional mode: no header, but the line
        // is a data row whose third field is the chip number — accept when that field is non-empty.
        return IndexOf(fields, ChipColumn) >= 0
               || Field(fields, PositionalChipIndex).Trim().Length > 0;
    }

    public ChipReadData Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ReadoutFormatException("The file is empty.");

        // SPORTident exports are UTF-8 and may carry a BOM; strip it so the first field/header matches.
        var text = content.TrimStart('﻿');
        var lines = text.Split('\n');

        if (lines.Length == 0)
            throw new ReadoutFormatException("The file has no rows.");

        var header = SplitLine(lines[0]);
        var hasHeader = IndexOf(header, ChipColumn) >= 0;

        var layout = hasHeader
            ? new ColumnLayout(
                Chip: IndexOf(header, ChipColumn),
                ReadOn: IndexOf(header, ReadOnColumn),
                Start: IndexOf(header, StartTimeColumn),
                // In header mode the named "Start time" column already isolates the start station, so
                // there is no clear-vs-start ambiguity to guard against.
                ClearCode: -1,
                StartCode: -1,
                Finish: IndexOf(header, FinishTimeColumn),
                // The punch triplets begin in the column immediately after "No. of records".
                FirstPunch: NextOrNone(IndexOf(header, RecordCountColumn)))
            : new ColumnLayout(
                Chip: PositionalChipIndex,
                ReadOn: PositionalReadOnIndex,
                Start: PositionalStartTimeIndex,
                ClearCode: PositionalClearCodeIndex,
                StartCode: PositionalStartCodeIndex,
                Finish: PositionalFinishTimeIndex,
                FirstPunch: PositionalRecordCountIndex + 1);

        if (layout.Chip < 0)
            throw new ReadoutFormatException($"The file has no '{ChipColumn}' column; it is not a SPORTident readout.");

        // In header mode the first line is the header; in positional mode it is already a data row.
        var firstDataLine = hasHeader ? 1 : 0;

        var records = new List<ChipReadRecord>();
        for (var i = firstDataLine; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0)
                continue;

            var fields = SplitLine(line);
            var chip = Field(fields, layout.Chip).Trim();
            if (chip.Length == 0)
                continue;

            var readOn = layout.ReadOn >= 0 ? ParseReadOn(Field(fields, layout.ReadOn)) : null;
            records.Add(new ChipReadRecord
            {
                ChipNumber = chip,
                StartTime = ReadStartTime(fields, layout, readOn),
                FinishTime = CombineDateAndTime(readOn, Field(fields, layout.Finish)),
                Punches = ReadPunches(fields, layout.FirstPunch, readOn)
            });
        }

        return new ChipReadData { SourceFormat = SourceFormat, Records = records };
    }

    // The resolved column positions for a file, whichever mode it is in. -1 means "absent".
    // ClearCode/StartCode are the station-box code fields used to tell a genuine start punch from a
    // device that merely copied the clear-box punch into the start slot (positional mode only).
    private readonly record struct ColumnLayout(
        int Chip, int ReadOn, int Start, int ClearCode, int StartCode, int Finish, int FirstPunch);

    // The column after a found one, or -1 when the source column was absent.
    private static int NextOrNone(int index) => index >= 0 ? index + 1 : -1;

    // The start time, but only when the chip carries a genuine start punch. Some SI readers fill the
    // start station slot with a copy of the clear-box punch when no real start station was used; in that
    // case the start triplet's station code equals the clear triplet's code, and we report no start
    // (null) so the existing value is never overwritten with a clear time.
    private static DateTimeOffset? ReadStartTime(IReadOnlyList<string> fields, ColumnLayout layout, DateTimeOffset? readOn)
    {
        if (layout.StartCode >= 0 && layout.ClearCode >= 0)
        {
            var startCode = Field(fields, layout.StartCode).Trim();
            var clearCode = Field(fields, layout.ClearCode).Trim();
            // No start code at all, or merely the clear punch copied over → no genuine start.
            if (startCode.Length == 0 ||
                string.Equals(startCode, clearCode, StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return CombineDateAndTime(readOn, Field(fields, layout.Start));
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
