using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.DataAccess.Documents;

namespace OrientPyx.Tests.Core;

/// <summary>
/// The monitor page stays current without reloading: beside <c>rez01.html</c> the app writes <c>rez01.data.js</c>, the
/// open page loads it every few seconds and swaps in the header and the groups that changed, keeping the group at the
/// top of the screen in place — a withdrawal shows within seconds instead of after a whole scroll cycle.
/// </summary>
public sealed class MonitorLiveDataTests
{
    private static readonly HtmlMonitorWriter Writer = new();

    [Fact]
    public void DataFile_SitsBesideThePage()
    {
        Assert.Equal("rez01.data.js", MonitorFile.DataFileName("rez01.html"));
        Assert.Equal("screen.data.js", MonitorFile.DataFileName(@"C:\events\cup\day1\screen.htm"));
        Assert.Equal(5, MonitorFile.New("rez01.html", "Екран").RefreshSeconds); // 5 s by default
    }

    [Fact]
    public void Page_LoadsItsDataFile_AndDataCarriesTheSameContent()
    {
        var doc = Document(Runner("Іваненко Іван", "на дистанції"));
        var page = Encoding.UTF8.GetString(Writer.Write(doc, "rez01.data.js"));
        var data = Data(Writer.WriteData(doc));

        Assert.Contains("var dataSrc = \"rez01.data.js\";", page);
        Assert.Contains("<div id=\"groups\">", page);
        Assert.Contains("<section class=\"group\" data-g=\"М21\">", page);
        // The page knows the stamps of what it shows: the same data swaps nothing in.
        Assert.Contains($"hv = \"{data.GetProperty("hv").GetString()}\", bv = \"{data.GetProperty("bv").GetString()}\"", page);
        Assert.Contains("Іваненко Іван", data.GetProperty("body").GetString());
        Assert.Equal("Екран 1", data.GetProperty("title").GetString());

        // Without a data file the page reloads itself, as before.
        Assert.Contains("var dataSrc = \"\";", Encoding.UTF8.GetString(Writer.Write(doc)));
    }

    [Fact]
    public void Data_ChangesOnlyWhatChanged()
    {
        var before = Data(Writer.WriteData(Document(Runner("Іваненко Іван", "на дистанції"))));
        var withdrawn = Data(Writer.WriteData(Document(Runner("Іваненко Іван", "DNS"))));
        var slower = Data(Writer.WriteData(Document(Runner("Іваненко Іван", "DNS")) with { RefreshSeconds = 10 }));

        Assert.NotEqual(before.GetProperty("bv").GetString(), withdrawn.GetProperty("bv").GetString());
        Assert.Equal(before.GetProperty("hv").GetString(), withdrawn.GetProperty("hv").GetString());
        Assert.Equal(before.GetProperty("layout").GetString(), withdrawn.GetProperty("layout").GetString());
        // Another refresh interval needs the page's script itself: the page reloads.
        Assert.NotEqual(withdrawn.GetProperty("layout").GetString(), slower.GetProperty("layout").GetString());
    }

    [Fact]
    public void Data_IsAsciiAndCannotCloseAScript()
    {
        var bytes = Writer.WriteData(Document(Runner("</script><b>Ґава Ївга", "MP")));
        Assert.All(bytes, b => Assert.True(b < 0x80));
        var js = Encoding.ASCII.GetString(bytes);
        Assert.StartsWith("orientPyxMonitor({", js);
        Assert.DoesNotContain("</script", js, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;/script&gt;&lt;b&gt;Ґава Ївга", Data(bytes).GetProperty("body").GetString());
    }

    /// <summary>
    /// The real thing in a headless Edge/Chrome: a page opened from disk loads its data file (a file:// script), swaps
    /// in the new rows — a withdrawal and runners added to the group above — and keeps the screen on the group it was
    /// showing, at the same offset into it.
    /// </summary>
    [BrowserFact]
    public void OpenPage_SwapsInNewData_KeepingTheScreenOnItsGroup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orientpyx-monitor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var women = Enumerable.Range(1, 40).Select(i => Runner($"Учасниця {i}", "на дистанції")).ToArray();
            var before = Document([Runner("Іваненко Іван", "на дистанції")], women) with { ScrollSpeed = 0 };
            var after = Document(
                [Runner("Іваненко Іван", "DNS"), .. Enumerable.Range(1, 10).Select(i => Runner($"Новий {i}", "1:00:00"))],
                women) with { ScrollSpeed = 0 };

            // A probe on top of the page: scroll 40 px into «Ж21» at once, and after the first poll write down the
            // group at the top of the screen and how far into it the screen is.
            var page = Encoding.UTF8.GetString(Writer.Write(before, "rez01.data.js")).Replace("</body>", """
                <script>
                window.addEventListener('load', function(){
                  function sec(){ return document.querySelector('section[data-g="Ж21"]'); }
                  window.scrollTo(0, sec().getBoundingClientRect().top + window.pageYOffset + 40);
                  document.body.setAttribute('data-before', Math.round(-sec().getBoundingClientRect().top));
                  setTimeout(function(){
                    document.body.setAttribute('data-off', Math.round(-sec().getBoundingClientRect().top));
                  }, 5000);
                });
                </script>
                </body>
                """);
            File.WriteAllText(Path.Combine(dir, "rez01.html"), page, new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(dir, "rez01.data.js"), Writer.WriteData(after));

            var dom = BrowserFactAttribute.DumpDom(Path.Combine(dir, "rez01.html"), dir);

            Assert.Contains("Новий 10", dom); // the new rows are in
            // The screen stayed 40 px into «Ж21» (± a pixel of scroll rounding), though ten rows came in above it.
            var beforeOff = Attribute(dom, "data-before");
            var afterOff = Attribute(dom, "data-off");
            Assert.InRange(beforeOff, 39, 41);
            Assert.InRange(afterOff, beforeOff - 1, beforeOff + 1);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// With its data file the page never reloads (the data keeps it current); without one — only the page copied
    /// elsewhere — it reloads itself every «Оновлення», as before.
    /// </summary>
    [BrowserFact]
    public void OpenPage_ReloadsOnlyWithoutLiveData()
    {
        Assert.Equal(1, Loads(withData: true));
        Assert.True(Loads(withData: false) >= 2, "a page without its data file reloads itself");
    }

    // Opens a page refreshed every 3 s for 8 s of page time and counts how many times it loaded.
    private static int Loads(bool withData)
    {
        var dir = Path.Combine(Path.GetTempPath(), "orientpyx-monitor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var doc = Document(Runner("Іваненко Іван", "на дистанції")) with { RefreshSeconds = 3, ScrollSpeed = 0 };
            // A probe before the page's script counts the loads; from the second one on it switches the page's timers
            // off, so the page settles and the headless browser can hand the DOM over (it waits out every reload).
            var page = Encoding.UTF8.GetString(Writer.Write(doc, "rez01.data.js")).Replace("<script>\n(function(){", """
                <script>
                var n = 1;
                try { n = (+sessionStorage.getItem('loads') || 0) + 1; sessionStorage.setItem('loads', n); } catch (e) { n = -1; }
                document.body.setAttribute('data-loads', n);
                if (n >= 2) { window.setTimeout = window.setInterval = function(){ return 0; }; }
                </script>
                <script>
                (function(){
                """.ReplaceLineEndings("\n"));
            File.WriteAllText(Path.Combine(dir, "rez01.html"), page, new UTF8Encoding(false));
            if (withData)
                File.WriteAllBytes(Path.Combine(dir, "rez01.data.js"), Writer.WriteData(doc));

            return Attribute(BrowserFactAttribute.DumpDom(Path.Combine(dir, "rez01.html"), dir), "data-loads");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static int Attribute(string dom, string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(dom, name + "=\"(-?[0-9]+)\"");
        Assert.True(m.Success, $"{name} not found in the page");
        return int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static MonitorRow Runner(string name, string status) => new([name, status], Unplaced: status != "1:00:00");

    private static MonitorDocument Document(params MonitorRow[] men) => Document(men, []);

    private static MonitorDocument Document(MonitorRow[] men, MonitorRow[] women) => new(
        Title: "Екран 1",
        Subtitle: "Кубок",
        RefreshSeconds: 30, // the fallback reload never comes during a test
        ScrollSpeed: 55,
        Columns: [new MonitorColumn(ResultColumn.FullName, "ПІБ"), new MonitorColumn(ResultColumn.Status, "Статус")],
        Groups: women.Length == 0
            ? [new MonitorGroup("М21", "", men)]
            : [new MonitorGroup("М21", "", men), new MonitorGroup("Ж21", "", women)]);

    private static JsonElement Data(byte[] js)
    {
        var text = Encoding.ASCII.GetString(js).Trim();
        const string prefix = "orientPyxMonitor(";
        Assert.StartsWith(prefix, text);
        Assert.EndsWith(");", text);
        return JsonDocument.Parse(text[prefix.Length..^2]).RootElement.Clone();
    }
}

/// <summary>A test that needs a Chromium browser (Edge or Chrome, or <c>ORIENTPYX_BROWSER</c>); skipped without one.</summary>
public sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute()
    {
        if (Browser is null)
            Skip = "No Edge/Chrome found (set ORIENTPYX_BROWSER to a Chromium executable).";
    }

    public static string? Browser { get; } = new[]
        {
            Environment.GetEnvironmentVariable("ORIENTPYX_BROWSER"),
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
        }
        .FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));

    /// <summary>Opens <paramref name="htmlPath"/> headless, lets 8 s of page time pass and returns the DOM then.</summary>
    public static string DumpDom(string htmlPath, string workDir)
    {
        var start = new ProcessStartInfo(Browser!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, // the DOM comes out as UTF-8, whatever the console's code page
            UseShellExecute = false,
        };
        foreach (var arg in new[]
        {
            "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
            "--user-data-dir=" + Path.Combine(workDir, "profile"), "--window-size=1024,600",
            "--virtual-time-budget=8000", "--dump-dom", new Uri(htmlPath).AbsoluteUri,
        })
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The headless browser did not finish in 60 s.");
        }
        return output.Result;
    }
}
