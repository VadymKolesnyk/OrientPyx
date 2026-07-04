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
///   <item><c>Start time</c> / <c>Finish time</c>, each preceded by its <c>… DOW</c> weekday column —
///   times of day, dated by the recorded weekday relative to the <c>Read on</c> day so a course that
///   crosses midnight is timed correctly; left null when absent.</item>
///   <item>a variable tail of <c>&lt;n&gt; CN</c> / <c>&lt;n&gt; DOW</c> / <c>&lt;n&gt; time</c>
///   triplets after <c>No. of records</c> — the controls the chip punched, each likewise dated by its
///   own weekday.</item>
/// </list>
/// Rows are emitted as-is, including duplicate chip numbers (this is a raw log); de-duplication is
/// left to the consumer.
///
/// The file MUST carry a header row naming the columns (<c>SIID</c>, <c>Start time</c>, …); columns
/// are matched by name, so their order can vary. This is the layout SPORTident Reader produces with
/// the <b>Config+ (card readout)</b> list format. A file with no such header is rejected as not a
/// readout — the operator must select that list format in the reader software.
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

    // Each station carries its own day-of-week column right before its time ("Start DOW", "Finish
    // DOW", and the middle field of every punch triplet). We read them so a control punched after
    // midnight — a night start, a rogaine spanning days — lands on the correct calendar date, not the
    // date the chip was read out. DOW is the two-letter English code Mo/Tu/We/Th/Fr/Sa/Su.
    private const string StartDowColumn = "Start DOW";
    private const string FinishDowColumn = "Finish DOW";

    // The course punches are NOT the named "* CN" columns (Clear / Check / Start / Finish and their
    // "_r" siblings are the unit's housekeeping/station boxes, not controls). They are the headerless
    // tail of "<code> ; <DOW> ; <time>" triplets that each data row appends after this column, whose
    // value is the punch count.
    private const string RecordCountColumn = "No. of records";

    public bool CanParse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var firstLine = FirstLine(content);
        if (firstLine is null)
            return false;

        // A real readout begins with a header row naming the SIID column (the Config+ card-readout
        // list format). Anything else is not a file this parser can read.
        return IndexOf(SplitLine(firstLine), ChipColumn) >= 0;
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

        var layout = new ColumnLayout(
            Chip: IndexOf(header, ChipColumn),
            ReadOn: IndexOf(header, ReadOnColumn),
            Start: IndexOf(header, StartTimeColumn),
            StartDow: IndexOf(header, StartDowColumn),
            Finish: IndexOf(header, FinishTimeColumn),
            FinishDow: IndexOf(header, FinishDowColumn),
            // The punch triplets begin in the column immediately after "No. of records".
            FirstPunch: NextOrNone(IndexOf(header, RecordCountColumn)));

        if (layout.Chip < 0)
            throw new ReadoutFormatException(
                $"The file has no '{ChipColumn}' header column; it is not a SPORTident card-readout export. " +
                "Select the 'Config+ (card readout)' list format in the SPORTident reader software.");

        var records = new List<ChipReadRecord>();
        // The first line is the header, so data starts on the second line.
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0)
                continue;

            var fields = SplitLine(line);
            var chip = Field(fields, layout.Chip).Trim();
            if (chip.Length == 0)
                continue;

            records.Add(BuildRecord(fields, layout, chip));
        }

        return new ChipReadData { SourceFormat = SourceFormat, Records = records };
    }

    // Builds one chip's record, dating every time-of-day by its own day-of-week so a course that
    // crosses midnight is timed correctly (see <see cref="ReadoutTimeResolver"/>). The "Read on"
    // date/time is the anchor: it's the moment the chip was read at the finish, so every punch happened
    // on that day or a day before it. Order matters — the finish anchors the punches, and the earliest of
    // {first punch, finish} anchors the start — so a station whose DOW is blank still lands right.
    private static ChipReadRecord BuildRecord(IReadOnlyList<string> fields, ColumnLayout layout, string chip)
    {
        var readOn = layout.ReadOn >= 0 ? ParseReadOn(Field(fields, layout.ReadOn)) : null;

        var finish = ReadoutTimeResolver.Resolve(
            readOn,
            ReadoutTimeResolver.ParseTimeOfDay(Field(fields, layout.Finish)),
            ParseDayOfWeek(Field(fields, layout.FinishDow)),
            laterAnchor: null);
        // Punches carry their own DOW; the finish is the fallback anchor so a punch with a blank DOW is
        // still ordered monotonically before the finish.
        var punches = ReadPunches(fields, layout.FirstPunch, readOn, finish);

        // The start precedes the whole course, so anchor its fallback to the earliest dated event —
        // the first punch, or the finish when there are no punches — and step back if it reads later.
        var startAnchor = punches.FirstOrDefault(p => p.Time is not null)?.Time ?? finish;
        var start = ReadoutTimeResolver.Resolve(
            readOn,
            ReadoutTimeResolver.ParseTimeOfDay(Field(fields, layout.Start)),
            ParseDayOfWeek(Field(fields, layout.StartDow)),
            startAnchor);

        return new ChipReadRecord
        {
            ChipNumber = chip,
            StartTime = start,
            FinishTime = finish,
            Punches = punches
        };
    }

    // The resolved column positions for a file. -1 means "absent". StartDow/FinishDow are the
    // day-of-week columns that sit immediately before the matching time column.
    private readonly record struct ColumnLayout(
        int Chip, int ReadOn, int Start, int StartDow, int Finish, int FinishDow, int FirstPunch);

    // The column after a found one, or -1 when the source column was absent.
    private static int NextOrNone(int index) => index >= 0 ? index + 1 : -1;

    // --- Punches -------------------------------------------------------------

    // Reads the trailing punch triplets — "<code> ; <DOW> ; <time>", repeated — that start at
    // <paramref name="firstPunchIndex"/> and run to the end of the row. A blank triplet (all three
    // fields empty) is a MISSED punch — a reader-station glitch drops one control's record while later
    // ones survive — so it is skipped, not treated as the end of the list; scanning simply runs to the
    // last field (the trailing empties from the row's final ';' contribute nothing). Each punch's own
    // DOW dates its time (a control taken after midnight gets the right calendar day); the finish is the
    // monotonic fallback anchor for a punch whose DOW is missing/unreadable.
    private static IReadOnlyList<ChipPunch> ReadPunches(
        IReadOnlyList<string> fields,
        int firstPunchIndex,
        DateTimeOffset? readOn,
        DateTimeOffset? finish)
    {
        if (firstPunchIndex < 0)
            return [];

        // The fallback anchor walks backwards from the finish: a punch with no DOW is assumed to be no
        // later than the next (already-dated) punch after it, so if its time-of-day is greater we step a
        // day back. We collect first, then date from the end.
        var raw = new List<(string Code, TimeSpan? Time, DayOfWeek? Dow)>();
        for (var i = firstPunchIndex; i < fields.Count; i += 3)
        {
            var code = Field(fields, i).Trim();
            var time = ReadoutTimeResolver.ParseTimeOfDay(Field(fields, i + 2));
            // A wholly-empty triplet is either a missed punch (skip, keep scanning) or the row's trailing
            // blanks (also nothing to add) — both handled by simply not recording it.
            if (code.Length == 0 && time is null)
                continue;
            // Triplet is <code>;<DOW>;<time>; tolerate a truncated final triplet.
            raw.Add((code, time, ParseDayOfWeek(Field(fields, i + 1))));
        }

        var stamps = ReadoutTimeResolver.ResolvePunches(
            readOn, raw.ConvertAll(r => (r.Time, r.Dow)), finish);

        var punches = new ChipPunch[raw.Count];
        for (var i = 0; i < raw.Count; i++)
            punches[i] = new ChipPunch(raw[i].Code, stamps[i]);
        return punches;
    }

    // --- Time parsing --------------------------------------------------------

    // Parses SPORTident's two-letter English weekday code (Mo/Tu/We/Th/Fr/Sa/Su). Anything else → null,
    // which routes the timestamp through the monotonic fallback instead.
    private static DayOfWeek? ParseDayOfWeek(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "mo" => DayOfWeek.Monday,
        "tu" => DayOfWeek.Tuesday,
        "we" => DayOfWeek.Wednesday,
        "th" => DayOfWeek.Thursday,
        "fr" => DayOfWeek.Friday,
        "sa" => DayOfWeek.Saturday,
        "su" => DayOfWeek.Sunday,
        _ => null
    };

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
