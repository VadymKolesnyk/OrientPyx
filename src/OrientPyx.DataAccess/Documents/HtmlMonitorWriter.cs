using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.DataAccess.Documents;

/// <summary>
/// Renders a <see cref="MonitorDocument"/> to a self-contained, modern UTF-8 HTML file for an on-screen
/// results monitor — the successor to the legacy orientir.exe <c>rezNN.htm</c> screens. One large-type
/// section per group (caption + a results table), with all CSS inlined and a small inlined script that:
/// <list type="bullet">
///   <item><b>keeps the results current in place</b> — every <see cref="PollMs"/> it loads the live-data script
///   written beside the page (<see cref="WriteData"/>, a <c>&lt;script&gt;</c> tag, because a page opened from disk
///   (file://) may not <c>fetch</c>) and, when the header or the groups changed, swaps them in, keeping the group at
///   the top of the screen where it was — a result or a withdrawal shows within seconds, mid-scroll. A page whose
///   timing changed reloads; without the data file (only the page copied elsewhere) the page falls back to
///   reloading itself every <see cref="MonitorDocument.RefreshSeconds"/>, at the top of a scroll cycle;</item>
///   <item><b>auto-scrolls</b> — smoothly scrolls top→bottom at <see cref="MonitorDocument.ScrollSpeed"/>
///   px/s, holds briefly at the bottom, jumps back to the top and loops. Still while the content fits.</item>
/// </list>
/// No web server is needed. Lives in DataAccess as an output writer, alongside the split/.docx/.xlsx writers;
/// BusinessLogic only produces the values-only document.
/// </summary>
public sealed class HtmlMonitorWriter : IMonitorHtmlWriter
{
    /// <summary>How often the open page loads its data file. Cheap (a local file, swapped only when it changed), so
    /// well under the shortest refresh: the delay a viewer sees is about the file's refresh interval.</summary>
    private const int PollMs = 2000;

    /// <summary>Bumped when the page's markup, CSS or script changes, so a page an older build wrote reloads
    /// itself into the new one instead of being fed data it doesn't lay out.</summary>
    private const int PageVersion = 2;

    // ASCII-only JSON (Cyrillic as \uXXXX, «<» escaped): the data script reads the same in any encoding the browser
    // guesses for a file:// script, and a value can never close the inline <script> of the page.
    private static readonly JsonSerializerOptions JsonOptions = new();

    public byte[] Write(MonitorDocument document, string? dataFileName = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var head = BuildHead(document);
        var body = BuildGroups(document);

        var sb = new StringBuilder(64 * 1024);
        sb.Append("<!DOCTYPE html>\n<html lang=\"uk\">\n<head>\n");
        sb.Append("<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(Esc(document.Title)).Append("</title>\n");
        sb.Append(HtmlFavicon.LinkTag);
        sb.Append("<style>\n").Append(Css).Append("</style>\n");
        sb.Append("</head>\n<body>\n");

        sb.Append("<div id=\"scroll\">\n");
        sb.Append("<div id=\"head\">\n").Append(head).Append("</div>\n");

        // Lead-in blank space so the first runners sit on screen a moment before the scroll starts moving.
        sb.Append("<div class=\"spacer\"></div>\n");

        sb.Append("<div id=\"groups\">\n").Append(body).Append("</div>\n");

        // Trailing blank space so the last runners linger before the page loops back to the top.
        sb.Append("<div class=\"spacer\"></div>\n");

        sb.Append("</div>\n"); // #scroll

        WriteScript(sb, document, dataFileName, Stamp(head), Stamp(body));
        sb.Append("</body>\n</html>\n");

        // UTF-8 without a BOM — the <meta charset> already declares the encoding.
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(sb.ToString());
    }

    public byte[] WriteData(MonitorDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var head = BuildHead(document);
        var body = BuildGroups(document);
        var data = new
        {
            layout = Layout(document),
            title = document.Title,
            hv = Stamp(head),
            head,
            bv = Stamp(body),
            body,
        };

        // One statement: a half-written file (read while the app rewrites it) doesn't parse and so changes nothing.
        var js = "orientPyxMonitor(" + JsonSerializer.Serialize(data, JsonOptions) + ");\n";
        return Encoding.ASCII.GetBytes(js);
    }

    private static int RefreshSeconds(MonitorDocument d) => Math.Max(MonitorFile.MinRefreshSeconds, d.RefreshSeconds);

    // The configured px/s, given a constant boost so the live monitor scrolls noticeably faster than the
    // stored value (kept here, not in settings, so existing files speed up without re-saving each one).
    private static int ScrollSpeed(MonitorDocument d) => (int)Math.Round(Math.Max(0, d.ScrollSpeed) * 1.6);

    // What the page's script itself depends on: a data file with another layout makes the page reload.
    private static string Layout(MonitorDocument d) => $"{PageVersion}:{RefreshSeconds(d)}:{ScrollSpeed(d)}";

    // A short content hash — the page swaps a part only when its hash changed.
    private static string Stamp(string html) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html)), 0, 8);

    private static string BuildHead(MonitorDocument d)
    {
        var sb = new StringBuilder();
        sb.Append("<header>\n");
        sb.Append("<h1>").Append(Esc(d.Title)).Append("</h1>\n");
        if (!string.IsNullOrWhiteSpace(d.Subtitle) &&
            !string.Equals(d.Subtitle, d.Title, StringComparison.Ordinal))
            sb.Append("<p class=\"sub\">").Append(Esc(d.Subtitle)).Append("</p>\n");
        sb.Append("</header>\n");
        return sb.ToString();
    }

    private static string BuildGroups(MonitorDocument d)
    {
        var sb = new StringBuilder(64 * 1024);
        foreach (var group in d.Groups)
            WriteGroup(sb, group, d.Columns);
        return sb.ToString();
    }

    private static void WriteGroup(StringBuilder sb, MonitorGroup group, IReadOnlyList<MonitorColumn> columns)
    {
        // data-g: the group the screen keeps in place when the rows are swapped in.
        sb.Append("<section class=\"group\" data-g=\"").Append(Esc(group.Name)).Append("\">\n");
        sb.Append("<h2>").Append(Esc(group.Name)).Append("</h2>\n");
        if (!string.IsNullOrWhiteSpace(group.Caption))
            sb.Append("<p class=\"caption\">").Append(Esc(group.Caption)).Append("</p>\n");

        // A column the group leaves out (another discipline's plugin column) is not in its table at all.
        var shown = Enumerable.Range(0, columns.Count).Where(c => !group.HiddenColumns.Contains(c)).ToList();

        sb.Append("<table>\n<thead>\n<tr>\n");
        foreach (var c in shown)
            sb.Append("<th class=\"").Append(CssClass(columns[c])).Append("\">")
              .Append(Esc(columns[c].Header)).Append("</th>\n");
        sb.Append("</tr>\n</thead>\n<tbody>\n");

        foreach (var row in group.Cells)
        {
            var rowClasses = string.Join(' ', new[]
            {
                row.Unplaced ? "unplaced" : null,
                row.Kind == MonitorRowKind.Team ? "team" : null,
                row.Kind == MonitorRowKind.Leg ? "leg" : null,
            }.Where(c => c is not null));
            sb.Append(rowClasses.Length > 0 ? "<tr class=\"" + rowClasses + "\">\n" : "<tr>\n");
            foreach (var c in shown)
            {
                var value = c < row.Values.Count ? row.Values[c] : string.Empty;
                sb.Append("<td class=\"").Append(CssClass(columns[c])).Append("\">")
                  .Append(Esc(value)).Append("</td>\n");
            }
            sb.Append("</tr>\n");
        }

        sb.Append("</tbody>\n</table>\n</section>\n");
    }

    // The live update + smooth auto-scroll behaviour. Kept tiny and dependency-free so it runs from file://.
    private static void WriteScript(
        StringBuilder sb, MonitorDocument d, string? dataFileName, string headStamp, string bodyStamp)
    {
        static string Js(string value) => JsonSerializer.Serialize(value, JsonOptions);

        sb.Append("<script>\n");
        sb.Append("(function(){\n");
        sb.Append("  var refreshMs = ").Append(RefreshSeconds(d) * 1000).Append(";\n");
        sb.Append("  var speed = ").Append(ScrollSpeed(d)).Append("; // px per second, 0 = no scroll\n");
        sb.Append("  var holdMs = 2500;     // pause at top/bottom before turning around\n");
        sb.Append("  var pollMs = ").Append(PollMs).Append(";\n");
        sb.Append("  var layout = ").Append(Js(Layout(d))).Append(";\n");
        sb.Append("  var dataSrc = ").Append(Js(dataFileName ?? string.Empty)).Append(";\n");
        sb.Append("  var hv = ").Append(Js(headStamp)).Append(", bv = ").Append(Js(bodyStamp)).Append(";\n");
        sb.Append("""
              var live = false;      // the data file answered: the page stays current without reloading
              var stale = false;     // the data file has another layout: reload (see the fallback below)
              var refreshDue = false;
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

              var pos = 0;           // tracked scroll position (px from top)
              var holding = true;    // start with a hold at the top
              var last = null;

              // The group at the top of the screen and how far the screen is into it, kept across a swap.
              function sections(){ return document.querySelectorAll('#groups > section'); }
              function anchor(){
                var y = curY(), secs = sections();
                for (var i = secs.length - 1; i >= 0; i--){
                  var top = secs[i].getBoundingClientRect().top + y;
                  if (top <= y) return { g: secs[i].getAttribute('data-g'), i: i, off: y - top };
                }
                return null;
              }
              function restore(a){
                if (!a) return;
                var secs = sections(), t = null;
                for (var i = 0; i < secs.length; i++){
                  if (secs[i].getAttribute('data-g') === a.g){ t = secs[i]; break; }
                }
                if (!t && secs.length) t = secs[Math.min(a.i, secs.length - 1)];
                if (!t) return;
                var y = t.getBoundingClientRect().top + curY() + Math.min(a.off, t.offsetHeight);
                pos = Math.max(0, Math.min(y, maxScroll()));
                setY(pos);
              }

              // Called by the data file: swap in what changed, keeping the screen on the same group.
              window.orientPyxMonitor = function(d){
                if (stale) return;
                if (d.layout !== layout){ stale = true; live = false; return; }
                live = true;
                if (d.hv === hv && d.bv === bv) return;
                var a = anchor();
                if (d.hv !== hv){
                  document.getElementById('head').innerHTML = d.head;
                  document.title = d.title;
                  hv = d.hv;
                }
                if (d.bv !== bv){
                  document.getElementById('groups').innerHTML = d.body;
                  bv = d.bv;
                }
                restore(a);
              };

              // A <script> tag, not fetch(): a page opened from disk (file://) may load scripts but not fetch files.
              function poll(){
                var s = document.createElement('script');
                s.src = encodeURIComponent(dataSrc) + '?t=' + new Date().getTime();
                s.onload = s.onerror = function(){ if (s.parentNode) s.parentNode.removeChild(s); };
                document.head.appendChild(s);
              }
              if (dataSrc) setInterval(poll, pollMs);

              // Without live data (no data file beside the page, or another layout) the page reloads itself —
              // a scrolling one only at the top of a cycle, so a reload never cuts a scroll short.
              function scrolls(){ return speed > 0 && maxScroll() > 2; }
              setInterval(function(){ if (refreshDue && !live && !scrolls()) reloadNow(); }, 1000);

              if (speed <= 0) return;

              // One-way scroll: glide top → bottom, hold, then snap straight back to the top and start over
              // (no slow reverse). The jump to the top is also where a pending reload is applied.
              setTimeout(function(){ holding = false; }, holdMs);

              function restartCycle(){
                if (refreshDue && !live){ reloadNow(); return; }   // reload at the top instead of looping
                pos = 0; setY(0);                                  // jump straight back to the very top
                holding = true;
                setTimeout(function(){ holding = false; }, holdMs);
              }

              function step(ts){
                if (last === null) last = ts;
                var dt = (ts - last) / 1000; last = ts;
                var max = maxScroll();

                if (!holding){
                  if (max <= 2){
                    pos = 0;                 // everything fits — stay put until the rows outgrow the screen
                  } else {
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
                }
                requestAnimationFrame(step);
              }
              requestAnimationFrame(step);

            """);
        sb.Append("})();\n");
        sb.Append("</script>\n");
    }

    // Column → CSS class, for per-column alignment (numbers/times centred, names left-aligned).
    private static string CssClass(MonitorColumn column) => column.Column switch
    {
        ResultColumn.Extra => column.Extra is { IsNumeric: false } ? "c-name" : "c-num",
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
        tbody tr.team{ font-weight:700; }
        tbody tr.leg{ font-size:0.85em; }
        tbody tr.leg td:first-child{ padding-left:1.6em; }
        td.c-name,th.c-name{ text-align:left; }
        td.c-num,th.c-num{ text-align:center; white-space:nowrap; }
        td.c-place,th.c-place{ text-align:center; width:3.2em; font-weight:700; }
        td.c-name{ font-weight:600; }
        """;
}
