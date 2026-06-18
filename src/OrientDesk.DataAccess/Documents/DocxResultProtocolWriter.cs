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

            foreach (var section in document.Sections)
                AppendSection(body, document, section);

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

    // The header block: organisation/club (line 1), the bold title (line 2), then a date-left /
    // competition-type-centre / venue-right line (line 3) using a centre + right tab stop.
    private static void AppendHeader(Body body, ResultProtocolDocument doc, int rightTabTwips)
    {
        if (doc.Subtitle.Length > 0)
            body.Append(CentredParagraph(doc.Subtitle, bold: false, sizeHalfPoints: 24));
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

    private static void AppendSection(Body body, ResultProtocolDocument doc, ResultProtocolSection section)
    {
        // Group caption (bold).
        body.Append(CaptionParagraph(section.GroupName, bold: true));

        // Course sub-caption: length · control count · time limit — only the parts that are known.
        var parts = new[] { section.DistanceText, section.ControlCountText, section.TimeLimitText }
            .Where(s => s.Length > 0);
        var sub = string.Join("    ", parts);
        if (sub.Length > 0)
            body.Append(CaptionParagraph(sub, bold: false));

        body.Append(BuildTable(doc.ColumnHeaders, section.Rows));

        // Gap after the table before the next group.
        body.Append(new Paragraph());
    }

    // Compact table font: 9.5 pt (half-points), to match the dense printed protocol.
    private const string TableFontHalfPoints = "19";

    private static Table BuildTable(IReadOnlyList<string> headers, IReadOnlyList<ResultProtocolBodyRow> rows)
    {
        var table = new Table();
        // No table-level borders — only the header row is boxed (see the header cells below). TableProperties
        // child order matters: width before margins (schema CT_TblPrBase).
        table.AppendChild(new TableProperties(
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
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

        // A table grid (one column per header) must follow the properties before any row.
        var grid = new TableGrid();
        for (var i = 0; i < headers.Count; i++)
            grid.Append(new GridColumn());
        table.Append(grid);

        // Header row (bold, boxed).
        var headerRow = new TableRow();
        foreach (var caption in headers)
            headerRow.Append(Cell(caption, bold: true, boxed: true));
        table.Append(headerRow);

        foreach (var row in rows)
        {
            var tr = new TableRow();
            // A team caption row reads as one team: its cells are bold (the team place/score stand out).
            var bold = row.IsTeamHeader;
            // Pad/truncate to the header width so a malformed row can't shift the columns.
            for (var i = 0; i < headers.Count; i++)
                tr.Append(Cell(i < row.Cells.Count ? row.Cells[i] : string.Empty, bold, boxed: false));
            table.Append(tr);
        }

        return table;
    }

    // One table cell. Font/size come from the document default (Times New Roman at the table size), so an
    // empty cell still renders correctly; the run only adds bold when needed. Only header cells are boxed —
    // <paramref name="boxed"/> draws a single border on all four sides; data cells stay border-less.
    private static TableCell Cell(string text, bool bold, bool boxed)
    {
        var run = new Run(new Text(text ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve });
        if (bold)
            run.RunProperties = new RunProperties(new Bold());

        var cellProps = new TableCellProperties();
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
        var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        // Run-property child order matters: <w:b> before <w:sz> (schema CT_RPr order).
        var props = new RunProperties();
        if (bold)
            props.Append(new Bold());
        props.Append(new FontSize { Val = sizeHalfPoints.ToString() });
        run.RunProperties = props;
        return new Paragraph(
            new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
            run);
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
        => new(new Text(text ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve });

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
