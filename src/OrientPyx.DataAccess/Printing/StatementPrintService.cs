using System.Runtime.Versioning;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.DataAccess.Printing;

/// <summary>
/// Prints a participant statement («відомість») to an installed system printer on A4 via GDI
/// (<c>System.Drawing.Printing</c>). Renders the same <see cref="ResultProtocolDocument"/> the .docx export
/// produces — the centred header block, the applied-filters line, then the flat table (Times New Roman) whose
/// rows are already sorted by chip, with own-chip cells drawn bold. A long list flows onto more A4 pages, the
/// column header repeated at the top of each page. Windows-only at runtime (guarded by
/// <see cref="OperatingSystem.IsWindows"/>); <see cref="PrintAsync"/> throws <see cref="PrintNotSupportedException"/>
/// elsewhere so the UI can show a message.
/// </summary>
public sealed class StatementPrintService : IStatementPrintService
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public IReadOnlyList<string> GetInstalledPrinters()
    {
        if (!OperatingSystem.IsWindows())
            return [];
        return GetInstalledPrintersWindows();
    }

    public Task PrintAsync(
        ResultProtocolDocument document,
        A4PrintSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(settings);

        if (!OperatingSystem.IsWindows())
            throw new PrintNotSupportedException();

        // GDI printing is synchronous; keep it off the caller's thread so a slow spooler can't block the UI.
        return Task.Run(() =>
        {
            if (OperatingSystem.IsWindows())
                PrintWindows(document, settings);
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
    private static void PrintWindows(ResultProtocolDocument document, A4PrintSettings settings)
    {
        using var doc = new System.Drawing.Printing.PrintDocument();
        if (settings.HasPrinter)
            doc.PrinterSettings.PrinterName = settings.PrinterName;

        doc.DefaultPageSettings.PaperSize =
            new System.Drawing.Printing.PaperSize("A4", 827, 1169); // A4 in hundredths of an inch (210×297 mm)
        doc.DefaultPageSettings.Landscape = document.Orientation == ProtocolOrientation.Landscape;
        doc.DefaultPageSettings.Margins = new System.Drawing.Printing.Margins(50, 50, 50, 50); // 0.5" all round

        var renderer = new StatementRenderer(document);
        doc.PrintPage += renderer.OnPrintPage;
        doc.Print();
    }
}

/// <summary>
/// Draws a participant statement onto A4 pages: the centred header block (subtitle / competition name / bold
/// title / date·type·venue), the applied-filters line, then the flat table — a boxed header row followed by
/// border-less data rows. Column widths are content-sized then scaled to the printable width (same shape as the
/// .docx / preview), so the print matches. Own-chip cells (per <see cref="ResultProtocolBodyRow.BoldCells"/>)
/// are drawn bold. Holds a paint cursor across <see cref="OnPrintPage"/> calls so a long list flows onto more
/// pages, re-drawing the column header at the top of each.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class StatementRenderer
{
    private const string SerifFont = "Times New Roman";
    private const float BodyFontSize = 9.5f;
    private const float TitleFontSize = 13f;
    private const float CellPadX = 3f;   // horizontal text inset inside a cell
    private const float RowPadY = 2f;    // vertical inset above/below a row's text

    private readonly ResultProtocolDocument _doc;
    private readonly IReadOnlyList<ResultProtocolBodyRow> _rows;

    private float[] _columnWidths = [];
    private int _nextRow;
    private bool _widthsComputed;

    public StatementRenderer(ResultProtocolDocument doc)
    {
        _doc = doc;
        // A statement is a single flat section (the builder emits one).
        _rows = doc.Sections.Count > 0 ? doc.Sections[0].Rows : [];
    }

    public void OnPrintPage(object? sender, System.Drawing.Printing.PrintPageEventArgs e)
    {
        var g = e.Graphics!;
        var bounds = e.MarginBounds;
        float left = bounds.Left;
        float top = bounds.Top;
        float right = bounds.Right;
        float bottom = bounds.Bottom;
        var centreX = (left + right) / 2f;
        float y = top;

        using var titleFont = new System.Drawing.Font(SerifFont, TitleFontSize, System.Drawing.FontStyle.Bold);
        using var headFont = new System.Drawing.Font(SerifFont, BodyFontSize, System.Drawing.FontStyle.Regular);
        using var bodyFont = new System.Drawing.Font(SerifFont, BodyFontSize, System.Drawing.FontStyle.Regular);
        using var boldFont = new System.Drawing.Font(SerifFont, BodyFontSize, System.Drawing.FontStyle.Bold);

        if (!_widthsComputed)
        {
            _columnWidths = ComputeColumnWidths(g, headFont, right - left);
            _widthsComputed = true;
        }

        // The statement has no header block — only the applied-filters heading (centred, bold), printed once on
        // the first page over the table.
        if (_nextRow == 0)
        {
            if (_doc.FilterSummary.Length > 0)
            {
                y = DrawCentred(g, _doc.FilterSummary, titleFont, centreX, y);
                y += 6f;
            }
        }

        // Column header row (boxed), repeated at the top of each page.
        var rowHeight = bodyFont.GetHeight(g) + 2 * RowPadY;
        y = DrawHeaderRow(g, boldFont, left, y, rowHeight);

        for (; _nextRow < _rows.Count; _nextRow++)
        {
            if (y + rowHeight > bottom)
            {
                e.HasMorePages = true;
                return;
            }
            DrawBodyRow(g, _rows[_nextRow], bodyFont, boldFont, left, y, rowHeight);
            y += rowHeight;
        }

        e.HasMorePages = false;
    }

    // A boxed header row: each caption centred in its column, with a full cell border.
    private float DrawHeaderRow(System.Drawing.Graphics g, System.Drawing.Font font, float left, float y, float rowHeight)
    {
        float x = left;
        using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0x33, 0x33, 0x33), 0.7f);
        for (var c = 0; c < _doc.ColumnHeaders.Count && c < _columnWidths.Length; c++)
        {
            var w = _columnWidths[c];
            g.DrawRectangle(pen, x, y, w, rowHeight);
            DrawCellText(g, _doc.ColumnHeaders[c], font, x, y, w, rowHeight, centre: true);
            x += w;
        }
        return y + rowHeight;
    }

    // A body row: each cell left-aligned, drawn bold when its BoldCells mask (own chip) says so; a hairline
    // under the row keeps the table readable without a full grid.
    private void DrawBodyRow(System.Drawing.Graphics g, ResultProtocolBodyRow row,
        System.Drawing.Font font, System.Drawing.Font boldFont, float left, float y, float rowHeight)
    {
        float x = left;
        using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0xC8, 0xC8, 0xC8), 0.5f);
        for (var c = 0; c < _doc.ColumnHeaders.Count && c < _columnWidths.Length; c++)
        {
            var w = _columnWidths[c];
            var text = c < row.Cells.Count ? row.Cells[c] : string.Empty;
            var bold = row.IsTeamHeader || (row.BoldCells is { } mask && c < mask.Count && mask[c]);
            DrawCellText(g, text, bold ? boldFont : font, x, y, w, rowHeight, centre: false);
            x += w;
        }
        g.DrawLine(pen, left, y + rowHeight, x, y + rowHeight);
    }

    private static void DrawCellText(System.Drawing.Graphics g, string text, System.Drawing.Font font,
        float x, float y, float w, float rowHeight, bool centre)
    {
        var rect = new System.Drawing.RectangleF(x + CellPadX, y + RowPadY, w - 2 * CellPadX, rowHeight - 2 * RowPadY);
        var fmt = new System.Drawing.StringFormat
        {
            Alignment = centre ? System.Drawing.StringAlignment.Center : System.Drawing.StringAlignment.Near,
            LineAlignment = System.Drawing.StringAlignment.Center,
            Trimming = System.Drawing.StringTrimming.EllipsisCharacter,
            FormatFlags = System.Drawing.StringFormatFlags.NoWrap
        };
        g.DrawString(text ?? string.Empty, font, System.Drawing.Brushes.Black, rect, fmt);
    }

    // Content-proportional column widths: each column sized to the widest of its header caption and its cells,
    // then scaled to fill the printable width (or scaled DOWN when the natural widths overflow the page). Mirrors
    // the shape of the .docx/preview sizing so the printed table matches; a simple single-pass scale is enough
    // here since the print target is fixed A4.
    private float[] ComputeColumnWidths(System.Drawing.Graphics g, System.Drawing.Font font, float available)
    {
        var count = _doc.ColumnHeaders.Count;
        if (count == 0)
            return [];

        var natural = new float[count];
        for (var c = 0; c < count; c++)
        {
            var widest = g.MeasureString(_doc.ColumnHeaders[c], font).Width;
            foreach (var row in _rows)
            {
                if (c >= row.Cells.Count)
                    continue;
                var cw = g.MeasureString(row.Cells[c], font).Width;
                if (cw > widest)
                    widest = cw;
            }
            natural[c] = widest + 2 * CellPadX + 4f;
        }

        var total = natural.Sum();
        if (total <= 0)
            return natural;

        // Scale every column proportionally to exactly fill the printable width (up when narrow, down when wide).
        var scale = available / total;
        for (var c = 0; c < count; c++)
            natural[c] *= scale;
        return natural;
    }

    private static float DrawCentred(System.Drawing.Graphics g, string text, System.Drawing.Font font, float centreX, float y)
    {
        using var fmt = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center };
        g.DrawString(text, font, System.Drawing.Brushes.Black, centreX, y, fmt);
        return y + font.GetHeight(g) + 1.5f;
    }
}
