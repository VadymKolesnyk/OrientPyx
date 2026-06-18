using System.Runtime.Versioning;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.DataAccess.Printing;

/// <summary>
/// Prints split printouts to an installed system printer via GDI (<c>System.Drawing.Printing</c>). The
/// renderer lays the receipt out for a narrow thermal roll (56 or 80 mm): a centred header identifying the
/// runner, then the course passage in order as a monospace table. The driver handles the cut at the end of
/// the page; a "Print to PDF" printer instead prompts the OS save dialog — same code path.
///
/// Windows-only at runtime: every member that touches the spooler is guarded by
/// <see cref="OperatingSystem.IsWindows"/>, and <see cref="PrintAsync"/> throws
/// <see cref="PrintNotSupportedException"/> elsewhere so the UI can show a message.
/// </summary>
public sealed class SplitPrintService : ISplitPrintService
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public IReadOnlyList<string> GetInstalledPrinters()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        return GetInstalledPrintersWindows();
    }

    public Task PrintAsync(
        SplitPrintDocument document,
        SplitPrintLabels labels,
        PrintSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(settings);

        if (!OperatingSystem.IsWindows())
            throw new PrintNotSupportedException();

        // GDI printing is synchronous; keep it off the caller's thread so a slow spooler can't block the UI.
        return Task.Run(() =>
        {
            if (OperatingSystem.IsWindows())
                PrintWindows(document, labels, settings);
        }, cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> GetInstalledPrintersWindows()
    {
        var names = new List<string>();
        foreach (var name in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
            if (name is string s)
                names.Add(s);
        return names;
    }

    [SupportedOSPlatform("windows")]
    private static void PrintWindows(SplitPrintDocument document, SplitPrintLabels labels, PrintSettings settings)
    {
        using var doc = new System.Drawing.Printing.PrintDocument();
        if (settings.HasPrinter)
            doc.PrinterSettings.PrinterName = settings.PrinterName;

        // Roll width in hundredths of an inch (the printer's unit): 56 mm ≈ 215, 80 mm ≈ 302. A tall page
        // lets the whole receipt flow as one continuous strip; the driver cuts at the printed end.
        var widthHundredths = settings.WidthMm <= 56 ? 215 : 302;
        var paper = new System.Drawing.Printing.PaperSize("Receipt", widthHundredths, 3276); // ~33 in tall
        doc.DefaultPageSettings.PaperSize = paper;
        doc.DefaultPageSettings.Margins = new System.Drawing.Printing.Margins(8, 8, 8, 8);

        var renderer = new ReceiptRenderer(document, labels, settings.WidthMm);
        doc.PrintPage += renderer.OnPrintPage;
        doc.Print();
    }
}

/// <summary>
/// Draws one split-printout receipt onto a <see cref="System.Drawing.Printing.PrintDocument"/> page,
/// laid out to match the classic SportIdent split slip: a centred header block (printed-at, number+chip,
/// group+name, start/finish, result/length/pace) over a monospace passage table whose columns are
/// №ПП · КП · ЧАС (cumulative) · ЧАС (leg) · П.ДОВЖ (km) · ШВИДК (pace). Off-course punches keep a "*" in
/// the leftmost column. Holds the paint cursor across <see cref="OnPrintPage"/> calls so a long passage can
/// flow onto a second page (rare). Everything is centred within the printer's <b>printable</b> area, so the
/// non-printable hardware margins on the left/right of a thermal roll don't push the layout off-centre.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ReceiptRenderer
{
    // Extra horizontal inset (hundredths of an inch) added on each side for the 80 mm roll: ~5 mm, since
    // those printers can clip near the edges. 5 mm = 5 / 25.4 in ≈ 0.197 in ≈ 20 hundredths.
    private const float WideSideInset = 20f;

    private readonly SplitPrintDocument _doc;
    private readonly SplitPrintLabels _labels;
    private readonly int _widthMm;
    private int _nextRow;

    public ReceiptRenderer(SplitPrintDocument doc, SplitPrintLabels labels, int widthMm)
    {
        _doc = doc;
        _labels = labels;
        _widthMm = widthMm;
    }

    public void OnPrintPage(object? sender, System.Drawing.Printing.PrintPageEventArgs e)
    {
        var g = e.Graphics!;

        // Printable band: the page minus the printer's non-printable hardware margins (the edges a thermal
        // head physically cannot reach). Centring on this — not on the nominal paper width — keeps the slip
        // visually centred even when the left/right unprintable margins differ.
        var hardX = e.PageSettings.HardMarginX;
        var hardY = e.PageSettings.HardMarginY;
        // On the 80 mm roll add ~5 mm of slack on each side so edge-clipping printers don't cut the slip.
        var sideInset = _widthMm > 56 ? WideSideInset : 0f;
        float left = hardX + sideInset;

        // The right edge is the physical roll width (302/215 hundredths-inch), NOT PageBounds.Width: a
        // "Print to PDF" printer (or a driver that ignores the custom PaperSize) reports a far wider page,
        // which let the table draw past the roll's real edge — the overflow seen on the 80 mm slip. Clamp to
        // the smaller of the two so we never lay out wider than the paper actually is.
        var rollWidthHundredths = _widthMm > 56 ? 302f : 215f;
        float rollRight = rollWidthHundredths - hardX - sideInset;
        float right = Math.Min(e.PageBounds.Width - hardX - sideInset, rollRight);
        float centreX = (left + right) / 2f;
        float y = hardY + 4f;

        // Pick the largest font that still lets the widest table row fit the printable band, so the slip
        // never spills off the right edge (it did on 80 mm at the old fixed 8.5 pt). Capped at 8.5 pt so a
        // narrow table on a wide roll doesn't blow up; the search floor (6 pt) keeps it legible.
        var fontSize = FitFontSize(g, right - left);

        using var headFont = new System.Drawing.Font("Consolas", fontSize, System.Drawing.FontStyle.Bold);
        using var monoFont = new System.Drawing.Font("Consolas", fontSize);
        using var monoBold = new System.Drawing.Font("Consolas", fontSize, System.Drawing.FontStyle.Bold);

        // The first page draws the whole header; a continuation page (overflow) jumps straight to the rows.
        if (_nextRow == 0)
        {
            y = DrawHeader(g, centreX, y, headFont);
            y += 2;
            y = DrawColumnHeader(g, centreX, y, monoBold);
        }

        var rowHeight = monoFont.GetHeight(g) + 1.5f;
        float bottom = e.PageBounds.Height - hardY;
        for (; _nextRow < _doc.Rows.Count; _nextRow++)
        {
            if (y + rowHeight > bottom)
            {
                e.HasMorePages = true;
                return;
            }
            DrawRow(g, _doc.Rows[_nextRow], centreX, y, monoFont);
            y += rowHeight;
        }

        // Bottom section (set-course MP only): the prescribed correct order on one compact line, the
        // not-taken controls in bold. Printed once the whole passage has fit, below the table.
        if (_doc.CorrectOrder.Count > 0)
            DrawCorrectOrder(g, left, right, y, monoFont, monoBold);

        e.HasMorePages = false;
    }

    // The correct-order section: the title then the prescribed codes laid out on one line ("31 32 33 …"),
    // wrapping within the printable band when too long. Controls the runner did not take in order are drawn
    // bold so the missing ones stand out, while the taken ones stay normal weight.
    private void DrawCorrectOrder(System.Drawing.Graphics g, float left, float right, float y,
        System.Drawing.Font mono, System.Drawing.Font monoBold)
    {
        var centreX = (left + right) / 2f;
        y += 4;
        y = DrawCentred(g, _labels.CorrectOrderTitle, monoBold, centreX, y);

        // Lay tokens out left-to-right, wrapping to a new line when the next code would overflow the band.
        var space = g.MeasureString(" ", mono).Width;
        var lineHeight = mono.GetHeight(g) + 1.5f;
        float x = left;
        foreach (var c in _doc.CorrectOrder)
        {
            var font = c.Taken ? mono : monoBold;
            var w = g.MeasureString(c.Code, font).Width;
            if (x > left && x + w > right)   // wrap: this code won't fit on the current line
            {
                x = left;
                y += lineHeight;
            }
            g.DrawString(c.Code, font, System.Drawing.Brushes.Black, x, y);
            x += w + space;
        }
    }

    // The centred header block, drawn line by line. Only non-empty lines consume vertical space.
    private float DrawHeader(System.Drawing.Graphics g, float centreX, float y, System.Drawing.Font font)
    {
        // 1) Printed-at date + time.
        y = DrawCentred(g, $"{_doc.PrintedAt:dd.MM.yyyy HH:mm:ss}", font, centreX, y);

        // 2) №<number>  ЧІП №<chip> (+ "орендований" when a rental chip).
        var idParts = new List<string>(2);
        if (_doc.Number.Length > 0)
            idParts.Add($"№{_doc.Number}");
        if (_doc.ChipNumber.Length > 0)
        {
            var chip = $"{_labels.ChipLabel}{_doc.ChipNumber}";
            if (_doc.IsRentalChip)
                chip += $" ({_labels.RentalChipLabel})";
            idParts.Add(chip);
        }
        if (idParts.Count > 0)
            y = DrawCentred(g, string.Join("  ", idParts), font, centreX, y);

        // 3) <group>  <name>.
        var who = string.Join("  ", new[] { _doc.GroupName, _doc.FullName }.Where(s => s.Length > 0));
        if (who.Length > 0)
            y = DrawCentred(g, who, font, centreX, y);

        // 4) СТАРТ <hh:mm:ss> ФІНІШ <hh:mm:ss.f>.
        var times = new List<string>(2);
        if (_doc.StartClock.Length > 0)
            times.Add($"{_labels.StartLabel} {_doc.StartClock}");
        if (_doc.FinishClock.Length > 0)
            times.Add($"{_labels.FinishLabel} {_doc.FinishClock}");
        if (times.Count > 0)
            y = DrawCentred(g, string.Join(" ", times), font, centreX, y);

        // 5) РЕЗ. <result> ДЛ. <km> СК.<pace> — plus the status when it isn't a plain OK.
        var summary = new List<string>(4);
        if (_doc.ResultText.Length > 0)
            summary.Add($"{_labels.ResultLabel} {_doc.ResultText}");
        if (_doc.TotalDistanceText.Length > 0)
            summary.Add($"{_labels.DistanceLabel} {_doc.TotalDistanceText}");
        if (_doc.AvgPaceText.Length > 0)
            summary.Add($"{_labels.AvgPaceLabel}{_doc.AvgPaceText}");
        if (summary.Count > 0)
            y = DrawCentred(g, string.Join(" ", summary), font, centreX, y);

        // Status when it isn't a plain OK; for MP we also spell out what it means (the missing/out-of-order
        // control), so the slip reads e.g. "Статус: MP - Бракує КП або порушено порядок: 33".
        if (_doc.StatusText.Length > 0 && _doc.StatusText != "OK")
        {
            var status = $"{_labels.StatusLabel}: {_doc.StatusText}";
            if (_doc.StatusDetail.Length > 0)
                status += $" - {string.Format(_labels.MpDetailLabel, _doc.StatusDetail)}";
            y = DrawCentred(g, status, font, centreX, y);
        }

        // Total points (rogaine) on its own line, mirroring the status line: "Сума балів: 12". When an
        // over-time penalty applies, spell out the breakdown "Сума балів: X - Y = Z" (gross − penalty = net).
        if (_doc.TotalPointsText.Length > 0)
        {
            var points = _doc.PenaltyText.Length > 0
                ? $"{_doc.GrossPointsText} - {_doc.PenaltyText} = {_doc.TotalPointsText}"
                : _doc.TotalPointsText;
            y = DrawCentred(g, $"{_labels.TotalPointsLabel}: {points}", font, centreX, y);
        }

        return y;
    }

    // The monospace column header, centred on the same width as the rows so the table sits in the middle.
    // Scored disciplines (rogaine) get their own compact header — №ПП КП БАЛ ЧАС — with the points column
    // pulled in right after the code (no off-course flag, no leg/distance/pace columns). The set-course
    // header keeps the full layout; its leg-time header ("ЧАС") is nudged one position left vs its
    // right-aligned data slot (per request) so it doesn't crowd the distance header.
    private float DrawColumnHeader(System.Drawing.Graphics g, float centreX, float y, System.Drawing.Font mono)
    {
        // Compact scored layout (geometry-less choice/score formats): №ПП КП БАЛ ЧАС.
        if (_doc.HasPoints && !_doc.HasGeometry)
        {
            var sSeq = Pad(_labels.ColSeq, 3);
            var sCode = PadRight(_labels.ColCode, 4);
            var sPts = PadRight(_labels.ColPoints, 5);
            var sElapsed = Pad(_labels.ColElapsed, 7);
            return DrawMono(g, $"{sSeq} {sCode} {sPts} {sElapsed}", mono, centreX, y);
        }

        // Right-align each header into its column width, but for the leg slot shift one char left: pad it to
        // width-1 (so it sits one space earlier) then add the missing space back, keeping the row width.
        var seq = Pad(_labels.ColSeq, 3);
        var code = PadRight(_labels.ColCode, 4);
        // Rogaine (HasGeometry + points) keeps the full layout but adds a бал column right after the code.
        var pts = _doc.HasGeometry ? PadRight(_labels.ColPoints, 5) + " " : string.Empty;
        var elapsed = Pad(_labels.ColElapsed, 7);
        var leg = Pad(_labels.ColLeg, 5) + " ";          // shifted left one position within the 6-wide slot
        var dist = Pad(_labels.ColDistance, 6);
        var pace = Pad(_labels.ColPace, 6);
        var header = $"{seq} {code} {pts}{elapsed} {leg} {dist} {pace}";
        // The header line has no off-course flag, so it gets the same one-space lead as on-course rows.
        return DrawMono(g, " " + header, mono, centreX, y);
    }

    // Largest Consolas size (≤ 8.5 pt, ≥ 6 pt) at which the widest fixed-width table line still fits the
    // printable band. Consolas is monospace, so the table's width is driven by its longest row; we measure
    // that one string at decreasing sizes and stop at the first that fits. Prevents the 80 mm overflow.
    private float FitFontSize(System.Drawing.Graphics g, float available)
    {
        var widest = WidestTableLine();
        for (var size = 8.5f; size > 6f; size -= 0.25f)
        {
            using var f = new System.Drawing.Font("Consolas", size);
            if (g.MeasureString(widest, f).Width <= available)
                return size;
        }
        return 6f;
    }

    // The longest fixed-width line the table will draw, so FitFontSize can size to it. Headers and rows all
    // share the same column layout (same total character count), so any full row works — we build one from
    // the column header (it always uses every column) plus the leading off-course flag column. Scored
    // (rogaine) docs use the compact №ПП КП БАЛ ЧАС layout with no flag/leg/distance/pace columns.
    private string WidestTableLine()
    {
        if (_doc.HasPoints && !_doc.HasGeometry)
            return $"{Pad(_labels.ColSeq, 3)} {PadRight(_labels.ColCode, 4)} {PadRight(_labels.ColPoints, 5)} {Pad(_labels.ColElapsed, 7)}";

        var seq = Pad(_labels.ColSeq, 3);
        var code = PadRight(_labels.ColCode, 4);
        // Rogaine adds a бал column to the full layout (see DrawColumnHeader/DrawRow).
        var pts = _doc.HasGeometry ? PadRight(_labels.ColPoints, 5) + " " : string.Empty;
        var elapsed = Pad(_labels.ColElapsed, 7);
        var leg = Pad(_labels.ColLeg, 6);
        var dist = Pad(_labels.ColDistance, 6);
        var pace = Pad(_labels.ColPace, 6);
        return $"*{seq} {code} {pts}{elapsed} {leg} {dist} {pace}";
    }

    private void DrawRow(System.Drawing.Graphics g, SplitPrintRow row, float centreX, float y, System.Drawing.Font mono)
    {
        // Compact scored layout (geometry-less choice/score formats): №ПП КП БАЛ ЧАС — the points the runner
        // earned ("+3") in the БАЛ slot instead of an off-course "*" flag, the number/code shifted left.
        if (_doc.HasPoints && !_doc.HasGeometry)
        {
            var scored = $"{Pad(row.Index, 3)} {PadRight(row.Code, 4)} {PadRight(row.PointsText ?? string.Empty, 5)} {Pad(row.ElapsedText, 7)}";
            DrawMono(g, scored, mono, centreX, y);
            return;
        }

        var line = Compose(row.Index, row.Code, row.PointsText, row.ElapsedText, row.LegText, row.DistanceText, row.PaceText);
        // Off-course punches are marked with a leading "*" on the left so they stand out on a mono receipt.
        var prefix = row.OnCourse ? " " : "*";
        DrawMono(g, prefix + line, mono, centreX, y);
    }

    // Builds a fixed-width set-course row string: seq(3) code(4) [бал(5) ]elapsed(7) leg(6) dist(6) pace(6).
    // The бал column is present only for rogaine (HasGeometry + points); set course passes a null pointsText.
    private string Compose(string seq, string code, string? pointsText, string elapsed, string leg, string dist, string pace)
    {
        var pts = _doc.HasGeometry ? PadRight(pointsText ?? string.Empty, 5) + " " : string.Empty;
        return $"{Pad(seq, 3)} {PadRight(code, 4)} {pts}{Pad(elapsed, 7)} {Pad(leg, 6)} {Pad(dist, 6)} {Pad(pace, 6)}";
    }

    // Draws a monospace line centred on its own measured width (so header and rows share an axis).
    private static float DrawMono(System.Drawing.Graphics g, string text, System.Drawing.Font font, float centreX, float y)
    {
        var w = g.MeasureString(text, font).Width;
        g.DrawString(text, font, System.Drawing.Brushes.Black, centreX - w / 2f, y);
        return y + font.GetHeight(g) + 1.5f;
    }

    // Draws a proportional (header) line centred on centreX.
    private static float DrawCentred(System.Drawing.Graphics g, string text, System.Drawing.Font font, float centreX, float y)
    {
        var fmt = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center };
        g.DrawString(text, font, System.Drawing.Brushes.Black, centreX, y, fmt);
        return y + font.GetHeight(g) + 1.5f;
    }

    private static string Pad(string s, int width) => (s ?? string.Empty).Length >= width
        ? s![..width]
        : (s ?? string.Empty).PadLeft(width);

    private static string PadRight(string s, int width) => (s ?? string.Empty).Length >= width
        ? s![..width]
        : (s ?? string.Empty).PadRight(width);
}
