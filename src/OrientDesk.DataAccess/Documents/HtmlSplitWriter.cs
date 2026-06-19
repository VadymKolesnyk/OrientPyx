using System.Globalization;
using System.Text;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.DataAccess.Documents;

/// <summary>
/// Renders a <see cref="SplitExportDocument"/> to a self-contained, modern UTF-8 HTML file. Two section
/// shapes, chosen per group by its <see cref="SplitsLayout"/>:
/// <list type="bullet">
///   <item><b>Ordered</b> (set course): a split table with one column per prescribed control (КП-N(code)).
///   Each runner is two rows — the cumulative time + per-control rank on top, the leg split below. Ranks,
///   the leader baseline and the fastest legs are computed across the OK (placed) runners only, so a
///   disqualified/MP/DNF run is never ranked or highlighted. The top 3 cumulatives and the top 3 legs at
///   each control are highlighted gold/silver/bronze; a cell's title shows the loss to the leader both by
///   overall (cumulative) time and on that single leg. A missed control in the middle of the run leaves only
///   its own column blank — the later controls still map onto their columns (the splits strategy matches the
///   prescribed course as a subsequence) — and a leg that would span a missed control carries no time.</item>
///   <item><b>Scored</b> (rogaine / free order): a table with one row per runner and КП-1…КП-N positional
///   columns (N = the longest passage in the group). Each runner writes their own visited control code into
///   the cell (own order/count), code over cumulative with the leg split + points below; a control that
///   scores nothing for the runner (a repeat / off-course punch) is greyed out, as if not on the course.</item>
/// </list>
/// The CSS is inlined so the file opens stand-alone. Lives in DataAccess as an output writer (alongside the
/// .docx / .xlsx writers); BusinessLogic only produces the values-only document.
/// </summary>
public sealed class HtmlSplitWriter : ISplitHtmlWriter
{
    public byte[] Write(SplitExportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var sb = new StringBuilder(64 * 1024);
        sb.Append("<!DOCTYPE html>\n<html lang=\"uk\">\n<head>\n");
        sb.Append("<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(Esc(document.Title)).Append("</title>\n");
        sb.Append("<style>\n").Append(Css).Append("</style>\n");
        sb.Append("</head>\n<body>\n");

        WriteHeader(sb, document);
        WriteNav(sb, document);

        for (var i = 0; i < document.Groups.Count; i++)
        {
            var group = document.Groups[i];
            sb.Append("<section class=\"group\" id=\"g").Append(i).Append("\">\n");
            WriteGroupHeader(sb, group, document.Labels);

            if (group.Layout == SplitsLayout.Ordered)
                WriteOrderedTable(sb, group, document.Labels);
            else
                WriteScoredTable(sb, group, document.Labels);

            sb.Append("</section>\n");
        }

        WriteFooter(sb, document);
        sb.Append("</body>\n</html>\n");

        // UTF-8 without a BOM — the <meta charset> already declares the encoding and a BOM is unnecessary.
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(sb.ToString());
    }

    // ── Header / nav / footer ────────────────────────────────────────────────────────────────────────

    private static void WriteHeader(StringBuilder sb, SplitExportDocument d)
    {
        sb.Append("<header class=\"doc-header\">\n");
        if (d.Subtitle.Length > 0)
            sb.Append("<p class=\"org\">").Append(Esc(d.Subtitle)).Append("</p>\n");
        sb.Append("<h1>").Append(Esc(d.Title)).Append("</h1>\n");
        if (d.CompetitionType.Length > 0)
            sb.Append("<p class=\"type\">").Append(Esc(d.CompetitionType)).Append("</p>\n");

        var meta = new List<string>();
        if (d.Venue.Length > 0) meta.Add(Esc(d.Venue));
        if (d.DateText.Length > 0) meta.Add(Esc(d.DateText));
        if (meta.Count > 0)
            sb.Append("<p class=\"meta\">").Append(string.Join(" &middot; ", meta)).Append("</p>\n");
        sb.Append("</header>\n");
    }

    private static void WriteNav(StringBuilder sb, SplitExportDocument d)
    {
        if (d.Groups.Count <= 1)
            return;
        sb.Append("<nav class=\"groups-nav\">\n");
        for (var i = 0; i < d.Groups.Count; i++)
            sb.Append("<a href=\"#g").Append(i).Append("\">").Append(Esc(d.Groups[i].Name)).Append("</a>\n");
        sb.Append("</nav>\n");
    }

    private static void WriteFooter(StringBuilder sb, SplitExportDocument d)
    {
        var stamp = DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        sb.Append("<footer class=\"doc-footer\">").Append(Esc(d.Labels.GeneratedLabel)).Append(' ')
          .Append(stamp).Append("</footer>\n");
    }

    private static void WriteGroupHeader(StringBuilder sb, SplitExportGroup g, SplitExportLabels labels)
    {
        sb.Append("<div class=\"group-header\"><h2>").Append(Esc(g.Name)).Append("</h2>");
        var meta = new List<string>();
        if (g.ControlCount is { } cc)
            meta.Add($"{cc} {Esc(labels.ControlCountLabel)}");
        if (g.DistanceKm is { } km)
            meta.Add($"{km.ToString("0.000", CultureInfo.InvariantCulture)} {Esc(labels.DistanceLabel)}");
        if (meta.Count > 0)
            sb.Append("<span class=\"group-meta\">").Append(string.Join(" &middot; ", meta)).Append("</span>");
        sb.Append("</div>\n");
    }

    // ── Set-course (ordered) split table ─────────────────────────────────────────────────────────────

    private static void WriteOrderedTable(StringBuilder sb, SplitExportGroup g, SplitExportLabels labels)
    {
        // Each runner's per-control cumulative + leg times, keyed by the control code in the order it was
        // taken on course. The table columns are the prescribed controls (g.Controls); a runner who missed a
        // control simply has no entry for it. The finish column uses the splits' finish marker.
        var runners = g.Rows.Select(r => RunnerSplits.From(r)).ToList();

        // Per-control fastest leg and leader cumulative, plus the cell ranks — computed across the OK
        // (placed) runners only, so a disqualified/MP/DNF run is never the leader, never a fastest leg, and
        // gets no rank or top-3 highlight. Non-OK runners still render their own times for reference.
        // The finish column is treated as one more "control" keyed by a sentinel.
        var columns = g.Controls.ToList();
        var ranked = runners.Where(r => r.Row.IsOk).ToList();
        var bestLeg = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        var leadCumulative = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in columns.Append(FinishKey))
        {
            foreach (var runner in ranked)
            {
                if (runner.Leg.TryGetValue(col, out var leg) && (!bestLeg.TryGetValue(col, out var bl) || leg < bl))
                    bestLeg[col] = leg;
                if (runner.Cumulative.TryGetValue(col, out var cum) && (!leadCumulative.TryGetValue(col, out var lc) || cum < lc))
                    leadCumulative[col] = cum;
            }

            // Per-control rank by ascending cumulative time across the OK runners who reached it (ties share a
            // rank), so each cell can show its "/N" position and the top 3 can be highlighted.
            RankInto(ranked, col, r => r.Cumulative, r => r.Rank);
            // Same ranking on the leg split alone, so the fastest legs (top 3) are highlighted too.
            RankInto(ranked, col, r => r.Leg, r => r.LegRank);
        }

        sb.Append("<div class=\"table-wrap\">\n<table class=\"splits ordered\">\n<thead>\n<tr>");
        sb.Append("<th class=\"col-place\">").Append(Esc(labels.ColumnPlace)).Append("</th>");
        sb.Append("<th class=\"col-name\">").Append(Esc(labels.ColumnName)).Append("</th>");
        sb.Append("<th class=\"col-num\">").Append(Esc(labels.ColumnNumber)).Append("</th>");
        sb.Append("<th class=\"col-result\">").Append(Esc(labels.ColumnResult)).Append("</th>");
        for (var i = 0; i < columns.Count; i++)
            sb.Append("<th class=\"col-cp\">").Append(Esc(labels.ControlPrefix)).Append('-').Append(i + 1)
              .Append("<span class=\"cp-code\">(").Append(Esc(columns[i])).Append(")</span></th>");
        sb.Append("<th class=\"col-cp\">").Append(Esc(labels.ColumnFinish)).Append("</th>");
        sb.Append("</tr>\n</thead>\n<tbody>\n");

        foreach (var runner in runners)
        {
            var rowClass = runner.Row.IsOk ? "runner" : "runner dnf";
            // First (top) row: place, name, number, result, then the cumulative time + per-control rank.
            sb.Append("<tr class=\"").Append(rowClass).Append("\">");
            sb.Append("<td class=\"place\" rowspan=\"2\">").Append(Esc(runner.Row.PlaceText)).Append("</td>");
            sb.Append("<td class=\"name\" rowspan=\"2\">").Append(Esc(runner.Row.FullName)).Append("</td>");
            sb.Append("<td class=\"num\" rowspan=\"2\">").Append(Esc(runner.Row.Number)).Append("</td>");
            sb.Append("<td class=\"result\" rowspan=\"2\">").Append(ResultCell(runner.Row)).Append("</td>");

            foreach (var col in columns.Append(FinishKey))
                WriteOrderedCumulativeCell(sb, runner, col, leadCumulative, bestLeg, labels);
            sb.Append("</tr>\n");

            // Second (bottom) row: the leg split under each control, fastest legs (top 3) highlighted.
            sb.Append("<tr class=\"").Append(rowClass).Append(" leg-row\">");
            foreach (var col in columns.Append(FinishKey))
                WriteOrderedLegCell(sb, runner, col, bestLeg, labels);
            sb.Append("</tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n");
    }

    // Ranks the runners who have a time for this column (ascending, ties shared) into the chosen rank
    // dictionary — used for both the cumulative rank and the leg-split rank.
    private static void RankInto(
        IReadOnlyList<RunnerSplits> runners, string col,
        Func<RunnerSplits, Dictionary<string, TimeSpan>> times,
        Func<RunnerSplits, Dictionary<string, int>> rank)
    {
        var reached = runners
            .Where(r => times(r).ContainsKey(col))
            .OrderBy(r => times(r)[col])
            .ToList();
        var place = 0;
        var seen = 0;
        TimeSpan? prev = null;
        foreach (var runner in reached)
        {
            seen++;
            var t = times(runner)[col];
            if (prev is null || prev.Value != t)
                place = seen;
            prev = t;
            rank(runner)[col] = place;
        }
    }

    private static void WriteOrderedCumulativeCell(
        StringBuilder sb, RunnerSplits runner, string col,
        IReadOnlyDictionary<string, TimeSpan> leadCumulative, IReadOnlyDictionary<string, TimeSpan> bestLeg,
        SplitExportLabels labels)
    {
        if (!runner.Cumulative.TryGetValue(col, out var cum))
        {
            sb.Append("<td class=\"cp-cell missing\">&mdash;</td>");
            return;
        }

        // Highlight the top 3 by cumulative time at this control (gold / silver / bronze), not just the leader.
        var rankClass = runner.Rank.TryGetValue(col, out var rk) ? PodiumClass(rk) : string.Empty;
        var cls = rankClass.Length > 0 ? "cp-cell " + rankClass : "cp-cell";

        // Tooltip with both losses: by overall (cumulative) time and by this leg — so the cell shows where
        // the time was lost on the whole run vs on this single leg. Each line is omitted when the runner
        // leads it (no loss).
        var lines = new List<string>(2);
        if (leadCumulative.TryGetValue(col, out var lc) && cum > lc)
            lines.Add(string.Format(labels.SplitLossTotal, FormatClock(cum - lc)));
        if (runner.Leg.TryGetValue(col, out var leg) && bestLeg.TryGetValue(col, out var bl) && leg > bl)
            lines.Add(string.Format(labels.SplitLossLeg, FormatClock(leg - bl)));
        var title = lines.Count > 0 ? $" title=\"{Esc(string.Join("\n", lines))}\"" : string.Empty;

        sb.Append("<td class=\"").Append(cls).Append('"').Append(title).Append('>');
        sb.Append("<span class=\"cum\">").Append(FormatClock(cum)).Append("</span>");
        if (runner.Rank.TryGetValue(col, out var rank))
            sb.Append("<span class=\"rank\">").Append(rank.ToString(CultureInfo.InvariantCulture)).Append("</span>");
        sb.Append("</td>");
    }

    private static void WriteOrderedLegCell(
        StringBuilder sb, RunnerSplits runner, string col, IReadOnlyDictionary<string, TimeSpan> bestLeg,
        SplitExportLabels labels)
    {
        if (!runner.Leg.TryGetValue(col, out var leg))
        {
            sb.Append("<td class=\"leg-cell\"></td>");
            return;
        }
        // Highlight the 3 fastest legs at this control (the leg ranking), not just the single best.
        var rankClass = runner.LegRank.TryGetValue(col, out var lr) ? PodiumClass(lr) : string.Empty;
        var title = bestLeg.TryGetValue(col, out var best) && leg > best
            ? $" title=\"{Esc(string.Format(labels.SplitLossLeg, FormatClock(leg - best)))}\""
            : string.Empty;
        sb.Append("<td class=\"leg-cell").Append(rankClass.Length > 0 ? " " + rankClass : "")
          .Append('"').Append(title).Append('>')
          .Append(FormatClock(leg)).Append("</td>");
    }

    // The podium CSS class for a 1/2/3 rank (gold/silver/bronze), empty for 4th and below.
    private static string PodiumClass(int rank) => rank switch
    {
        1 => "p1",
        2 => "p2",
        3 => "p3",
        _ => string.Empty
    };

    // ── Scored (rogaine / free order) split table ────────────────────────────────────────────────────

    // Unlike a set course, every runner visits their own controls in their own order, so there is no shared
    // КП column header. Instead the table has КП-1…КП-N positional columns (N = the longest passage in the
    // group) and each runner's actual control code is written inside the cell, with the leg split below and
    // (for a scored format) the points; the cumulative shows on the code line. Runners with fewer controls
    // simply leave the trailing positional columns blank.
    private static void WriteScoredTable(StringBuilder sb, SplitExportGroup g, SplitExportLabels labels)
    {
        // Each runner's actual passage (controls in punch order) + their finish marker.
        var runners = g.Rows.Select(r => (Row: r, Passage: PassageControls(r.Splits))).ToList();
        var maxControls = runners.Count == 0 ? 0 : runners.Max(r => r.Passage.Count);

        sb.Append("<div class=\"table-wrap\">\n<table class=\"splits scored\">\n<thead>\n<tr>");
        sb.Append("<th class=\"col-place\">").Append(Esc(labels.ColumnPlace)).Append("</th>");
        sb.Append("<th class=\"col-name\">").Append(Esc(labels.ColumnName)).Append("</th>");
        sb.Append("<th class=\"col-num\">").Append(Esc(labels.ColumnNumber)).Append("</th>");
        sb.Append("<th class=\"col-result\">").Append(Esc(g.HasPoints ? labels.ColumnScore : labels.ColumnResult)).Append("</th>");
        sb.Append("<th class=\"col-dist\">").Append(Esc(labels.ColumnDistance)).Append("</th>");
        for (var i = 0; i < maxControls; i++)
            sb.Append("<th class=\"col-cp\">").Append(Esc(labels.ControlPrefix)).Append('-').Append(i + 1).Append("</th>");
        sb.Append("<th class=\"col-cp\">").Append(Esc(labels.ColumnFinish)).Append("</th>");
        sb.Append("</tr>\n</thead>\n<tbody>\n");

        foreach (var (row, passage) in runners)
        {
            var rowClass = row.IsOk ? "runner" : "runner dnf";

            // Top row: place, name, number, result, then each control's code + cumulative (Σ) + running pts.
            sb.Append("<tr class=\"").Append(rowClass).Append("\">");
            sb.Append("<td class=\"place\" rowspan=\"2\">").Append(Esc(row.PlaceText)).Append("</td>");
            sb.Append("<td class=\"name\" rowspan=\"2\">").Append(Esc(row.FullName));
            if (row.Team.Length > 0)
                sb.Append("<span class=\"sub-team\">").Append(Esc(row.Team)).Append("</span>");
            sb.Append("</td>");
            sb.Append("<td class=\"num\" rowspan=\"2\">").Append(Esc(row.Number)).Append("</td>");
            WriteScoredResultCell(sb, row);

            // Distance the runner actually covered by chip: the sum of every leg's straight-line length
            // (start → first punch → … → finish), in the order they were punched. Blank when no control has
            // coordinates (nothing to measure).
            var distance = ChipDistanceKm(row.Splits);
            sb.Append("<td class=\"dist\" rowspan=\"2\">")
              .Append(distance is { } km ? km.ToString("0.00", CultureInfo.InvariantCulture) : "")
              .Append("</td>");

            // What counts as "scored" for the highlight: in a rogaine team the controls that scored for the
            // TEAM (every member punched them — the ones flagged CountsForTeam), not the ones the runner took
            // personally. A teamless runner (поза конкурсом) has no team context, so fall back to their own
            // scoring punches.
            var teamContext = passage.Any(p => p.CountsForTeam);

            for (var i = 0; i < maxControls; i++)
                WriteScoredTopCell(sb, i < passage.Count ? passage[i] : null, teamContext);
            WriteScoredTopCell(sb, FinishPunch(row.Splits), teamContext);
            sb.Append("</tr>\n");

            // Bottom row: the leg split under each control (and the finish leg / penalty).
            sb.Append("<tr class=\"").Append(rowClass).Append(" leg-row\">");
            for (var i = 0; i < maxControls; i++)
                WriteScoredLegCell(sb, i < passage.Count ? passage[i] : null, g.HasPoints, teamContext);
            WriteScoredLegCell(sb, FinishPunch(row.Splits), g.HasPoints, teamContext);
            sb.Append("</tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n");
    }

    // Top cell of a scored runner: the control code over the cumulative time. A control that counts toward
    // the result (team-scoring, or — when there is no team — the runner's own scoring punch) is highlighted
    // blue; everything else (a repeat / off-course / personally-but-not-team punch) is greyed out as if not
    // part of the course. An empty position renders blank.
    private static void WriteScoredTopCell(StringBuilder sb, PassagePunch? p, bool teamContext)
    {
        if (p is null)
        {
            sb.Append("<td class=\"cp-cell empty\"></td>");
            return;
        }
        var isFinish = p.Kind == PassageKind.Finish;
        var counts = Counts(p, teamContext);
        var cls = isFinish ? "cp-cell fin" : (counts ? "cp-cell scoring" : "cp-cell unscored");
        var code = isFinish ? "&#9873;" : Esc(p.Code); // a flag glyph for the finish column
        sb.Append("<td class=\"").Append(cls).Append("\">");
        sb.Append("<span class=\"code\">").Append(code).Append("</span>");
        if (p.Elapsed is { } e)
            sb.Append("<span class=\"cum\">").Append(FormatClock(e)).Append("</span>");
        sb.Append("</td>");
    }

    // Bottom cell of a scored runner: the leg split, plus the punch's points (or the finish penalty) when scored.
    private static void WriteScoredLegCell(StringBuilder sb, PassagePunch? p, bool hasPoints, bool teamContext)
    {
        if (p is null)
        {
            sb.Append("<td class=\"leg-cell empty\"></td>");
            return;
        }
        // Match the top cell: a counting control is highlighted, a non-counting punch (not the finish) is
        // greyed out, so a runner's whole КП column (code+cumulative over leg) reads as one block.
        var isControl = p.Kind == PassageKind.Control;
        var counts = isControl && Counts(p, teamContext);
        var extra = !isControl ? "" : counts ? " scoring" : " unscored";
        sb.Append("<td class=\"leg-cell").Append(extra).Append("\">");
        if (p.Leg is { } l)
            sb.Append("<span class=\"leg\">").Append(FormatClock(l)).Append("</span>");
        if (hasPoints && p.Points is { } pt)
        {
            // A negative points value (the finish over-time penalty) reads in red; gained points stay green.
            var ptsClass = pt < 0 ? "pts neg" : "pts";
            sb.Append("<span class=\"").Append(ptsClass).Append("\">")
              .Append(pt > 0 ? "+" : "").Append(pt.ToString(CultureInfo.InvariantCulture)).Append("</span>");
        }
        sb.Append("</td>");
    }

    // Whether a control punch counts for the highlight: in a team context only the team-scoring controls
    // (CountsForTeam — the ones every member punched, previously shown with a ★); for a runner with no team
    // context, their own scoring punches (OnCourse). A repeat / off-course punch never counts.
    private static bool Counts(PassagePunch p, bool teamContext) =>
        teamContext ? p.CountsForTeam : p.OnCourse;

    // A scored runner's actual passage as the ordered control punches (start/finish markers excluded).
    private static IReadOnlyList<PassagePunch> PassageControls(SplitsView splits) =>
        splits.Passage.Where(p => p.Kind == PassageKind.Control).ToList();

    // Total distance the runner ran by chip: every leg's straight-line length summed over the whole passage
    // (the start marker carries no leg; each control and the finish carry the leg from the previous point).
    // Null when no leg has a measured distance (no control coordinates), so the cell stays blank.
    private static decimal? ChipDistanceKm(SplitsView splits)
    {
        decimal sum = 0;
        var any = false;
        foreach (var p in splits.Passage)
            if (p.LegKm is { } km)
            {
                sum += km;
                any = true;
            }
        return any ? sum : null;
    }

    // The finish marker of a passage (carries the finish leg/elapsed and, for rogaine, the over-time penalty).
    private static PassagePunch? FinishPunch(SplitsView splits) =>
        splits.Passage.LastOrDefault(p => p.Kind == PassageKind.Finish);

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    // The scored (rogaine) result cell: the «Бали» total, with the penalty/bonus detail spelled out under it
    // and the full per-control breakdown carried as the cell title (the same tooltip the participant tables
    // show). For a non-OK status it falls back to the status badge.
    private static void WriteScoredResultCell(StringBuilder sb, SplitExportRow row)
    {
        var title = row.ResultTooltip.Length > 0
            ? $" title=\"{Esc(row.ResultTooltip)}\""
            : string.Empty;
        sb.Append("<td class=\"result\" rowspan=\"2\"").Append(title).Append('>');
        sb.Append(ResultCell(row));
        if (row.IsOk && row.ResultDetail.Length > 0)
            sb.Append("<span class=\"result-detail\">").Append(Esc(row.ResultDetail)).Append("</span>");
        sb.Append("</td>");
    }

    // The result column markup: a status badge for a problem result, else the plain result text.
    private static string ResultCell(SplitExportRow row) =>
        row.IsOk
            ? Esc(row.ResultText)
            : $"<span class=\"status\">{Esc(row.StatusText)}</span>";

    // mm:ss for under an hour, else h:mm:ss — matching the on-screen split convention.
    private static string FormatClock(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
            t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    // Sentinel column key for the finish "control", so it can share the cumulative/leg/rank dictionaries.
    private const string FinishKey = " F";

    private const string Css = """
:root {
  --bg: #f5f6f8;
  --card: #ffffff;
  --ink: #1d2433;
  --muted: #6b7280;
  --line: #e2e5ea;
  --accent: #1f6feb;
  /* Podium tints for the top-3 split times: gold / silver / bronze, each with a slightly darker border. */
  --p1: #fff4d6; --p1-border: #f2c14e;
  --p2: #eef0f3; --p2-border: #c2c8d2;
  --p3: #fbe7d6; --p3-border: #e0a878;
  --dnf: #b42318;
}
* { box-sizing: border-box; }
html { -webkit-text-size-adjust: 100%; }
body {
  margin: 0;
  background: var(--bg);
  color: var(--ink);
  font-family: -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
  font-size: 13px;
  line-height: 1.4;
}
.doc-header { text-align: center; padding: 24px 16px 12px; border-bottom: 1px solid var(--line); background: var(--card); }
.doc-header .org { margin: 0 0 4px; color: var(--muted); font-size: 13px; }
.doc-header h1 { margin: 4px 0; font-size: 19px; font-weight: 700; }
.doc-header .type { margin: 4px auto; max-width: 880px; font-size: 13px; }
.doc-header .meta { margin: 6px 0 0; color: var(--muted); font-size: 13px; }
.groups-nav {
  position: sticky; top: 0; z-index: 5;
  display: flex; flex-wrap: wrap; gap: 6px;
  padding: 10px 16px;
  background: rgba(255,255,255,.92);
  backdrop-filter: blur(6px);
  border-bottom: 1px solid var(--line);
}
.groups-nav a {
  text-decoration: none; color: var(--accent);
  border: 1px solid var(--line); border-radius: 999px;
  padding: 2px 10px; font-size: 12px; background: var(--card);
}
.groups-nav a:hover { background: var(--accent); color: #fff; border-color: var(--accent); }
.group { padding: 18px 16px 4px; width: fit-content; }
.group-header { display: flex; align-items: baseline; gap: 12px; margin-bottom: 8px; }
.group-header h2 { margin: 0; font-size: 16px; font-weight: 700; color: var(--accent); }
.group-meta { color: var(--muted); font-size: 12px; }
.table-wrap { overflow-x: auto; background: var(--card); border: 1px solid var(--line); border-radius: 10px; }
table.splits { border-collapse: collapse; width: 100%; font-variant-numeric: tabular-nums; }
table.splits th, table.splits td { padding: 4px 8px; text-align: center; white-space: nowrap; }
table.splits thead th {
  position: sticky; top: 0; background: #eef1f6; color: var(--ink);
  font-weight: 600; font-size: 11px; border-bottom: 2px solid var(--line);
}
/* Shared structure for both the ordered (set-course) and scored (rogaine) split tables. */
table.splits .col-name, table.splits td.name { text-align: left; }
table.splits td.name { font-weight: 600; }
table.splits tbody tr.runner > td { border-top: 1px solid var(--line); }
table.splits tbody tr.leg-row > td { border-top: 0; }
table.splits td.place { font-weight: 700; color: var(--accent); }
table.splits td.result { font-weight: 600; }
/* The penalty/bonus detail under a scored result total (e.g. "−3 +2"), in a smaller muted line. */
table.splits td.result .result-detail { display: block; font-weight: 400; color: var(--muted); font-size: 10px; }
table.splits tr.dnf td.name { color: var(--dnf); }
table.splits td.name .sub-team { display: block; font-weight: 400; color: var(--muted); font-size: 10px; }
/* Ordered (set course): cumulative + per-control rank, top-3 (gold/silver/bronze) highlights. */
table.ordered .cp-code { display: block; font-weight: 400; color: var(--muted); font-size: 10px; }
table.ordered td.cp-cell .cum { font-weight: 600; }
table.ordered td.cp-cell .rank { display: inline-block; margin-left: 3px; color: var(--muted); font-size: 10px; }
table.ordered td.cp-cell.missing { color: var(--muted); }
table.ordered td.leg-cell { color: var(--muted); font-size: 11px; padding-top: 0; }
/* Top-3 podium tint on the cumulative cell (1st = gold, 2nd = silver, 3rd = bronze); the matching leg
   cell tints a touch lighter so the two rows of a fast control read as one block. */
table.ordered td.cp-cell.p1 { background: var(--p1); box-shadow: inset 0 0 0 1px var(--p1-border); }
table.ordered td.cp-cell.p2 { background: var(--p2); box-shadow: inset 0 0 0 1px var(--p2-border); }
table.ordered td.cp-cell.p3 { background: var(--p3); box-shadow: inset 0 0 0 1px var(--p3-border); }
table.ordered td.cp-cell.p1 .rank { color: #7a5b00; }
table.ordered td.leg-cell.p1 { background: var(--p1); color: #6b4f00; font-weight: 600; }
table.ordered td.leg-cell.p2 { background: var(--p2); color: #4a4f57; font-weight: 600; }
table.ordered td.leg-cell.p3 { background: var(--p3); color: #6b3f1a; font-weight: 600; }
/* Scored (rogaine): the control code lives in the cell (own order per runner), code over cumulative, leg
   + points below. A control that scores nothing for this runner (a repeat / off-course punch) is greyed
   out, as if it isn't part of the course; scoring punches read in normal ink. */
table.scored td.dist { font-weight: 600; color: var(--ink); }
table.scored td.cp-cell .code { display: block; font-weight: 700; }
table.scored td.cp-cell .cum { display: block; color: var(--muted); font-size: 10px; }
/* A scoring (counted) control is highlighted blue across both its rows (code+cumulative over the leg);
   the finish keeps its neutral tint. */
table.scored td.scoring { background: #e3edff; }
table.scored td.cp-cell.scoring { box-shadow: inset 1px 0 0 #bcd4ff, inset -1px 0 0 #bcd4ff, inset 0 1px 0 #bcd4ff; }
table.scored td.leg-cell.scoring { box-shadow: inset 1px 0 0 #bcd4ff, inset -1px 0 0 #bcd4ff, inset 0 -1px 0 #bcd4ff; }
table.scored td.cp-cell.fin { background: #f3f5f8; }
table.scored td.leg-cell { font-size: 11px; padding-top: 0; }
table.scored td.leg-cell .leg { color: var(--muted); }
table.scored td.leg-cell .pts { margin-left: 4px; font-weight: 700; color: #11622a; }
/* A negative points value — the finish over-time penalty — reads in red. */
table.scored td.leg-cell .pts.neg { color: var(--dnf); }
/* A non-scoring punch (repeat / off-course): greyed in both rows, with a lighter code, as if not on the course. */
table.scored td.unscored, table.scored td.unscored .code, table.scored td.unscored .cum, table.scored td.unscored .leg { color: #aab0bb; }
table.scored td.cp-cell.unscored .code { font-weight: 400; }
.status { display: inline-block; padding: 0 6px; border-radius: 4px; background: #fde8e6; color: var(--dnf); font-weight: 700; font-size: 11px; }
.doc-footer { padding: 16px; text-align: center; color: var(--muted); font-size: 11px; }
@media print {
  body { background: #fff; font-size: 11px; }
  .groups-nav { display: none; }
  .table-wrap { border-color: #ccc; }
  table.splits tbody tr.runner { page-break-inside: avoid; }
}
""";

    /// <summary>
    /// One runner's set-course splits reduced to per-control cumulative time, leg split and rank, keyed by
    /// control code (the finish under <see cref="FinishKey"/>). Built from the on-course punches of the
    /// runner's <see cref="SplitsView.Passage"/>; a missed/off-course control simply has no entry.
    /// </summary>
    private sealed class RunnerSplits
    {
        public required SplitExportRow Row { get; init; }
        public Dictionary<string, TimeSpan> Cumulative { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, TimeSpan> Leg { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Rank { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> LegRank { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static RunnerSplits From(SplitExportRow row)
        {
            var rs = new RunnerSplits { Row = row };
            foreach (var p in row.Splits.Passage)
            {
                if (p.Kind == PassageKind.Finish)
                {
                    if (p.Elapsed is { } fe) rs.Cumulative[FinishKey] = fe;
                    if (p.Leg is { } fl) rs.Leg[FinishKey] = fl;
                    continue;
                }
                if (p.Kind != PassageKind.Control || !p.OnCourse)
                    continue; // only on-course punches map onto the prescribed columns
                var code = p.Code.Trim();
                if (code.Length == 0)
                    continue;
                if (p.Elapsed is { } e) rs.Cumulative[code] = e;
                if (p.Leg is { } l) rs.Leg[code] = l;
            }
            return rs;
        }
    }
}
