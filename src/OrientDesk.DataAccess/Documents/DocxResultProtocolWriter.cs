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
            // auto-fitting on its own and ending up different widths. The same pass also decides, per column,
            // whether the full or the abbreviated caption is used (a narrow column falls back to the short one).
            var columnWidths = ComputeColumnWidths(document, out var headers);

            // Banded layout (the judges' start protocol): the column header is printed ONCE at the top and every
            // section is a shaded full-width caption band followed by its rows, all in a single continuous table —
            // so the sheet reads as one running list with start-minute bands, matching the classic printed form.
            // Otherwise (results / regular start): one self-contained table per group, header repeated each time.
            if (document.Sections.Count > 0 && document.Sections.All(s => s.IsBanded))
                AppendBandedTable(body, document, columnWidths, headers, document.ColumnBodyWrap);
            else
                foreach (var section in document.Sections)
                    AppendSection(body, document, section, columnWidths, headers, document.ColumnBodyWrap);

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
        int[] columnWidths, IReadOnlyList<string> headers, IReadOnlyList<bool> bodyWrap)
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

        body.Append(BuildTable(headers, section.Rows, columnWidths, bodyWrap));

        // Gap after the table before the next group.
        body.Append(new Paragraph());
    }

    // The banded layout (judges' start protocol): one continuous table — a single boxed header row, then for each
    // section a shaded caption band (a single cell spanning every column) followed by that section's data rows.
    private static void AppendBandedTable(Body body, ResultProtocolDocument doc, int[] columnWidths,
        IReadOnlyList<string> headers, IReadOnlyList<bool> bodyWrap)
    {
        var totalWidth = columnWidths.Sum();
        var table = new Table();
        table.AppendChild(new TableProperties(
            new TableLayout { Type = TableLayoutValues.Fixed },
            new TableWidth { Width = totalWidth.ToString(), Type = TableWidthUnitValues.Dxa },
            // The whole banded table is boxed (outer + inside rules) so the bands and rows sit in a real grid,
            // like the printed judges' sheet.
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }),
            new TableCellMarginDefault(
                new TableCellLeftMargin { Width = 40, Type = TableWidthValues.Dxa },
                new TableCellRightMargin { Width = 40, Type = TableWidthValues.Dxa })));

        var grid = new TableGrid();
        for (var i = 0; i < headers.Count; i++)
            grid.Append(new GridColumn { Width = columnWidths[i].ToString() });
        table.Append(grid);

        // Single header row (bold, boxed), printed once at the very top.
        var headerRow = new TableRow();
        for (var i = 0; i < headers.Count; i++)
            headerRow.Append(Cell(headers[i], bold: true, boxed: true, columnWidths[i], noWrap: false));
        table.Append(headerRow);

        foreach (var section in doc.Sections)
        {
            // The caption band: one bold, centred, shaded cell spanning every column.
            table.Append(BandRow(section.GroupName, headers.Count, totalWidth));

            foreach (var row in section.Rows)
            {
                var tr = new TableRow();
                var bold = row.IsTeamHeader;
                for (var i = 0; i < headers.Count; i++)
                {
                    var noWrap = !(i < bodyWrap.Count && bodyWrap[i]);
                    tr.Append(Cell(i < row.Cells.Count ? row.Cells[i] : string.Empty, bold, boxed: false,
                        columnWidths[i], noWrap));
                }
                table.Append(tr);
            }
        }

        body.Append(table);
        body.Append(new Paragraph());
    }

    // Caption-band shading colour — a light grey fill so the band reads as a divider without overpowering the text.
    private const string BandShade = "D9D9D9";

    // A full-width caption band row: a single cell merged across all columns, bold + centred, with a grey fill.
    private static TableRow BandRow(string caption, int columnCount, int totalWidth)
    {
        var run = new Run(new Text(caption ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve })
        {
            RunProperties = new RunProperties(new Bold())
        };
        var cellProps = new TableCellProperties(
            new TableCellWidth { Width = totalWidth.ToString(), Type = TableWidthUnitValues.Dxa },
            // Span every column.
            new HorizontalMerge { Val = MergedCellValues.Restart },
            new Shading { Val = ShadingPatternValues.Clear, Fill = BandShade });
        var cell = new TableCell(cellProps,
            new Paragraph(
                new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
                run));

        var rowChildren = new List<TableCell> { cell };
        // The remaining columns are empty continuation cells of the horizontal merge.
        for (var i = 1; i < columnCount; i++)
            rowChildren.Add(new TableCell(
                new TableCellProperties(
                    new HorizontalMerge { Val = MergedCellValues.Continue },
                    new Shading { Val = ShadingPatternValues.Clear, Fill = BandShade }),
                new Paragraph()));

        var tr = new TableRow();
        foreach (var c in rowChildren)
            tr.Append(c);
        return tr;
    }

    // ── Content-fit, group-equal column widths ───────────────────────────────────────────────────────────

    // Per-column width in twips: sized to the widest content in that column, then scaled to exactly fill the
    // printable page width. Computed once for the whole document, so all group tables share identical widths.
    // Also decides, per column, whether to print the FULL or the abbreviated caption — <paramref name="headers"/>
    // gets the chosen caption text for each column. The char counts per column:
    //  • dataChars       — longest BODY cell. Inviolable: data must never wrap.
    //  • shortWordChars  — the short caption's longest word (capped). Also inviolable: the abbreviation is the
    //    fallback, so it must always fit on one line.
    //  • fullWordChars   — the full caption's longest word (capped). PREFERRED: fit it when there's room so the
    //    full header shows; otherwise the column falls back to the short caption.
    //  • naturalChars    — full caption length, used only to share out leftover slack.
    private static int[] ComputeColumnWidths(ResultProtocolDocument doc, out IReadOnlyList<string> headers)
    {
        var count = doc.ColumnHeaders.Count;
        if (count == 0)
        {
            headers = [];
            return [];
        }

        var shortWordChars = new int[count];
        var fullWordChars = new int[count];
        for (var c = 0; c < count; c++)
        {
            var full = doc.ColumnHeaders[c] ?? string.Empty;
            var shrt = c < doc.ColumnHeadersShort.Count ? doc.ColumnHeadersShort[c] : string.Empty;
            // No abbreviation ⇒ the short floor is just the full word (the column can't shrink below the caption).
            shortWordChars[c] = HeaderWidthChars(shrt.Length > 0 ? shrt : full);
            fullWordChars[c] = HeaderWidthChars(full);
        }

        // Per column gather the longest cell, the mean cell length, and the cell count — used to size each
        // column to its content.
        // Mean is taken over NON-EMPTY cells only — a mostly-blank column (e.g. ДЮСШ) must be sized to the
        // typical FILLED value, not collapsed by all its empty rows pulling the average toward zero.
        var maxCell = new int[count];
        long[] sumCell = new long[count];
        var filledCount = new int[count];
        foreach (var section in doc.Sections)
            foreach (var row in section.Rows)
                for (var c = 0; c < count && c < row.Cells.Count; c++)
                {
                    var len = row.Cells[c]?.Length ?? 0;
                    maxCell[c] = Math.Max(maxCell[c], len);
                    if (len > 0)
                    {
                        sumCell[c] += len;
                        filledCount[c]++;
                    }
                }

        // The column's body content width (chars):
        //  • non-wrapping columns (short codes: рік, № з/п, номер, результат, місце, кваліфікація…) must fit
        //    their LONGEST value on one line, so contentChars = the longest cell;
        //  • wrapping columns (name, club, region, ДЮСШ, coach…) are sized to the TYPICAL FILLED value — the
        //    mean over non-empty cells with some slack — capped at WrapColumnMaxChars, so one long value wraps
        //    instead of widening the column.
        var dataChars = new int[count];
        var naturalChars = new int[count];
        for (var c = 0; c < count; c++)
        {
            var wraps = c < doc.ColumnBodyWrap.Count && doc.ColumnBodyWrap[c];
            if (wraps && filledCount[c] > 0)
            {
                var mean = (double)sumCell[c] / filledCount[c];
                var target = (int)Math.Ceiling(mean * WrapMeanSlack);
                target = Math.Clamp(target, WrapColumnMinChars, WrapColumnMaxChars);
                dataChars[c] = Math.Min(maxCell[c], target);
            }
            else
            {
                dataChars[c] = maxCell[c];
            }
            // "Natural" want (for distributing leftover slack) = the body content width only. It deliberately
            // does NOT include the full header length — otherwise a column with a long multi-word header but
            // short data (e.g. "Дата народження" over "16.05.2018") would greedily soak up slack and stretch.
            // The header is handled by the preferred floor (full word) + abbreviation fallback, not here.
            naturalChars[c] = dataChars[c];
        }

        // Char→twips conversion: average glyph advance for the 9.5 pt table font, plus the cell's left+right
        // padding. Kept close to Word's real metric (≈100); the tiered DistributeWidths is what keeps headers
        // unwrapped, not an inflated per-char estimate.
        const int charTwips = 137;  // ≈ average glyph advance at 13 pt
        const int padTwips = 80;    // 40 left + 40 right (matches TableCellMarginDefault)
        const int minColTwips = 360;

        // Local note: the body content width for a wrapping column is the mean cell length × WrapMeanSlack,
        // clamped to [WrapColumnMinChars, WrapColumnMaxChars]; see the loop above.
        var dataFloor = new int[count];      // inviolable: data AND the abbreviation must fit
        var preferredFloor = new int[count]; // + the full header word, when there's room
        var natural = new int[count];
        for (var c = 0; c < count; c++)
        {
            var hard = Math.Max(dataChars[c], shortWordChars[c]); // data and the fallback caption both must fit
            dataFloor[c] = Math.Max(minColTwips, hard * charTwips + padTwips);
            preferredFloor[c] = Math.Max(dataFloor[c], fullWordChars[c] * charTwips + padTwips);
            natural[c] = Math.Max(preferredFloor[c], naturalChars[c] * charTwips + padTwips);
        }

        var pageWidth = doc.Orientation == ProtocolOrientation.Landscape ? A4HeightTwips : A4WidthTwips;
        var printable = (int)pageWidth - 720 - 720;
        var widths = DistributeWidths(dataFloor, preferredFloor, natural, printable);

        // Give any rounding remainder to the first (widest, name) column so the row spans the full width.
        widths[0] += printable - widths.Sum();

        // Pick the caption per column: use the FULL caption only when its WHOLE text fits the final column width
        // on one line (so a multi-word header like "Дата народження" abbreviates to "Дата нар." instead of
        // wrapping at the space); otherwise fall back to the short caption.
        var chosen = new string[count];
        for (var c = 0; c < count; c++)
        {
            var full = doc.ColumnHeaders[c] ?? string.Empty;
            var shrt = c < doc.ColumnHeadersShort.Count ? doc.ColumnHeadersShort[c] : string.Empty;
            var fullNeeds = full.Length * charTwips + padTwips;
            chosen[c] = shrt.Length > 0 && fullNeeds > widths[c] ? shrt : full;
        }
        headers = chosen;
        return widths;
    }

    // Lays the columns out across the printable width in three tiers, so the right things give way first when
    // space is tight:
    //  1. Every column gets at least its data floor (data must never wrap). If even those overflow, scale them
    //     down proportionally — the only case where data is allowed to wrap (an extreme, very crowded table).
    //  2. Raise columns toward their preferred floor (data + header word) so short headers stay unwrapped; if
    //     there isn't room for every header word, share the available room proportionally — the long header
    //     words wrap first since they ask for the most.
    //  3. Hand any remaining slack out by each column's natural-size "want", so wide columns (the name) grow.
    private static int[] DistributeWidths(int[] dataFloor, int[] preferredFloor, int[] natural, int printable)
    {
        var count = dataFloor.Length;
        var widths = new int[count];

        long dataTotal = dataFloor.Sum(w => (long)w);

        // Tier 1: data floors don't even fit — scale them down (data wraps; unavoidable on a hugely crowded page).
        if (dataTotal >= printable)
        {
            for (var c = 0; c < count; c++)
                widths[c] = dataTotal > 0 ? (int)((long)dataFloor[c] * printable / dataTotal) : printable / count;
            return widths;
        }

        // Tier 2: start from the data floors, then add room toward the preferred floors (the header words).
        for (var c = 0; c < count; c++)
            widths[c] = dataFloor[c];
        var toPreferred = printable - (int)dataTotal;
        long headerWant = 0;
        var hWant = new int[count];
        for (var c = 0; c < count; c++)
        {
            hWant[c] = preferredFloor[c] - dataFloor[c];
            headerWant += hWant[c];
        }
        if (headerWant >= toPreferred)
        {
            // Not enough room to fit every header word — share it out; the biggest asks (long headers) fall short
            // and wrap, the small asks (short headers) are fully satisfied.
            for (var c = 0; c < count; c++)
                widths[c] += headerWant > 0 ? (int)((long)hWant[c] * toPreferred / headerWant) : 0;
            return widths;
        }

        // Every header word fits. Tier 3: spread the rest by natural-size want.
        for (var c = 0; c < count; c++)
            widths[c] = preferredFloor[c];
        var slack = printable - widths.Sum();
        long wantTotal = 0;
        var want = new int[count];
        for (var c = 0; c < count; c++)
        {
            want[c] = Math.Max(0, natural[c] - preferredFloor[c]);
            wantTotal += want[c];
        }
        for (var c = 0; c < count; c++)
            widths[c] += wantTotal > 0 ? (int)((long)want[c] * slack / wantTotal) : slack / count;
        return widths;
    }

    // A header wraps between words; columns up to this many characters in a single word are kept on one line
    // (so short headers never break), while longer words may wrap so they don't inflate a narrow data column.
    private const int HeaderWordCap = 8;

    // Sizing of a WRAPPING free-text column's body: target = mean cell length × WrapMeanSlack, clamped to
    // [WrapColumnMinChars, WrapColumnMaxChars]. So the column sits near the typical value (a little roomier
    // than the average), never narrower than a few chars, and never wider than the max — long outliers wrap.
    private const double WrapMeanSlack = 1.35;
    private const int WrapColumnMinChars = 6;
    private const int WrapColumnMaxChars = 20;

    // The header's contribution to a column's FLOOR: the longest single word in the header, capped at
    // HeaderWordCap, plus a one-char safety margin so the word never lands a hair too wide for the budgeted
    // column and wraps. Short header words ("Номер", "Місце", "Region") stay on one line; a long word
    // ("Кваліфікація") is capped so it's allowed to wrap (the column then falls back to the abbreviation).
    // Multi-word headers ("Прізвище, ім'я", "Рік нар.") are measured per word, so they wrap at the spaces.
    private static int HeaderWidthChars(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return 0;
        var longestWord = header.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length)
            .DefaultIfEmpty(0)
            .Max();
        return Math.Min(longestWord, HeaderWordCap) + HeaderSafetyChars;
    }

    // One extra character of slack on every header word's floor, so a slightly-wider-than-estimated word still
    // fits its column rather than wrapping. Applied to both the short and full header words.
    private const int HeaderSafetyChars = 1;

    // Table font: 13 pt (half-points), sized so roughly 40 rows fit on an A4 page (was 9.5 pt / ~55 rows).
    private const string TableFontHalfPoints = "26";

    private static Table BuildTable(IReadOnlyList<string> headers, IReadOnlyList<ResultProtocolBodyRow> rows,
        int[] columnWidths, IReadOnlyList<bool> bodyWrap)
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

        // Header row (bold, boxed). Headers always allow wrapping — a long chosen caption may span two lines.
        var headerRow = new TableRow();
        for (var i = 0; i < headers.Count; i++)
            headerRow.Append(Cell(headers[i], bold: true, boxed: true, columnWidths[i], noWrap: false));
        table.Append(headerRow);

        foreach (var row in rows)
        {
            var tr = new TableRow();
            // A team caption row reads as one team: its cells are bold (the team place/score stand out).
            var bold = row.IsTeamHeader;
            // Pad/truncate to the header width so a malformed row can't shift the columns. A non-wrapping
            // (short-code) column forces its data onto one line; free-text columns wrap.
            for (var i = 0; i < headers.Count; i++)
            {
                var noWrap = !(i < bodyWrap.Count && bodyWrap[i]);
                tr.Append(Cell(i < row.Cells.Count ? row.Cells[i] : string.Empty, bold, boxed: false,
                    columnWidths[i], noWrap));
            }
            table.Append(tr);
        }

        return table;
    }

    // One table cell. Font/size come from the document default (Times New Roman at the table size), so an
    // empty cell still renders correctly; the run only adds bold when needed. Only header cells are boxed —
    // <paramref name="boxed"/> draws a single border on all four sides; data cells stay border-less.
    private static TableCell Cell(string text, bool bold, bool boxed, int widthTwips, bool noWrap)
    {
        var run = new Run(new Text(text ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve });
        if (bold)
            run.RunProperties = new RunProperties(new Bold());

        // The explicit cell width (Dxa) is required under a fixed table layout so the column keeps its
        // computed, group-equal width regardless of content. Child order follows the CT_TcPr schema:
        // tcW → tcBorders → noWrap.
        var cellProps = new TableCellProperties(
            new TableCellWidth { Width = widthTwips.ToString(), Type = TableWidthUnitValues.Dxa });
        if (boxed)
            cellProps.Append(new TableCellBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 }));
        // A short-code column never wraps its data onto a second line (рік, № з/п, результат, місце…).
        if (noWrap)
            cellProps.Append(new NoWrap());

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

        // Role starts at the left margin (no indent), the name at the centre tab — gives the wide gap on the
        // page without pushing the whole block toward the middle of the sheet.
        var nameTab = rightTabTwips / 2;
        foreach (var official in doc.Officials)
        {
            var p = new Paragraph(new ParagraphProperties(
                new SpacingBetweenLines { Before = "60", After = "60" },
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
