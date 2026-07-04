using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>
/// <see cref="IReadoutParser"/> for the <b>Sport Time</b> timing-system CSV export: a semicolon-separated
/// file with a named header, matched by column name. It differs from the SPORTident export in several ways
/// this parser handles:
/// <list type="bullet">
///   <item>Columns are named <c>SI-Card</c> (chip), <c>read at</c> (the read moment — a <b>time only</b>,
///   no date), <c>start time</c>, <c>Finish time</c>, and after <c>No. of punches</c> a tail of
///   <c>N.CN;N.DOW;N.Time</c> punch triplets.</item>
///   <item>The dedicated <c>*_DOW</c> columns are always empty; instead the weekday, when present, is written
///   in parentheses after the time itself — e.g. <c>11:17:16.187 (Вт)</c> — using Ukrainian codes, and it
///   may be absent entirely.</item>
///   <item>The file has no date at all, so timestamps are placed on a synthetic base date (anchored to the
///   finish's weekday when known); only time differences matter downstream, and midnight crossings are kept
///   correct by the weekday-in-parentheses plus the monotonic fallback (see <see cref="ReadoutTimeResolver"/>).</item>
///   <item>A wholly-empty punch triplet is a MISSED punch (reader-station glitch) — skipped, not treated as
///   the end of the list — so later punches are still read.</item>
/// </list>
/// The bytes are windows-1251 in practice; decoding to text is the caller's job (this parses text only).
/// </summary>
public sealed class SportTimeCsvReadoutParser : IReadoutParser
{
    private const char Delimiter = ';';
    private const string SourceFormat = "SportTime-CSV";

    private const string ChipColumn = "SI-Card";
    private const string ReadAtColumn = "read at";
    private const string StartTimeColumn = "start time";
    private const string FinishTimeColumn = "Finish time";
    private const string PunchCountColumn = "No. of punches";

    // A synthetic base date the file itself doesn't carry. Its weekday is shifted to match the finish's
    // parenthesised DOW when there is one, so weekday-relative dating works; otherwise it's arbitrary and
    // only the monotonic fallback orders the course. Absolute dates are meaningless here — only diffs are.
    private static readonly DateTimeOffset SyntheticBase =
        new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero); // 2001-01-01 is a Monday.

    public bool CanParse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var firstLine = FirstLine(content);
        if (firstLine is null)
            return false;

        // A real Sport Time readout names the SI-Card column in its header.
        return IndexOf(SplitLine(firstLine), ChipColumn) >= 0;
    }

    public ChipReadData Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ReadoutFormatException("The file is empty.");

        var text = content.TrimStart('﻿');
        var lines = text.Split('\n');
        if (lines.Length == 0)
            throw new ReadoutFormatException("The file has no rows.");

        var header = SplitLine(lines[0]);
        var layout = new ColumnLayout(
            Chip: IndexOf(header, ChipColumn),
            Start: IndexOf(header, StartTimeColumn),
            Finish: IndexOf(header, FinishTimeColumn),
            FirstPunch: NextOrNone(IndexOf(header, PunchCountColumn)));

        if (layout.Chip < 0)
            throw new ReadoutFormatException(
                $"The file has no '{ChipColumn}' header column; it is not a Sport Time readout export.");

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

    private static ChipReadRecord BuildRecord(IReadOnlyList<string> fields, ColumnLayout layout, string chip)
    {
        var (finishTime, finishDow) = SplitTimeAndDow(Field(fields, layout.Finish));
        // Anchor the synthetic base date's weekday to the finish's DOW (when known) so weekday-relative
        // dating lines up; without one the base weekday is arbitrary and the monotonic fallback takes over.
        var baseDate = AnchorBase(finishDow);

        var finish = ReadoutTimeResolver.Resolve(baseDate, finishTime, finishDow, laterAnchor: null);

        var (raw, punches) = ReadPunches(fields, layout.FirstPunch, baseDate, finish);

        var (startTime, startDow) = SplitTimeAndDow(Field(fields, layout.Start));
        var startAnchor = punches.FirstOrDefault(p => p.Time is not null)?.Time ?? finish;
        var start = ReadoutTimeResolver.Resolve(baseDate, startTime, startDow, startAnchor);

        return new ChipReadRecord
        {
            ChipNumber = chip,
            StartTime = start,
            FinishTime = finish,
            Punches = punches
        };
    }

    // Reads the trailing "N.CN;N.DOW;N.Time" triplets. The dedicated DOW field is empty; the weekday, if
    // any, rides in parentheses after the time. A wholly-empty triplet is a missed punch (skip, keep
    // scanning) or the row's trailing blanks — either way not recorded.
    private static (List<string> Raw, ChipPunch[] Punches) ReadPunches(
        IReadOnlyList<string> fields,
        int firstPunchIndex,
        DateTimeOffset? baseDate,
        DateTimeOffset? finish)
    {
        if (firstPunchIndex < 0)
            return ([], []);

        var codes = new List<string>();
        var times = new List<(TimeSpan? Time, DayOfWeek? Dow)>();
        for (var i = firstPunchIndex; i < fields.Count; i += 3)
        {
            var code = Field(fields, i).Trim();
            var (time, dow) = SplitTimeAndDow(Field(fields, i + 2));
            if (code.Length == 0 && time is null)
                continue;
            codes.Add(code);
            times.Add((time, dow));
        }

        var stamps = ReadoutTimeResolver.ResolvePunches(baseDate, times, finish);
        var punches = new ChipPunch[codes.Count];
        for (var i = 0; i < codes.Count; i++)
            punches[i] = new ChipPunch(codes[i], stamps[i]);
        return (codes, punches);
    }

    private readonly record struct ColumnLayout(int Chip, int Start, int Finish, int FirstPunch);

    private static int NextOrNone(int index) => index >= 0 ? index + 1 : -1;

    // Returns a base date whose weekday equals the finish's DOW (so ReadoutTimeResolver's weekday rule
    // lands times right), or the plain synthetic base when the finish has no weekday.
    private static DateTimeOffset AnchorBase(DayOfWeek? finishDow)
    {
        if (finishDow is not { } wd)
            return SyntheticBase;
        var shift = ((int)wd - (int)SyntheticBase.DayOfWeek + 7) % 7;
        return SyntheticBase.AddDays(shift);
    }

    // --- Time + weekday parsing ----------------------------------------------

    // Splits a Sport Time value like "11:17:16.187 (Вт)" into its time of day and (optional) weekday. The
    // weekday, when present, is a Ukrainian two-letter code in parentheses; a plain "11:13:54" yields no DOW.
    private static (TimeSpan? Time, DayOfWeek? Dow) SplitTimeAndDow(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return (null, null);

        DayOfWeek? dow = null;
        var paren = text.IndexOf('(');
        if (paren >= 0)
        {
            var close = text.IndexOf(')', paren + 1);
            var token = close > paren ? text[(paren + 1)..close] : text[(paren + 1)..];
            dow = ParseUkrainianDow(token);
            text = text[..paren].Trim();
        }

        return (ReadoutTimeResolver.ParseTimeOfDay(text), dow);
    }

    // Ukrainian weekday abbreviations Sport Time writes in parentheses. Case-insensitive; anything else →
    // null (routes through the monotonic fallback).
    private static DayOfWeek? ParseUkrainianDow(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "пн" => DayOfWeek.Monday,
        "вт" => DayOfWeek.Tuesday,
        "ср" => DayOfWeek.Wednesday,
        "чт" => DayOfWeek.Thursday,
        "пт" => DayOfWeek.Friday,
        "сб" => DayOfWeek.Saturday,
        "нд" or "нед" => DayOfWeek.Sunday,
        _ => null
    };

    // --- CSV helpers ---------------------------------------------------------

    private static string? FirstLine(string content)
    {
        var newline = content.IndexOf('\n');
        var line = (newline < 0 ? content : content[..newline]).TrimStart('﻿').TrimEnd('\r');
        return line.Length == 0 ? null : line;
    }

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
