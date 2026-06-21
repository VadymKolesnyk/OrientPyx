using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.DataAccess.Documents;

/// <summary>
/// Renders a <see cref="ResultProtocolDocument"/> to a Word (.docx) file via the Open XML SDK. Lays the
/// protocol out like the classic printed sheet: a centred title block (organisation, then the bold title,
/// then a date-left / type-centre / venue-right line), then one block per group — a bold group caption, a
/// course sub-caption, and a results table. Only the table's header row is boxed; the data rows are
/// border-less. The whole document uses Times New Roman (set once as the document default so even empty
/// cells inherit it). Page size and orientation come from the document. Lives in DataAccess because the
/// document library must not be referenced from BusinessLogic (mirrors how the .xlsx writer is split out).
/// </summary>
public sealed class DocxResultProtocolWriter : IResultProtocolWriter
{
    // A4 in twentieths of a point (twips): 210 × 297 mm → 11906 × 16838.
    private const uint A4WidthTwips = 11906;
    private const uint A4HeightTwips = 16838;

    private const string FontName = "Times New Roman";

    public byte[] Write(ResultProtocolDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var stream = new MemoryStream();
        using (var word = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = word.AddMainDocumentPart();
            AppendStyles(main);

            var body = new Body();

            AppendHeader(body, document, RightTabPosition(document.Orientation));

            // Column widths are computed once from the content across ALL sections, so every group's table
            // uses the same column widths (sized to content, equal between groups) rather than each table
            // auto-fitting on its own and ending up different widths.
            var columnWidths = ComputeColumnWidths(document);

            foreach (var section in document.Sections)
                AppendSection(body, document, section, columnWidths);

            AppendOfficials(body, document, RightTabPosition(document.Orientation));

            body.Append(PageSettings(document.Orientation));
            main.Document = new Document(body);
            main.Document.Save();
        }

        return stream.ToArray();
    }

    // Document defaults: Times New Roman at the compact table size, applied to EVERY run/paragraph unless
    // overridden — so empty table cells (which carry no run) still render in the right font and size rather
    // than Word's Calibri 11/12 default.
    private static void AppendStyles(MainDocumentPart main)
    {
        var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
        var runDefaults = new RunPropertiesBaseStyle(
            new RunFonts { Ascii = FontName, HighAnsi = FontName, ComplexScript = FontName },
            new FontSize { Val = TableFontHalfPoints },
            new FontSizeComplexScript { Val = TableFontHalfPoints });
        var paraDefaults = new ParagraphPropertiesBaseStyle(
            new SpacingBetweenLines { Before = "0", After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto });
        stylesPart.Styles = new Styles(new DocDefaults(
            new RunPropertiesDefault(runDefaults),
            new ParagraphPropertiesDefault(paraDefaults)));
        stylesPart.Styles.Save();
    }

    // The header block: organisation/club (line 0), competition name (line 1), the bold title (line 2), then a
    // date-left / competition-type-centre / venue-right line (line 3) using a centre + right tab stop. Each
    // text field may itself span multiple lines (the user can press Enter in the header inputs).
    private static void AppendHeader(Body body, ResultProtocolDocument doc, int rightTabTwips)
    {
        if (doc.Subtitle.Length > 0)
            body.Append(CentredParagraph(doc.Subtitle, bold: false, sizeHalfPoints: 24));
        if (doc.CompetitionName.Length > 0)
            body.Append(CentredParagraph(doc.CompetitionName, bold: false, sizeHalfPoints: 24));
        if (doc.Title.Length > 0)
            body.Append(CentredParagraph(doc.Title, bold: true, sizeHalfPoints: 28));

        if (doc.DateText.Length > 0 || doc.CompetitionType.Length > 0 || doc.Venue.Length > 0)
        {
            // One paragraph, three fields: date at the left margin, type centred (centre tab at mid-width),
            // venue flush right (right tab at the right margin).
            var p = new Paragraph(new ParagraphProperties(new Tabs(
                new TabStop { Val = TabStopValues.Center, Position = rightTabTwips / 2 },
                new TabStop { Val = TabStopValues.Right, Position = rightTabTwips })));
            p.Append(Run(doc.DateText));
            p.Append(new Run(new TabChar()));
            p.Append(Run(doc.CompetitionType));
            p.Append(new Run(new TabChar()));
            p.Append(Run(doc.Venue));
            body.Append(p);
        }

        // A little breathing room before the first group.
        body.Append(new Paragraph());
    }

    private static void AppendSection(Body body, ResultProtocolDocument doc, ResultProtocolSection section,
        int[] columnWidths)
    {
        // Group caption (bold). The course-setter (начальник дистанції), when set, sits on the same line,
        // pushed toward the centre with a tab stop — matching the printed sheet's "group … course-setter" row.
        if (section.CourseSetterText.Length > 0)
        {
            var rightTab = RightTabPosition(doc.Orientation);
            var caption = new Paragraph(new ParagraphProperties(
                new SpacingBetweenLines { After = "40" },
                new Tabs(new TabStop { Val = TabStopValues.Left, Position = rightTab / 2 })));
            caption.Append(BoldRun(section.GroupName));
            caption.Append(new Run(new TabChar()));
            caption.Append(BoldRun(section.CourseSetterText));
            body.Append(caption);
        }
        else
        {
            body.Append(CaptionParagraph(section.GroupName, bold: true));
        }

        // Course sub-caption: length · control count · time limit — only the parts that are known.
        var parts = new[] { section.DistanceText, section.ControlCountText, section.TimeLimitText }
            .Where(s => s.Length > 0);
        var sub = string.Join("    ", parts);
        if (sub.Length > 0)
            body.Append(CaptionParagraph(sub, bold: false));

        body.Append(BuildTable(doc.ColumnHeaders, section.Rows, columnWidths));

        // Gap after the table before the next group.
        body.Append(new Paragraph());
    }

    // ── Content-fit, group-equal column widths ───────────────────────────────────────────────────────────

    // Per-column width in twips: sized to the widest content in that column (header + every cell of every
    // section), then scaled so the total exactly fills the printable page width. Computed once for the whole
    // document, so all group tables share identical column widths.
    private static int[] ComputeColumnWidths(ResultProtocolDocument doc)
    {
        var count = doc.ColumnHeaders.Count;
        if (count == 0)
            return [];

        // Longest text (in characters) seen in each column, across the header and all rows of all sections.
        var maxChars = new int[count];
        for (var c = 0; c < count; c++)
            maxChars[c] = doc.ColumnHeaders[c]?.Length ?? 0;
        foreach (var section in doc.Sections)
            foreach (var row in section.Rows)
                for (var c = 0; c < count && c < row.Cells.Count; c++)
                    maxChars[c] = Math.Max(maxChars[c], row.Cells[c]?.Length ?? 0);

        // Natural width per column from its character count: a rough average glyph advance for the 9.5 pt
        // table font, plus the cell's left+right padding, with a sane minimum so a 1-char column is tappable.
        const int charTwips = 95;   // ≈ average glyph advance at 9.5 pt
        const int padTwips = 80;    // 40 left + 40 right (matches TableCellMarginDefault)
        const int minColTwips = 360;
        var natural = new int[count];
        long total = 0;
        for (var c = 0; c < count; c++)
        {
            natural[c] = Math.Max(minColTwips, maxChars[c] * charTwips + padTwips);
            total += natural[c];
        }

        // Scale to fill the printable width exactly (page width − left/right margins of 720 each).
        var pageWidth = doc.Orientation == ProtocolOrientation.Landscape ? A4HeightTwips : A4WidthTwips;
        var printable = (int)pageWidth - 720 - 720;
        var widths = new int[count];
        long assigned = 0;
        for (var c = 0; c < count; c++)
        {
            widths[c] = total > 0 ? (int)((long)natural[c] * printable / total) : printable / count;
            assigned += widths[c];
        }
        // Give any rounding remainder to the first (widest, name) column so the row spans the full width.
        widths[0] += printable - (int)assigned;
        return widths;
    }

    // Compact table font: 9.5 pt (half-points), to match the dense printed protocol.
    private const string TableFontHalfPoints = "19";

    private static Table BuildTable(IReadOnlyList<string> headers, IReadOnlyList<ResultProtocolBodyRow> rows,
        int[] columnWidths)
    {
        var totalWidth = columnWidths.Sum();
        var table = new Table();
        // No table-level borders — only the header row is boxed (see the header cells below). TableProperties
        // child order matters: layout/width before margins (schema CT_TblPrBase). A FIXED layout makes Word
        // honour the explicit column widths verbatim (so every group's table lines up identically) instead of
        // auto-fitting each table to its own content.
        table.AppendChild(new TableProperties(
            new TableLayout { Type = TableLayoutValues.Fixed },
            new TableWidth { Width = totalWidth.ToString(), Type = TableWidthUnitValues.Dxa },
            new TableBorders(
                new TopBorder { Val = BorderValues.None },
                new LeftBorder { Val = BorderValues.None },
                new BottomBorder { Val = BorderValues.None },
                new RightBorder { Val = BorderValues.None },
                new InsideHorizontalBorder { Val = BorderValues.None },
                new InsideVerticalBorder { Val = BorderValues.None }),
            // Tight default cell padding (≈0.7 mm left/right) so rows stay compact.
            new TableCellMarginDefault(
                new TableCellLeftMargin { Width = 40, Type = TableWidthValues.Dxa },
                new TableCellRightMargin { Width = 40, Type = TableWidthValues.Dxa })));

        // A table grid (one column per header, with the shared computed width) must follow the properties
        // before any row.
        var grid = new TableGrid();
        for (var i = 0; i < headers.Count; i++)
            grid.Append(new GridColumn { Width = columnWidths[i].ToString() });
        table.Append(grid);

        // Header row (bold, boxed).
        var headerRow = new TableRow();
        for (var i = 0; i < headers.Count; i++)
            headerRow.Append(Cell(headers[i], bold: true, boxed: true, columnWidths[i]));
        table.Append(headerRow);

        foreach (var row in rows)
        {
            var tr = new TableRow();
            // A team caption row reads as one team: its cells are bold (the team place/score stand out).
            var bold = row.IsTeamHeader;
            // Pad/truncate to the header width so a malformed row can't shift the columns.
            for (var i = 0; i < headers.Count; i++)
                tr.Append(Cell(i < row.Cells.Count ? row.Cells[i] : string.Empty, bold, boxed: false, columnWidths[i]));
            table.Append(tr);
        }

        return table;
    }

    // One table cell. Font/size come from the document default (Times New Roman at the table size), so an
    // empty cell still renders correctly; the run only adds bold when needed. Only header cells are boxed —
    // <paramref name="boxed"/> draws a single border on all four sides; data cells stay border-less.
    private static TableCell Cell(string text, bool bold, bool boxed, int widthTwips)
    {
        var run = new Run(new Text(text ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve });
        if (bold)
            run.RunProperties = new RunProperties(new Bold());

        // The explicit cell width (Dxa) is required under a fixed table layout so the column keeps its
        // computed, group-equal width regardless of content.
        var cellProps = new TableCellProperties(
            new TableCellWidth { Width = widthTwips.ToString(), Type = TableWidthUnitValues.Dxa });
        if (boxed)
            cellProps.Append(new TableCellBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 }));

        return new TableCell(cellProps, new Paragraph(run));
    }

    private static Paragraph CentredParagraph(string text, bool bold, int sizeHalfPoints)
    {
        var run = new Run();
        // Run-property child order matters: <w:b> before <w:sz> (schema CT_RPr order).
        var props = new RunProperties();
        if (bold)
            props.Append(new Bold());
        props.Append(new FontSize { Val = sizeHalfPoints.ToString() });
        run.RunProperties = props;
        AppendMultilineText(run, text);
        return new Paragraph(
            new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
            run);
    }

    // Fills a run with the text, turning each embedded newline (the user pressed Enter in a header input) into
    // a <w:br/> so multi-line header fields render as multiple lines in Word. CRLF and lone LF both split.
    private static void AppendMultilineText(Run run, string text)
    {
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                run.Append(new Break());
            run.Append(new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
        }
    }

    // The trailing officials signature block (chief judge / secretary / jury). Each line reads
    // "<role>        <name (category)>" with the role at a left indent and the name aligned to a centre tab,
    // mirroring the printed protocol's signature rows. Nothing is printed when there are no officials.
    private static void AppendOfficials(Body body, ResultProtocolDocument doc, int rightTabTwips)
    {
        if (doc.Officials.Count == 0)
            return;

        // A little breathing room above the block.
        body.Append(new Paragraph());

        // Role at ~1/5 of the printable width, the name at the centre tab — gives the wide gap on the page.
        var roleIndent = rightTabTwips / 5;
        var nameTab = rightTabTwips / 2;
        foreach (var official in doc.Officials)
        {
            var p = new Paragraph(new ParagraphProperties(
                new SpacingBetweenLines { Before = "60", After = "60" },
                new Indentation { Left = roleIndent.ToString() },
                new Tabs(new TabStop { Val = TabStopValues.Left, Position = nameTab })));
            p.Append(Run(official.Role));
            p.Append(new Run(new TabChar()));
            p.Append(Run(official.NameWithCategory));
            body.Append(p);
        }
    }

    private static Run BoldRun(string text)
    {
        var run = Run(text);
        run.RunProperties = new RunProperties(new Bold());
        return run;
    }

    private static Paragraph CaptionParagraph(string text, bool bold)
    {
        var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        if (bold)
            run.RunProperties = new RunProperties(new Bold());
        return new Paragraph(
            new ParagraphProperties(new SpacingBetweenLines { After = "40" }),
            run);
    }

    private static Run Run(string text)
    {
        var run = new Run();
        AppendMultilineText(run, text);
        return run;
    }

    // The right-tab position (twips) at the right text margin = page width − left/right margins (720 each),
    // so the venue lands flush right of the printable area in the chosen orientation.
    private static int RightTabPosition(ProtocolOrientation orientation)
    {
        var pageWidth = orientation == ProtocolOrientation.Landscape ? A4HeightTwips : A4WidthTwips;
        return (int)pageWidth - 720 - 720;
    }

    // The final sectPr: A4 in the chosen orientation (landscape swaps width/height and sets the flag).
    private static SectionProperties PageSettings(ProtocolOrientation orientation)
    {
        var landscape = orientation == ProtocolOrientation.Landscape;
        var size = landscape
            ? new PageSize { Width = A4HeightTwips, Height = A4WidthTwips, Orient = PageOrientationValues.Landscape }
            : new PageSize { Width = A4WidthTwips, Height = A4HeightTwips };
        return new SectionProperties(
            size,
            new PageMargin { Top = 720, Bottom = 720, Left = 720, Right = 720, Header = 0, Footer = 0, Gutter = 0 });
    }
}
