using System.Globalization;
using System.Text;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.DataAccess.Documents;

/// <summary>
/// Renders a <see cref="MonitorDocument"/> to a self-contained, modern UTF-8 HTML file for an on-screen
/// results monitor — the successor to the legacy orientir.exe <c>rezNN.htm</c> screens. One dark, large-type
/// section per group (caption + a results table), with all CSS inlined and a small inlined script that:
/// <list type="bullet">
///   <item><b>auto-refreshes</b> — reloads the page every <see cref="MonitorDocument.RefreshSeconds"/> seconds,
///   pausing the reload until a full scroll cycle finishes so a refresh never interrupts mid-scroll;</item>
///   <item><b>auto-scrolls</b> — smoothly scrolls top→bottom at <see cref="MonitorDocument.ScrollSpeed"/>
///   px/s, holds briefly at each end, then loops. Disabled when the speed is 0 or the content fits.</item>
/// </list>
/// No web server is needed: the page reloads itself via <c>location.reload()</c>, so the file works opened
/// straight from disk (file://) on a venue screen or kiosk browser. Lives in DataAccess as an output writer,
/// alongside the split/.docx/.xlsx writers; BusinessLogic only produces the values-only document.
/// </summary>
public sealed class HtmlMonitorWriter : IMonitorHtmlWriter
{
    public byte[] Write(MonitorDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var refresh = Math.Max(MonitorFile.MinRefreshSeconds, document.RefreshSeconds);
        // The configured px/s, given a constant boost so the live monitor scrolls noticeably faster than the
        // stored value (kept here, not in settings, so existing files speed up without re-saving each one).
        var scroll = (int)Math.Round(Math.Max(0, document.ScrollSpeed) * 1.6);

        var sb = new StringBuilder(64 * 1024);
        sb.Append("<!DOCTYPE html>\n<html lang=\"uk\">\n<head>\n");
        sb.Append("<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(Esc(document.Title)).Append("</title>\n");
        sb.Append("<style>\n").Append(Css).Append("</style>\n");
        sb.Append("</head>\n<body>\n");

        sb.Append("<div id=\"scroll\">\n");
        WriteHeader(sb, document);

        // Lead-in blank space so the first runners sit on screen a moment before the scroll starts moving.
        sb.Append("<div class=\"spacer\"></div>\n");

        foreach (var group in document.Groups)
            WriteGroup(sb, group, document.Columns);

        // Trailing blank space so the last runners linger before the page loops back to the top.
        sb.Append("<div class=\"spacer\"></div>\n");

        sb.Append("</div>\n"); // #scroll

        WriteScript(sb, refresh, scroll);
        sb.Append("</body>\n</html>\n");

        // UTF-8 without a BOM — the <meta charset> already declares the encoding.
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(sb.ToString());
    }

    private static void WriteHeader(StringBuilder sb, MonitorDocument d)
    {
        sb.Append("<header>\n");
        sb.Append("<h1>").Append(Esc(d.Title)).Append("</h1>\n");
        if (!string.IsNullOrWhiteSpace(d.Subtitle) &&
            !string.Equals(d.Subtitle, d.Title, StringComparison.Ordinal))
            sb.Append("<p class=\"sub\">").Append(Esc(d.Subtitle)).Append("</p>\n");
        sb.Append("</header>\n");
    }

    private static void WriteGroup(StringBuilder sb, MonitorGroup group, IReadOnlyList<MonitorColumn> columns)
    {
        sb.Append("<section class=\"group\">\n");
        sb.Append("<h2>").Append(Esc(group.Name)).Append("</h2>\n");
        if (!string.IsNullOrWhiteSpace(group.Caption))
            sb.Append("<p class=\"caption\">").Append(Esc(group.Caption)).Append("</p>\n");

        sb.Append("<table>\n<thead>\n<tr>\n");
        foreach (var col in columns)
            sb.Append("<th class=\"").Append(CssClass(col.Column)).Append("\">")
              .Append(Esc(col.Header)).Append("</th>\n");
        sb.Append("</tr>\n</thead>\n<tbody>\n");

        foreach (var row in group.Cells)
        {
            sb.Append(row.Unplaced ? "<tr class=\"unplaced\">\n" : "<tr>\n");
            for (var c = 0; c < columns.Count; c++)
            {
                var value = c < row.Values.Count ? row.Values[c] : string.Empty;
                sb.Append("<td class=\"").Append(CssClass(columns[c].Column)).Append("\">")
                  .Append(Esc(value)).Append("</td>\n");
            }
            sb.Append("</tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</section>\n");
    }

    // The auto-refresh + smooth auto-scroll behaviour. Kept tiny and dependency-free so it runs from file://.
    private static void WriteScript(StringBuilder sb, int refreshSeconds, int scrollSpeed)
    {
        sb.Append("<script>\n");
        sb.Append("(function(){\n");
        sb.Append("  var refreshMs = ").Append(refreshSeconds * 1000).Append(";\n");
        sb.Append("  var speed = ").Append(scrollSpeed).Append("; // px per second, 0 = no scroll\n");
        sb.Append("  var holdMs = 2500;     // pause at top/bottom before turning around\n");
        sb.Append("""
              var refreshDue = false;
              // Reload only at the top of a cycle, so a refresh never cuts a scroll short.
              function reloadNow(){ location.reload(); }
              setTimeout(function(){ refreshDue = true; }, refreshMs);

              // Read/write the window scroll directly — robust across browsers where the scroller is
              // <html> vs <body> (setting documentElement.scrollTop alone is unreliable).
              function curY(){ return window.pageYOffset || document.documentElement.scrollTop || 0; }
              function setY(y){ window.scrollTo(0, y); }
              function maxScroll(){
                var doc = document.documentElement, b = document.body;
                var h = Math.max(doc.scrollHeight, b ? b.scrollHeight : 0);
                return Math.max(0, h - window.innerHeight);
              }

              // No scroll wanted, or everything already fits → just plain meta-style refresh.
              if (speed <= 0 || maxScroll() <= 2){
                setTimeout(reloadNow, refreshMs);
                return;
              }

              // One-way scroll: glide top → bottom, hold, then snap straight back to the top and start over
              // (no slow reverse). The jump to the top is also where a pending refresh is applied.
              var pos = 0;           // tracked scroll position (px from top)
              var holding = true;    // start with a hold at the top
              var last = null;
              setTimeout(function(){ holding = false; }, holdMs);

              function restartCycle(){
                if (refreshDue){ reloadNow(); return; }   // reload at the top instead of looping
                pos = 0; setY(0);                          // jump straight back to the very top
                holding = true;
                setTimeout(function(){ holding = false; }, holdMs);
              }

              function step(ts){
                if (last === null) last = ts;
                var dt = (ts - last) / 1000; last = ts;
                var max = maxScroll();

                if (!holding){
                  pos += speed * dt;
                  if (pos >= max){
                    pos = max; setY(max);
                    holding = true;
                    // Hold at the bottom, then jump back to the top to begin the next cycle.
                    setTimeout(restartCycle, holdMs);
                  } else {
                    setY(pos);
                  }
                }
                requestAnimationFrame(step);
              }
              requestAnimationFrame(step);

            """);
        sb.Append("})();\n");
        sb.Append("</script>\n");
    }

    // Column → CSS class, for per-column alignment (numbers/times centred, names left-aligned).
    private static string CssClass(ResultColumn column) => column switch
    {
        ResultColumn.FullName or ResultColumn.Team or ResultColumn.Club or ResultColumn.Region => "c-name",
        ResultColumn.Place => "c-place",
        _ => "c-num",
    };

    private static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;");
    }

    private const string Css = """
        :root{
          --bg:#f4f6fb; --panel:#e7ecf6; --line:#cdd6e6; --text:#16202f; --muted:#5a6b86;
          --accent:#1f6feb; --head:#dde5f2; --row:#ffffff; --row2:#f0f3fa; --dim:#9aa6ba;
        }
        *{ box-sizing:border-box; }
        html,body{ margin:0; padding:0; }
        body{
          background:var(--bg); color:var(--text); overflow-x:hidden;
          font-family:"Segoe UI",system-ui,Roboto,Arial,sans-serif;
          font-size:clamp(16px,2.2vh,28px); line-height:1.25;
        }
        #scroll{ padding:2vh 3vw 6vh; }
        /* Blank lead/tail space so the first and last rows linger on screen at the turn-around. */
        .spacer{ height:30vh; }
        header{ text-align:center; margin:0 0 2.2vh; }
        h1{ margin:0; font-size:1.7em; font-weight:700; letter-spacing:.3px; }
        .sub{ margin:.4em 0 0; color:var(--muted); font-size:.85em; }
        section.group{ margin:0 0 3.2vh; }
        h2{
          margin:0 0 .3em; padding:.25em .6em; font-size:1.25em; font-weight:700;
          color:var(--accent); border-left:.35em solid var(--accent); background:var(--panel);
          border-radius:.25em;
        }
        .caption{ margin:0 0 .5em .9em; color:var(--muted); font-size:.8em; }
        table{ width:100%; border-collapse:collapse; }
        thead th{
          position:relative; text-align:center; font-weight:600; font-size:.8em;
          text-transform:uppercase; letter-spacing:.4px; color:var(--muted);
          background:var(--head); padding:.5em .6em; border-bottom:2px solid var(--line);
        }
        tbody td{ padding:.45em .6em; border-bottom:1px solid var(--line); }
        tbody tr:nth-child(odd){ background:var(--row); }
        tbody tr:nth-child(even){ background:var(--row2); }
        tbody tr.unplaced{ color:var(--dim); }
        td.c-name,th.c-name{ text-align:left; }
        td.c-num,th.c-num{ text-align:center; white-space:nowrap; }
        td.c-place,th.c-place{ text-align:center; width:3.2em; font-weight:700; }
        td.c-name{ font-weight:600; }
        """;
}
