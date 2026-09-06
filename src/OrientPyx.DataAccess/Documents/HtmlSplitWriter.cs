using System.Globalization;
using System.Text;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.DataAccess.Documents;

/// <summary>
/// Renders a <see cref="SplitExportDocument"/> to a self-contained, modern UTF-8 HTML file. Two section
/// shapes, chosen per group by its <see cref="SplitsLayout"/>:
/// <list type="bullet">
///   <item><b>Ordered</b> (set course): a split table with one column per prescribed control (КП-N(code)).
///   Each runner is two rows — the cumulative time + its overall place on top, the leg split + its place on
///   that leg below. A non-OK run (MP/DNF/DSQ) is not excluded wholesale — the part of it that was really
///   run counts: <b>every</b> leg split takes part in the leg ranking (a leg time only exists between two
///   consecutive on-course controls, so it is genuine however the run ended), while the cumulative ranking
///   and the leader baseline take a runner only up to the control before their first missed one — past that
///   their elapsed time covers a shorter course, so those cells are printed but neither ranked, highlighted
///   nor given a gap. The top 3 cumulatives and
///   the top 3 legs at each control are highlighted gold/silver/bronze. The two cells of one control are a
///   single clickable block: hovering shows the loss to the leader overall and on that leg, and clicking
///   (tapping) either cell outlines the block and opens a &lt;dialog&gt; spelling both out — time, place and
///   gap — for the overall run and for the leg. A missed control in the middle of the run leaves only
///   its own column blank — the later controls still map onto their columns (the splits strategy matches the
///   prescribed course as a subsequence) — and a leg that would span a missed control carries no time.</item>
///   <item><b>Scored</b> (rogaine / free order): a table with one row per runner and КП-1…КП-N positional
///   columns (N = the longest passage in the group). Each runner writes their own visited control code into
///   the cell (own order/count), code over cumulative with the leg split + points below; a control that
///   scores nothing for the runner (a repeat / off-course punch) is greyed out, as if not on the course.</item>
/// </list>
/// The CSS and the small detail-panel script are inlined so the file opens stand-alone, offline. The layout
/// is responsive: on a phone the group sections go full-bleed with the identity columns pinned to the left
/// while the КП columns scroll sideways. Every size is expressed in rem off a single root font-size, and a
/// two-finger pinch drives that one value from the script instead of using the browser's own zoom — so
/// pulling back genuinely fits more controls on screen while the layout stays a layout (the sticky column
/// keeps working, the dialog stays a centred card, the page never grows wider than the screen). The
/// per-cell payload is
/// deliberately lean — the figures ride on the top cell of each block only, and the hover tooltips are
/// attached by the script on first hover — because on a full competition (hundreds of runners × a dozen
/// controls) repeated attributes, not the visible text, are what make the page heavy to load and scroll.
/// Lives in DataAccess as an output writer (alongside the .docx / .xlsx writers); BusinessLogic only
/// produces the values-only document.
/// </summary>
public sealed class HtmlSplitWriter : ISplitHtmlWriter
{
    public byte[] Write(SplitExportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var sb = new StringBuilder(64 * 1024);
        sb.Append("<!DOCTYPE html>\n<html lang=\"uk\">\n<head>\n");
        sb.Append("<meta charset=\"utf-8\">\n");
        // user-scalable=no because the pinch gesture is handled in the page itself: it resizes the
        // text (the root font-size) instead of zooming the pixels, which keeps the layout — sticky
        // columns, a centred dialog, no page wider than the screen — intact while still fitting more
        // on screen.
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1, ")
          .Append("maximum-scale=1, user-scalable=no\">\n");
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
        WriteDetailPanel(sb, document.Labels);
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

    // The click-open detail panel, as a native <dialog>: the browser gives it the backdrop, the top layer
    // (so no table cell can ever paint over it), Esc-to-close and focus handling for free — which is both
    // less code and far cheaper than a hand-rolled overlay. One element is reused by every cell and filled
    // from the tapped cell's data-*. The captions ride along as data-* on the dialog so the script stays
    // localization-free. Sizing is the CSS's job: a small centred card on a wide screen, full-screen on a
    // phone (see the media query).
    private static void WriteDetailPanel(StringBuilder sb, SplitExportLabels labels)
    {
        sb.Append("<dialog id=\"cp-detail\"")
          .Append(" data-l-total=\"").Append(Esc(labels.SplitDetailTotal)).Append('"')
          .Append(" data-l-leg=\"").Append(Esc(labels.SplitDetailLeg)).Append('"')
          .Append(" data-l-time=\"").Append(Esc(labels.SplitDetailTime)).Append('"')
          .Append(" data-l-place=\"").Append(Esc(labels.SplitDetailPlace)).Append('"')
          .Append(" data-l-gap=\"").Append(Esc(labels.SplitDetailGap)).Append('"')
          .Append(" data-l-leader=\"").Append(Esc(labels.SplitDetailLeader)).Append('"')
          .Append(" data-l-title=\"").Append(Esc(labels.SplitDetailTitle)).Append('"')
          .Append(" data-l-losstotal=\"").Append(Esc(labels.SplitLossTotal)).Append('"')
          .Append(" data-l-lossleg=\"").Append(Esc(labels.SplitLossLeg)).Append('"')
          .Append(">\n");
        sb.Append("<div class=\"cp-head\">");
        sb.Append("<h3 id=\"cp-title\"></h3>");
        sb.Append("<button type=\"button\" class=\"cp-close\" title=\"").Append(Esc(labels.SplitDetailClose))
          .Append("\" aria-label=\"").Append(Esc(labels.SplitDetailClose)).Append("\">&times;</button>");
        sb.Append("</div>");
        sb.Append("<div id=\"cp-body\"></div>");
        sb.Append("\n</dialog>\n");
        sb.Append("<script>\n").Append(Script).Append("</script>\n");
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
        // Every column is one position on the prescribed course — a course that visits the same control twice
        // gets two columns for that code, and each keeps its own time.
        var columns = g.Controls.ToList();
        var finishColumn = columns.Count;
        var runners = g.Rows.Select(r => RunnerSplits.From(r, columns, finishColumn)).ToList();

        // Per-control fastest leg and leader cumulative, plus the cell ranks. A non-OK run (MP/DNF/DSQ) is
        // not simply dropped: the part of it that was actually run clean is real and competes.
        //   • Leg splits: every runner counts, always — a leg time exists only for a contiguous on-course
        //     pair of controls, so it is a genuine leg however the run ended.
        //   • Cumulative times: a runner counts up to their first missed control (see
        //     <see cref="RunnerSplits.ValidCumulativeThrough"/>); past that point their elapsed time is no
        //     longer comparable (they skipped course), so it is rendered but neither ranked nor highlighted.
        // The finish is treated as one more "control", the column past the last one.
        var bestLeg = new Dictionary<int, TimeSpan>();
        var leadCumulative = new Dictionary<int, TimeSpan>();
        foreach (var col in Enumerable.Range(0, columns.Count + 1))
        {
            // Cumulative: only the runners whose run is still valid at this column.
            var cumRanked = runners.Where(r => r.CumulativeCountsAt(col)).ToList();
            foreach (var runner in runners)
            {
                if (runner.Leg.TryGetValue(col, out var leg) && (!bestLeg.TryGetValue(col, out var bl) || leg < bl))
                    bestLeg[col] = leg;
                if (runner.CumulativeCountsAt(col)
                    && runner.Cumulative.TryGetValue(col, out var cum)
                    && (!leadCumulative.TryGetValue(col, out var lc) || cum < lc))
                    leadCumulative[col] = cum;
            }

            // Per-control rank by ascending cumulative time across the runners still valid here (ties share a
            // rank), so each cell can show its "/N" position and the top 3 can be highlighted.
            RankInto(cumRanked, col, r => r.Cumulative, r => r.Rank);
            // Same ranking on the leg split alone, across every runner, so the fastest legs (top 3) are
            // highlighted even when they were run by someone who was later disqualified.
            RankInto(runners, col, r => r.Leg, r => r.LegRank);
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

            for (var c = 0; c < columns.Count; c++)
                WriteOrderedCumulativeCell(sb, runner, c, leadCumulative, bestLeg);
            WriteOrderedCumulativeCell(sb, runner, finishColumn, leadCumulative, bestLeg);
            sb.Append("</tr>\n");

            // Second (bottom) row: the leg split with its own leg place, fastest legs (top 3) highlighted.
            sb.Append("<tr class=\"").Append(rowClass).Append(" leg-row\">");
            for (var c = 0; c < columns.Count; c++)
                WriteOrderedLegCell(sb, runner, c);
            WriteOrderedLegCell(sb, runner, finishColumn);
            sb.Append("</tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</div>\n");
    }

    // Ranks the runners who have a time for this column (ascending, ties shared) into the chosen rank
    // dictionary — used for both the cumulative rank and the leg-split rank.
    private static void RankInto(
        IReadOnlyList<RunnerSplits> runners, int col,
        Func<RunnerSplits, Dictionary<int, TimeSpan>> times,
        Func<RunnerSplits, Dictionary<int, int>> rank)
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

    // The figures a tapped control shows, emitted once — on the control's top (cumulative) cell only. The
    // leg cell below carries just the shared block key and the script reads the numbers off its partner, so
    // nothing is repeated: on a full competition the duplicated attributes were most of the file's weight.
    // The runner's name and the control's caption are not repeated either — the script takes them from the
    // row's name cell and the column's header.
    private static string DetailData(
        RunnerSplits runner, int col,
        IReadOnlyDictionary<int, TimeSpan> leadCumulative, IReadOnlyDictionary<int, TimeSpan> bestLeg)
    {
        var sb = new StringBuilder(96);

        if (runner.Cumulative.TryGetValue(col, out var cum))
        {
            sb.Append(" data-t=\"").Append(FormatClock(cum)).Append('"');
            if (runner.Rank.TryGetValue(col, out var rank))
                sb.Append(" data-tp=\"").Append(rank.ToString(CultureInfo.InvariantCulture)).Append('"');
            // The gap only makes sense while the runner is still on the whole course — past a missed control
            // their elapsed time covers less ground than the leader's, so no gap is offered.
            if (runner.CumulativeCountsAt(col) && leadCumulative.TryGetValue(col, out var lc))
                sb.Append(" data-tg=\"").Append(FormatClock(cum - lc)).Append('"');
        }

        if (runner.Leg.TryGetValue(col, out var leg))
        {
            sb.Append(" data-l=\"").Append(FormatClock(leg)).Append('"');
            if (runner.LegRank.TryGetValue(col, out var lr))
                sb.Append(" data-lp=\"").Append(lr.ToString(CultureInfo.InvariantCulture)).Append('"');
            if (bestLeg.TryGetValue(col, out var bl))
                sb.Append(" data-lg=\"").Append(FormatClock(leg - bl)).Append('"');
        }

        return sb.ToString();
    }

    private static void WriteOrderedCumulativeCell(
        StringBuilder sb, RunnerSplits runner, int col,
        IReadOnlyDictionary<int, TimeSpan> leadCumulative, IReadOnlyDictionary<int, TimeSpan> bestLeg)
    {
        if (!runner.Cumulative.TryGetValue(col, out var cum))
        {
            sb.Append("<td class=\"cp-cell missing\">&mdash;</td>");
            return;
        }

        // Highlight the top 3 by cumulative time at this control (gold / silver / bronze), not just the leader.
        var rankClass = runner.Rank.TryGetValue(col, out var rk) ? PodiumClass(rk) : string.Empty;
        var cls = rankClass.Length > 0 ? "cp-cell detail " + rankClass : "cp-cell detail";

        // The hover tooltip (loss to the leader overall + on this leg) is attached lazily by the script on
        // first hover, from the data-* below — baking a title= into every cell was a tenth of the file on a
        // full competition, for text almost none of which is ever read.
        sb.Append("<td class=\"").Append(cls).Append('"')
          .Append(" data-c=\"").Append(col).Append('"')
          .Append(DetailData(runner, col, leadCumulative, bestLeg)).Append('>');
        sb.Append("<span class=\"cum\">").Append(FormatClock(cum)).Append("</span>");
        if (runner.Rank.TryGetValue(col, out var rank))
            sb.Append("<span class=\"rank\">").Append(rank.ToString(CultureInfo.InvariantCulture)).Append("</span>");
        sb.Append("</td>");
    }

    private static void WriteOrderedLegCell(StringBuilder sb, RunnerSplits runner, int col)
    {
        if (!runner.Leg.TryGetValue(col, out var leg))
        {
            sb.Append("<td class=\"leg-cell\"></td>");
            return;
        }
        // Highlight the 3 fastest legs at this control (the leg ranking), not just the single best.
        var rankClass = runner.LegRank.TryGetValue(col, out var lr) ? PodiumClass(lr) : string.Empty;
        sb.Append("<td class=\"leg-cell detail").Append(rankClass.Length > 0 ? " " + rankClass : "")
          .Append("\" data-c=\"").Append(col).Append("\">")
          .Append("<span class=\"leg\">").Append(FormatClock(leg)).Append("</span>");
        // The leg place next to the leg time, mirroring the cumulative row's rank badge — so the place on
        // this single leg reads off the cell instead of only from the top-3 tint.
        if (runner.LegRank.TryGetValue(col, out var legRank))
            sb.Append("<span class=\"rank\">").Append(legRank.ToString(CultureInfo.InvariantCulture)).Append("</span>");
        sb.Append("</td>");
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
/* No overflow clipping on the root, and no user-scalable=no in the viewport meta: pinch-zoom must keep
   working, since zooming out is how you take in a wide split table on a phone. Nothing is allowed to make
   the document wider than the screen in the first place — the one wide element is the split table, and it
   scrolls inside its own .table-wrap. */
/* The root font-size is the page's single zoom lever: every font-size below is in rem, so setting this
   one property scales all the text at once. The pinch handler (see the script) writes it directly, which
   is why the page does not need — and does not use — the browser's own zoom. */
html {
  -webkit-text-size-adjust: 100%;
  font-size: 16px;
  /* Every length that should scale with the pinch is in em/rem off this one value — padding included,
     since px padding would keep the cells big while the text shrank, which both looks wrong and stops
     the table from actually getting narrower. */
}
/* The page owns the pinch (it resizes the text), so the browser must not claim a two-finger
   gesture as its own zoom. One-finger panning stays with the browser. */
body { touch-action: pan-x pan-y; }
body {
  margin: 0;
  background: var(--bg);
  color: var(--ink);
  font-family: -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
  font-size: 0.8125rem;
  line-height: 1.4;
}
.doc-header { text-align: center; padding: 24px 16px 12px; border-bottom: 1px solid var(--line); background: var(--card); }
.doc-header .org { margin: 0 0 4px; color: var(--muted); font-size: 0.8125rem; }
.doc-header h1 { margin: 4px 0; font-size: 1.1875rem; font-weight: 700; }
.doc-header .type { margin: 4px auto; max-width: 880px; font-size: 0.8125rem; }
.doc-header .meta { margin: 6px 0 0; color: var(--muted); font-size: 0.8125rem; }
.groups-nav {
  position: sticky; top: 0; z-index: 5;
  display: flex; flex-wrap: wrap; gap: 6px;
  padding: 10px 16px;
  background: var(--card);
  border-bottom: 1px solid var(--line);
}
.groups-nav a {
  text-decoration: none; color: var(--accent);
  border: 1px solid var(--line); border-radius: 999px;
  padding: 2px 10px; font-size: 0.75rem; background: var(--card);
}
.groups-nav a:hover { background: var(--accent); color: #fff; border-color: var(--accent); }
.group { padding: 18px 16px 4px; }
.group-header { display: flex; align-items: baseline; gap: 12px; margin-bottom: 8px; }
.group-header h2 { margin: 0; font-size: 1rem; font-weight: 700; color: var(--accent); }
.group-meta { color: var(--muted); font-size: 0.75rem; }
/* The scroller. width: fit-content keeps the card hugging a narrow table (a group with few controls
   should not stretch a card across a wide monitor), capped at the available width so a wide one scrolls. */
.table-wrap {
  overflow-x: auto; width: fit-content; max-width: 100%;
  background: var(--card); border: 1px solid var(--line); border-radius: 10px;
}
/* Content-sized, never container-sized: the table is as wide as its columns need and the wrapper scrolls.
   A percentage width here would stretch the columns to fill whatever box the table sits in — which is
   exactly what made the phone layout balloon when the group section went full-bleed. */
table.splits { border-collapse: collapse; width: auto; font-variant-numeric: tabular-nums; }
table.splits th, table.splits td { padding: 0.3em 0.6em; text-align: center; white-space: nowrap; }
table.splits thead th {
  position: sticky; top: 0; background: #eef1f6; color: var(--ink);
  font-weight: 600; font-size: 0.6875rem; border-bottom: 2px solid var(--line);
}
/* Skip layout and paint for the group sections that are far off-screen — the single biggest win on a
   competition with dozens of groups. contain-intrinsic-size keeps the scrollbar stable by remembering
   each section's last measured height, so scrolling does not jump. */
.group { content-visibility: auto; contain-intrinsic-size: auto 700px; }
/* Shared structure for both the ordered (set-course) and scored (rogaine) split tables. */
table.splits .col-name, table.splits td.name { text-align: left; }
table.splits td.name { font-weight: 600; }
table.splits tbody tr.runner > td { border-top: 1px solid var(--line); }
table.splits tbody tr.leg-row > td { border-top: 0; }
table.splits td.place { font-weight: 700; color: var(--accent); }
table.splits td.result { font-weight: 600; }
/* The penalty/bonus detail under a scored result total (e.g. "−3 +2"), in a smaller muted line. */
table.splits td.result .result-detail { display: block; font-weight: 400; color: var(--muted); font-size: 0.78em; }
table.splits tr.dnf td.name { color: var(--dnf); }
table.splits td.name .sub-team { display: block; font-weight: 400; color: var(--muted); font-size: 0.78em; }
/* Ordered (set course): cumulative + per-control rank, top-3 (gold/silver/bronze) highlights. */
table.ordered .cp-code { display: block; font-weight: 400; color: var(--muted); font-size: 0.8em; }
table.ordered td.cp-cell .cum { font-weight: 600; }
/* The place badge next to a time — the same treatment on both rows (overall on top, leg below), so the
   leg place never reads as digits glued onto its time. */
table.ordered td .rank { display: inline-block; margin-left: 0.3em; color: var(--muted); font-size: 0.78em; font-weight: 400; vertical-align: baseline; }
table.ordered td.cp-cell.missing { color: var(--muted); }
table.ordered td.leg-cell { color: var(--muted); font-size: 0.85em; padding-top: 0; }
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
table.scored td.cp-cell .cum { display: block; color: var(--muted); font-size: 0.8em; }
/* A scoring (counted) control is highlighted blue across both its rows (code+cumulative over the leg);
   the finish keeps its neutral tint. */
table.scored td.scoring { background: #e3edff; }
table.scored td.cp-cell.scoring { box-shadow: inset 1px 0 0 #bcd4ff, inset -1px 0 0 #bcd4ff, inset 0 1px 0 #bcd4ff; }
table.scored td.leg-cell.scoring { box-shadow: inset 1px 0 0 #bcd4ff, inset -1px 0 0 #bcd4ff, inset 0 -1px 0 #bcd4ff; }
table.scored td.cp-cell.fin { background: #f3f5f8; }
table.scored td.leg-cell { font-size: 0.85em; padding-top: 0; }
table.scored td.leg-cell .leg { color: var(--muted); }
table.scored td.leg-cell .pts { margin-left: 0.3em; font-weight: 700; color: #11622a; }
/* A negative points value — the finish over-time penalty — reads in red. */
table.scored td.leg-cell .pts.neg { color: var(--dnf); }
/* A non-scoring punch (repeat / off-course): greyed in both rows, with a lighter code, as if not on the course. */
table.scored td.unscored, table.scored td.unscored .code, table.scored td.unscored .cum, table.scored td.unscored .leg { color: #aab0bb; }
table.scored td.cp-cell.unscored .code { font-weight: 400; }
.status { display: inline-block; padding: 0 6px; border-radius: 4px; background: #fde8e6; color: var(--dnf); font-weight: 700; font-size: 0.6875rem; }
.doc-footer { padding: 16px; text-align: center; color: var(--muted); font-size: 0.6875rem; }
/* A control cell that opens the detail panel. The two rows of one control are one clickable block: the
   top cell draws the block's left/right/top edge and the bottom cell its left/right/bottom edge, so the
   pair reads as a single outlined box instead of two stacked ones. box-shadow (not outline) is used so
   the picked border composites with the podium tint instead of overpainting the neighbouring column. */
table.ordered td.detail { cursor: pointer; -webkit-tap-highlight-color: transparent; }
table.ordered tr.runner:not(.leg-row) td.detail.picked {
  box-shadow: inset 2px 0 0 var(--accent), inset -2px 0 0 var(--accent), inset 0 2px 0 var(--accent);
}
table.ordered tr.leg-row td.detail.picked {
  box-shadow: inset 2px 0 0 var(--accent), inset -2px 0 0 var(--accent), inset 0 -2px 0 var(--accent);
}
/* ── The click-open detail panel ───────────────────────────────────────────────────────────────────
   Desktop: a small floating card pinned bottom-right. Phone: a full-width bottom sheet (see below). */
/* The dim behind the panel is a phone-only convention (it belongs to the bottom sheet); on a wide screen
   the panel is a small corner card and the table must stay readable behind it. The element is still
   rendered on desktop — transparent — so the click-outside-to-close path is identical everywhere. */
/* ── The click-open detail dialog ──────────────────────────────────────────────────────────────────
   A native <dialog>: the browser supplies the backdrop and the top layer, so no sticky table cell can
   paint over it and no z-index juggling is needed. Desktop: a centred card. Phone: full-screen (below). */
#cp-detail {
  border: 1px solid var(--line); border-radius: 14px;
  padding: 0; width: min(340px, calc(100vw - 32px));
  background: var(--card); color: var(--ink);
  box-shadow: 0 20px 48px rgba(15,23,42,.24);
  font-size: 0.875rem;
}
#cp-detail::backdrop { background: rgba(15,20,30,.4); }
#cp-detail .cp-head {
  display: flex; align-items: flex-start; gap: 8px;
  padding: 12px 12px 8px 16px; border-bottom: 1px solid var(--line);
}
#cp-detail h3 { margin: 0; flex: 1; font-size: 0.875rem; font-weight: 700; line-height: 1.35; }
#cp-detail .cp-close {
  flex: none; border: 0; background: transparent; color: var(--muted);
  font-size: 1.375rem; line-height: 1; cursor: pointer; padding: 0 4px; margin: -2px 0 0;
}
#cp-detail .cp-close:hover { color: var(--ink); }
#cp-body { padding: 4px 16px 16px; }
#cp-detail .cp-section { margin-top: 12px; }
#cp-detail .cp-section-title {
  font-size: 0.625rem; font-weight: 700; letter-spacing: .05em; text-transform: uppercase;
  color: var(--accent); margin-bottom: 4px;
}
#cp-detail .cp-line {
  display: flex; justify-content: space-between; align-items: baseline; gap: 16px;
  padding: 3px 0; font-variant-numeric: tabular-nums;
}
#cp-detail .cp-line .k { color: var(--muted); }
#cp-detail .cp-line .v { font-weight: 700; }
#cp-detail .cp-line .v.lead { color: #11622a; }
/* ── Phone layout ──────────────────────────────────────────────────────────────────────────────────
   The table goes full-bleed with the runner's identity pinned to the left edge, and the dialog takes the
   whole screen so the two columns of figures always fit without the card being clipped by the viewport. */
@media (max-width: 700px) {
  body { font-size: 0.875rem; }
  .doc-header { padding: 16px 12px 10px; }
  .doc-header h1 { font-size: 1.0625rem; }
  .groups-nav { padding: 8px 10px; gap: 5px; overflow-x: auto; flex-wrap: nowrap; }
  .groups-nav a { padding: 4px 12px; font-size: 0.8125rem; }
  .group { padding: 14px 0 4px; }
  .group-header { padding: 0 12px; flex-wrap: wrap; gap: 6px; }
  /* Full-bleed on a phone: the card's rounded frame just wastes width the columns need. */
  .table-wrap {
    width: auto; max-width: 100%;
    border-radius: 0; border-left: 0; border-right: 0; -webkit-overflow-scrolling: touch;
  }
  table.splits th, table.splits td { padding: 0.35em 0.45em; }
  /* A sticky <thead> crossing the sticky left columns forces a two-axis re-layout on every scroll frame
     — the main source of stutter on a phone. The sticky group nav already keeps the reader oriented, so
     only the identity columns stay pinned here. */
  table.splits thead th { position: static; }
  /* «Місце» and «Прізвище» pin to the left edge as one unit while the КП columns scroll sideways. Their
     widths are in em so they track the pinch-zoom font scale instead of clipping the name when the text
     grows. */
  table.splits td.place, table.splits th.col-place {
    position: sticky; left: 0; z-index: 2;
    width: 2.4em; min-width: 2.4em; padding-left: 0.3em; padding-right: 0.3em; background: var(--card);
  }
  table.splits td.name, table.splits th.col-name {
    position: sticky; left: 2.4em; z-index: 2;
    width: 7.5em; min-width: 7.5em; max-width: 7.5em;
    background: var(--card); border-right: 1px solid var(--line);
    white-space: normal; overflow-wrap: anywhere;
  }
  table.splits th.col-place, table.splits th.col-name { z-index: 6; background: #eef1f6; }
  table.splits tr.dnf td.place, table.splits tr.dnf td.name { background: var(--card); }
  /* The dialog keeps its default centred-card behaviour here; only the touch targets grow a little. */
  #cp-detail h3 { font-size: 1rem; }
  #cp-detail .cp-close { font-size: 1.625rem; padding: 0 6px; }
  #cp-detail .cp-line { padding: 5px 0; }
}
@media print {
  /* Printing ignores whatever the reader pinched the screen to: the root scale is reset so the paper
     always gets the same size, whatever the on-screen zoom happens to be. */
  html { font-size: 16px !important; }
  body { background: #fff; font-size: 0.6875rem; }
  .groups-nav { display: none; }
  .table-wrap { border-color: #ccc; }
  table.splits tbody tr.runner { page-break-inside: avoid; }
  #cp-detail { display: none !important; }
}
""";

    // The page's behaviour, in one small dependency-free block.
    //
    // A control occupies two stacked cells (cumulative on top, leg below) that share a data-c column index
    // inside one runner's pair of rows; only the top cell carries the figures. Clicking either one lights
    // the whole block and opens the shared <dialog>, filled from that pair — showModal() gives us the
    // backdrop, the top layer and Esc-to-close for free. The runner's name comes from the row's name cell
    // and the control's caption from the column header, so neither is repeated per cell.
    //
    // The hover tooltip is attached on first hover for the same reason: a title= on every cell is a large
    // share of a full competition's file, for text that is mostly never read.
    private const string Script = """
(function () {
  var panel = document.getElementById('cp-detail');
  var titleEl = document.getElementById('cp-title');
  var bodyEl = document.getElementById('cp-body');
  var picked = [];
  if (!panel) return;

  function label(name) { return panel.getAttribute('data-l-' + name) || ''; }

  // The two cells of one control: the clicked one and its partner in the sibling row.
  function block(cell) {
    var row = cell.parentNode;
    var isLeg = row.classList.contains('leg-row');
    var mate = isLeg ? row.previousElementSibling : row.nextElementSibling;
    var out = [cell];
    if (mate && mate.classList.contains(isLeg ? 'runner' : 'leg-row')) {
      var twin = mate.querySelector('td[data-c="' + cell.dataset.c + '"]');
      if (twin) out.push(twin);
    }
    return out;
  }

  // The figures live on the cumulative (top) cell of the block.
  function figures(cells) {
    for (var i = 0; i < cells.length; i++)
      if (cells[i].classList.contains('cp-cell')) return cells[i].dataset;
    return cells[0].dataset;
  }

  function runnerName(cell) {
    var row = cell.parentNode;
    if (row.classList.contains('leg-row')) row = row.previousElementSibling || row;
    var n = row.querySelector('td.name');
    return n ? (n.firstChild ? n.firstChild.textContent : n.textContent).trim() : '';
  }

  function controlName(cell) {
    var table = cell.closest('table');
    var heads = table.tHead.rows[0].cells;
    // The control columns are the trailing ones; the leading identity columns have no data-c counterpart.
    var offset = heads.length - (table.tHead.rows[0].querySelectorAll('th.col-cp').length);
    var th = heads[offset + Number(cell.dataset.c)];
    return th ? th.textContent.replace(/\s+/g, ' ').trim() : '';
  }

  function line(key, value, lead) {
    var row = document.createElement('div');
    row.className = 'cp-line';
    var k = document.createElement('span');
    k.className = 'k';
    k.textContent = key;
    var v = document.createElement('span');
    v.className = lead ? 'v lead' : 'v';
    v.textContent = value;
    row.appendChild(k);
    row.appendChild(v);
    return row;
  }

  function section(title, time, place, gap) {
    if (!time) return null;
    var box = document.createElement('div');
    box.className = 'cp-section';
    var head = document.createElement('div');
    head.className = 'cp-section-title';
    head.textContent = title;
    box.appendChild(head);
    box.appendChild(line(label('time'), time, false));
    if (place) box.appendChild(line(label('place'), place, place === '1'));
    if (gap !== undefined) {
      var isLead = gap === '0:00';
      box.appendChild(line(label('gap'), isLead ? label('leader') : '+' + gap, isLead));
    }
    return box;
  }

  function unpick() {
    for (var i = 0; i < picked.length; i++) picked[i].classList.remove('picked');
    picked = [];
  }

  function open(cell) {
    var cells = block(cell);
    var d = figures(cells);
    if (!d.t && !d.l) return;

    unpick();
    for (var i = 0; i < cells.length; i++) {
      cells[i].classList.add('picked');
      picked.push(cells[i]);
    }

    var tpl = label('title');
    var who = runnerName(cell), what = controlName(cell);
    titleEl.textContent = tpl ? tpl.replace('{0}', who).replace('{1}', what) : (who + ' \u2014 ' + what);

    bodyEl.textContent = '';
    var parts = [section(label('total'), d.t, d.tp, d.tg), section(label('leg'), d.l, d.lp, d.lg)];
    for (var j = 0; j < parts.length; j++) if (parts[j]) bodyEl.appendChild(parts[j]);

    if (panel.open) panel.close();
    if (panel.showModal) panel.showModal(); else panel.setAttribute('open', '');
  }

  document.addEventListener('click', function (e) {
    var t = e.target;
    if (!t || !t.closest) return;
    var cell = t.closest('td.detail');
    if (cell) { open(cell); return; }
    // A click on the backdrop lands on the <dialog> itself (it fills the viewport), so it closes too.
    if (t.closest('.cp-close') || t === panel) {
      if (panel.close && panel.open) panel.close(); else panel.removeAttribute('open');
    }
  });

  // Fires on Esc and on close() alike, so the block highlight always clears with the dialog.
  panel.addEventListener('close', unpick);

  // Lazy hover tooltip: built once per cell, then left on the element for the browser to reuse.
  document.addEventListener('mouseover', function (e) {
    var t = e.target;
    if (!t || !t.closest) return;
    var cell = t.closest('td.detail');
    if (!cell || cell.title) return;
    var d = figures(block(cell));
    var lines = [];
    if (d.tg && d.tg !== '0:00') lines.push(label('losstotal').replace('{0}', d.tg));
    if (d.lg && d.lg !== '0:00') lines.push(label('lossleg').replace('{0}', d.lg));
    // A space keeps the "already built" check true for a cell that legitimately has nothing to say.
    cell.title = lines.length ? lines.join('\n') : ' ';
  });

  // ── Pinch to resize the text ──────────────────────────────────────────────────────────────────────
  // Two fingers change the ROOT FONT SIZE rather than the browser's page zoom. Every size in the CSS is
  // in rem, so pinching out shrinks the whole table and fits more controls on screen, while the layout
  // stays a layout: the sticky name column keeps working, the dialog stays a properly centred card, and
  // the page never ends up wider than the screen. Browser zoom does none of that — it scales the pixels
  // of a page that is already exactly one screen wide, so there is nothing to pull back to.
  var ROOT = document.documentElement;
  var MIN = 5, MAX = 30, BASE = 16;
  var scale = BASE;
  var startDist = 0, startScale = 0, pinching = false;

  function apply(px) {
    scale = Math.min(MAX, Math.max(MIN, px));
    ROOT.style.fontSize = scale + 'px';
  }

  function distance(t) {
    var dx = t[0].clientX - t[1].clientX;
    var dy = t[0].clientY - t[1].clientY;
    return Math.sqrt(dx * dx + dy * dy);
  }

  document.addEventListener('touchstart', function (e) {
    if (e.touches.length !== 2) return;
    // Not inside the dialog: there the two-finger gesture is not ours to take.
    var tg = e.target;
    if (tg && tg.closest && tg.closest('#cp-detail')) return;
    pinching = true;
    startDist = distance(e.touches);
    startScale = scale;
  }, { passive: true });

  document.addEventListener('touchmove', function (e) {
    if (!pinching || e.touches.length !== 2) return;
    var d = distance(e.touches);
    if (!startDist) return;
    // Preventing the default stops the browser from also scrolling/zooming under the gesture.
    if (e.cancelable) e.preventDefault();
    apply(startScale * (d / startDist));
  }, { passive: false });

  function endPinch(e) {
    if (pinching && (!e.touches || e.touches.length < 2)) pinching = false;
  }
  document.addEventListener('touchend', endPinch, { passive: true });
  document.addEventListener('touchcancel', endPinch, { passive: true });

  // Ctrl+wheel and the keyboard shortcuts do the same thing on a desktop, for consistency.
  document.addEventListener('wheel', function (e) {
    if (!e.ctrlKey) return;
    e.preventDefault();
    apply(scale * (e.deltaY < 0 ? 1.1 : 1 / 1.1));
  }, { passive: false });

  document.addEventListener('keydown', function (e) {
    if (!e.ctrlKey && !e.metaKey) return;
    if (e.key === '=' || e.key === '+') { e.preventDefault(); apply(scale * 1.1); }
    else if (e.key === '-') { e.preventDefault(); apply(scale / 1.1); }
    else if (e.key === '0') { e.preventDefault(); apply(BASE); }
  });
})();
""";

    /// <summary>
    /// One runner's set-course splits reduced to per-control cumulative time, leg split and rank, keyed by the
    /// <b>column index</b> — the position on the prescribed course, with the finish under
    /// <paramref name="finishColumn"/> (the column past the last control). Built from the on-course punches of
    /// the runner's <see cref="SplitsView.Passage"/>; a missed/off-course control simply has no entry.
    /// <para>
    /// The key is the position, not the code, because a course may visit the same control twice (e.g.
    /// «55 44 43 55 66»): keying by code put the second visit's time into both «55» columns.
    /// </para>
    /// </summary>
    private sealed class RunnerSplits
    {
        public required SplitExportRow Row { get; init; }
        public Dictionary<int, TimeSpan> Cumulative { get; } = [];
        public Dictionary<int, TimeSpan> Leg { get; } = [];
        public Dictionary<int, int> Rank { get; } = [];
        public Dictionary<int, int> LegRank { get; } = [];

        /// <summary>
        /// The last column whose cumulative time still describes the <b>whole prescribed course so far</b> —
        /// i.e. the column just before the runner's first missed control. Up to (and including) it the elapsed
        /// time is comparable with everyone else's and takes part in the ranking/leader baseline; from the
        /// first skipped control on it no longer is (the runner ran a shorter course), so those cells are
        /// still printed but neither ranked nor highlighted. −1 when the very first control was missed.
        /// An OK run keeps every column, the finish included.
        /// </summary>
        public int ValidCumulativeThrough { get; private set; } = -1;

        /// <summary>True when this runner's cumulative time at <paramref name="col"/> counts for the
        /// ranking and the leader baseline (see <see cref="ValidCumulativeThrough"/>).</summary>
        public bool CumulativeCountsAt(int col) => col <= ValidCumulativeThrough;

        public static RunnerSplits From(SplitExportRow row, IReadOnlyList<string> columns, int finishColumn)
        {
            var rs = new RunnerSplits { Row = row };
            // Fallback for a layout whose punches carry no course position (e.g. the «mixed» pattern splits):
            // walk the columns forward, matching each on-course punch to the next column with that code, so
            // repeats still land on distinct columns.
            var next = 0;
            foreach (var p in row.Splits.Passage)
            {
                if (p.Kind == PassageKind.Finish)
                {
                    if (p.Elapsed is { } fe) rs.Cumulative[finishColumn] = fe;
                    if (p.Leg is { } fl) rs.Leg[finishColumn] = fl;
                    continue;
                }
                if (p.Kind != PassageKind.Control || !p.OnCourse)
                    continue; // only on-course punches map onto the prescribed columns
                var code = p.Code.Trim();
                if (code.Length == 0)
                    continue;

                int column;
                if (p.CourseIndex is { } ci && ci >= 0 && ci < columns.Count)
                {
                    column = ci;
                    next = ci + 1;
                }
                else
                {
                    var found = -1;
                    for (var j = next; j < columns.Count; j++)
                    {
                        if (string.Equals(code, columns[j].Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            found = j;
                            break;
                        }
                    }
                    if (found < 0)
                        continue;
                    column = found;
                    next = found + 1;
                }

                if (p.Elapsed is { } e) rs.Cumulative[column] = e;
                if (p.Leg is { } l) rs.Leg[column] = l;
            }

            // How far the cumulative times stay comparable: an OK run all the way (the finish column
            // included), otherwise up to the column before the first control with no time — the first one
            // the runner missed. Note a non-OK run can still own every control column (e.g. a DSQ or a
            // late finish), in which case nothing is cut off.
            var through = finishColumn;
            for (var col = 0; col <= finishColumn; col++)
            {
                if (rs.Cumulative.ContainsKey(col))
                    continue;
                // The finish column is only "missing" for a run with no finish punch; either way everything
                // before it stands.
                through = col - 1;
                break;
            }
            rs.ValidCumulativeThrough = row.IsOk ? finishColumn : through;
            return rs;
        }
    }
}
