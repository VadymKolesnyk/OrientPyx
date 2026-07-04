using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.DataAccess.Documents;

/// <summary>
/// Renders a <see cref="SummaryProtocolDocument"/> (the multi-day «Підсумковий залік») to a Word (.docx) file.
/// Like the results-protocol writer, but the table has a <b>two-tier boxed header</b>: a top row of leading
/// identity headers (each vertically merged down through the second tier) + one day-band cell per counted day
/// (spanning its 2–3 sub-columns) + a merged «Сума», then a second row of the per-day sub-column captions
/// (М / Час [ / Очки]). One section per group, each its own boxed table. Times New Roman set as the document
/// default so empty cells inherit the font. Lives in DataAccess (the Open XML SDK must not reach BusinessLogic).
/// </summary>
public sealed class DocxSummaryProtocolWriter : ISummaryProtocolWriter
{
    private const uint A4WidthTwips = 11906;
    private const uint A4HeightTwips = 16838;
    private const string FontName = "Times New Roman";
    private const string TableFontHalfPoints = "20";   // 10 pt — summary has many columns, so a touch smaller
    private const string BandShade = "F2F2F2";

    public byte[] Write(SummaryProtocolDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var stream = new MemoryStream();
        using (var word = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = word.AddMainDocumentPart();
            AppendStyles(main);

            var body = new Body();
            AppendHeader(body, document, RightTabPosition(document.Orientation));

            var widths = ComputeColumnWidths(document);
            foreach (var section in document.Sections)
                AppendSection(body, document, section, widths);

            AppendOfficials(body, document, RightTabPosition(document.Orientation));

            var footerReference = AppendFooterPart(main, document);
            body.Append(PageSettings(document.Orientation, footerReference));
            main.Document = new Document(body);
            main.Document.Save();
        }

        return stream.ToArray();
    }

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

    private static void AppendHeader(Body body, SummaryProtocolDocument doc, int rightTabTwips)
    {
        if (doc.Subtitle.Length > 0)
            body.Append(CentredParagraph(doc.Subtitle, bold: false, 24));
        if (doc.CompetitionName.Length > 0)
            body.Append(CentredParagraph(doc.CompetitionName, bold: false, 24));
        if (doc.Title.Length > 0)
            body.Append(CentredParagraph(doc.Title, bold: true, 28));

        if (doc.DateText.Length > 0 || doc.CompetitionType.Length > 0 || doc.Venue.Length > 0)
        {
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

        body.Append(new Paragraph());
    }

    private static void AppendSection(Body body, SummaryProtocolDocument doc, SummaryProtocolSection section, int[] widths)
    {
        body.Append(CaptionParagraph(section.GroupName, bold: true));
        body.Append(BuildTable(doc, section, widths));
        body.Append(new Paragraph());
    }

    // The boxed table: a two-tier header (band row + sub-column row), then the data rows. The full leaf-column
    // grid is declared up front; the header cells use vertical merge (leading + total span both tiers) and
    // horizontal merge (each day band spans its sub-columns).
    private static Table BuildTable(SummaryProtocolDocument doc, SummaryProtocolSection section, int[] widths)
    {
        var totalWidth = widths.Sum();
        var table = new Table();
        table.AppendChild(new TableProperties(
            new TableLayout { Type = TableLayoutValues.Fixed },
            new TableWidth { Width = totalWidth.ToString(), Type = TableWidthUnitValues.Dxa },
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }),
            new TableCellMarginDefault(
                new TableCellLeftMargin { Width = 30, Type = TableWidthValues.Dxa },
                new TableCellRightMargin { Width = 30, Type = TableWidthValues.Dxa })));

        var grid = new TableGrid();
        foreach (var w in widths)
            grid.Append(new GridColumn { Width = w.ToString() });
        table.Append(grid);

        var leadCount = doc.LeadingColumns.Count;

        // ── Tier 1 (band row) ──────────────────────────────────────────────────────────────────────────
        var tier1 = new TableRow();
        // Leading columns: a header cell spanning both tiers (vertical-merge restart).
        for (var c = 0; c < leadCount; c++)
            tier1.Append(HeaderCell(doc.LeadingColumns[c].Caption, widths[c],
                vMerge: MergedCellValues.Restart, centred: true));

        // Day bands: a band caption cell spanning the band's sub-columns (horizontal merge), shaded.
        var col = leadCount;
        foreach (var band in doc.DayBands)
        {
            var span = band.SubColumns.Count;
            // First cell of the band: the caption, horizontal-merge restart, shaded.
            tier1.Append(BandHeaderCell(band.Caption, widths[col], MergedCellValues.Restart));
            for (var s = 1; s < span; s++)
                tier1.Append(BandHeaderCell(string.Empty, widths[col + s], MergedCellValues.Continue));
            col += span;
        }

        // Total column: spans both tiers.
        tier1.Append(HeaderCell(doc.TotalColumnHeader, widths[col], vMerge: MergedCellValues.Restart, centred: true));
        table.Append(tier1);

        // ── Tier 2 (sub-column row) ─────────────────────────────────────────────────────────────────────
        var tier2 = new TableRow();
        // Leading columns: vertical-merge continuation (empty).
        for (var c = 0; c < leadCount; c++)
            tier2.Append(HeaderCell(string.Empty, widths[c], vMerge: MergedCellValues.Continue, centred: true));

        col = leadCount;
        foreach (var band in doc.DayBands)
        {
            foreach (var sub in band.SubColumns)
            {
                tier2.Append(HeaderCell(sub, widths[col], vMerge: null, centred: true));
                col++;
            }
        }
        // Total column: vertical-merge continuation.
        tier2.Append(HeaderCell(string.Empty, widths[col], vMerge: MergedCellValues.Continue, centred: true));
        table.Append(tier2);

        // ── Data rows ───────────────────────────────────────────────────────────────────────────────────
        foreach (var row in section.Rows)
        {
            var tr = new TableRow();
            for (var c = 0; c < widths.Length; c++)
            {
                var text = c < row.Count ? row[c] : string.Empty;
                // The name column is left-aligned and wraps; everything else is centred, single-line. The
                // leading place column + day sub-cells + total are short codes.
                var isName = c == doc.NameColumnIndex;
                tr.Append(DataCell(text, widths[c], centred: !isName, noWrap: !isName));
            }
            table.Append(tr);
        }

        return table;
    }

    // ── Column widths ───────────────────────────────────────────────────────────────────────────────────

    // Leaf-column widths in twips. Mirrors DocxResultProtocolWriter.ComputeColumnWidths: each column is sized to
    // its content (the longest value for short-code columns; a typical value for wrapping free-text columns),
    // then the columns are laid out across the printable width in three tiers (guarantee the data floors,
    // squeezing the shrinkable columns by shrink priority when they overflow; raise toward the header-word
    // floors; hand the rest out by natural want so the name column grows). So the summary sizes columns the same
    // way the per-day protocol does.
    private static int[] ComputeColumnWidths(SummaryProtocolDocument doc)
    {
        var count = doc.LeafColumnCount;
        if (count == 0)
            return [];

        // The per-leaf-column caption (leading captions, then each day band's sub-column captions, then total).
        var captions = LeafCaptions(doc);

        // The header word floor per column (the longest header word, capped + safety char). No abbreviations in
        // the summary, so the short and full header floors coincide.
        var headerWordChars = new int[count];
        for (var c = 0; c < count; c++)
            headerWordChars[c] = HeaderWidthChars(captions[c]);

        // Longest + mean (over non-empty cells) data length per column — the mean drives wrapping columns so a
        // mostly-blank column (ДЮСШ) sizes to its typical FILLED value, not collapsed by blanks.
        var maxCell = new int[count];
        var sumCell = new long[count];
        var filledCount = new int[count];
        foreach (var row in doc.Sections.SelectMany(s => s.Rows))
            for (var c = 0; c < count && c < row.Count; c++)
            {
                var len = row[c]?.Length ?? 0;
                maxCell[c] = Math.Max(maxCell[c], len);
                if (len > 0)
                {
                    sumCell[c] += len;
                    filledCount[c]++;
                }
            }

        var dataChars = new int[count];
        for (var c = 0; c < count; c++)
        {
            var wraps = c < doc.ColumnBodyWrap.Count && doc.ColumnBodyWrap[c];
            if (wraps && filledCount[c] > 0)
            {
                var mean = (double)sumCell[c] / filledCount[c];
                var target = Math.Clamp((int)Math.Ceiling(mean * WrapMeanSlack), WrapColumnMinChars, WrapColumnMaxChars);
                dataChars[c] = Math.Min(maxCell[c], target);
            }
            else
            {
                dataChars[c] = maxCell[c];
            }
        }

        const int charTwips = 137;  // ≈ average glyph advance at 13 pt (matches the results writer)
        const int padTwips = 60;    // 30 left + 30 right (TableCellMarginDefault in BuildTable)
        const int minColTwips = 300;

        var dataFloor = new int[count];
        var preferredFloor = new int[count];
        var natural = new int[count];
        for (var c = 0; c < count; c++)
        {
            dataFloor[c] = Math.Max(minColTwips, dataChars[c] * charTwips + padTwips);
            preferredFloor[c] = Math.Max(dataFloor[c], headerWordChars[c] * charTwips + padTwips);
            natural[c] = Math.Max(preferredFloor[c], dataChars[c] * charTwips + padTwips);
        }

        var priority = new int[count];
        for (var c = 0; c < count; c++)
            priority[c] = c < doc.ColumnShrinkPriority.Count ? doc.ColumnShrinkPriority[c] : 1;

        var shrinkFloor = new int[count];
        for (var c = 0; c < count; c++)
            shrinkFloor[c] = ShrinkFloor(priority[c], dataFloor[c], preferredFloor[c], minColTwips);

        var pageWidth = doc.Orientation == ProtocolOrientation.Landscape ? A4HeightTwips : A4WidthTwips;
        var printable = (int)pageWidth - 720 - 720;
        var widths = DistributeWidths(dataFloor, preferredFloor, natural, shrinkFloor, priority, printable);

        // Hand the rounding remainder to the wide name column (else the first column) so the row spans exactly.
        var slackCol = doc.NameColumnIndex >= 0 ? doc.NameColumnIndex : 0;
        widths[slackCol] += printable - widths.Sum();
        return widths;
    }

    // The caption per leaf column: leading captions, then each day band's sub-column captions, then the total.
    private static string[] LeafCaptions(SummaryProtocolDocument doc)
    {
        var captions = new string[doc.LeafColumnCount];
        var leadCount = doc.LeadingColumns.Count;
        for (var c = 0; c < leadCount; c++)
            captions[c] = doc.LeadingColumns[c].Caption;
        var col = leadCount;
        foreach (var band in doc.DayBands)
            foreach (var sub in band.SubColumns)
                captions[col++] = sub;
        captions[col] = doc.TotalColumnHeader;
        return captions;
    }

    // The smallest a column may be squeezed to under width pressure (mirrors DocxResultProtocolWriter.ShrinkFloor).
    private static int ShrinkFloor(int priority, int dataFloor, int preferredFloor, int absoluteMin) => priority switch
    {
        <= 1 => preferredFloor,
        2 => Math.Max(absoluteMin, (int)(dataFloor * 0.85)),
        3 => Math.Max(absoluteMin, (int)(dataFloor * 0.70)),
        _ => Math.Max(absoluteMin, (int)(dataFloor * 0.55)),
    };

    // The 3-tier distribution shared with the results writer (see DocxResultProtocolWriter.DistributeWidths).
    private static int[] DistributeWidths(
        int[] dataFloor, int[] preferredFloor, int[] natural, int[] shrinkFloor, int[] priority, int printable)
    {
        var count = dataFloor.Length;
        var widths = new int[count];

        long dataTotal = dataFloor.Sum(w => (long)w);

        if (dataTotal >= printable)
        {
            for (var c = 0; c < count; c++)
                widths[c] = dataFloor[c];
            ShrinkByDeficit(widths, shrinkFloor, priority, (int)(dataTotal - printable));
            return widths;
        }

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
            for (var c = 0; c < count; c++)
                widths[c] += headerWant > 0 ? (int)((long)hWant[c] * toPreferred / headerWant) : 0;
            return widths;
        }

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

    private static int ShrinkWeight(int priority) => priority switch { 4 => 3, 3 => 2, 2 => 1, _ => 0 };

    private static void ShrinkByDeficit(int[] widths, int[] shrinkFloor, int[] priority, int deficit)
    {
        if (deficit <= 0)
            return;

        var count = widths.Length;
        var weight = new double[count];
        var open = new bool[count];
        for (var c = 0; c < count; c++)
        {
            weight[c] = ShrinkWeight(priority[c]);
            open[c] = weight[c] > 0 && widths[c] > shrinkFloor[c];
        }

        for (var pass = 0; pass < count && deficit > 0; pass++)
        {
            double weightTotal = 0;
            for (var c = 0; c < count; c++)
                if (open[c])
                    weightTotal += weight[c];
            if (weightTotal <= 0)
                break;

            var clampedAny = false;
            var remaining = deficit;
            for (var c = 0; c < count; c++)
            {
                if (!open[c])
                    continue;
                var share = (int)(deficit * weight[c] / weightTotal);
                var room = widths[c] - shrinkFloor[c];
                if (share >= room)
                {
                    share = room;
                    open[c] = false;
                    clampedAny = true;
                }
                widths[c] -= share;
                remaining -= share;
            }
            deficit = remaining;
            if (!clampedAny)
                break;
        }

        if (deficit > 0)
        {
            long total = widths.Sum(w => (long)w);
            var target = total - deficit;
            if (total > 0 && target > 0)
                for (var c = 0; c < count; c++)
                    widths[c] = (int)((long)widths[c] * target / total);
        }
    }

    private const int HeaderWordCap = 8;
    private const double WrapMeanSlack = 1.35;
    private const int WrapColumnMinChars = 6;
    private const int WrapColumnMaxChars = 20;
    private const int HeaderSafetyChars = 1;

    private static int HeaderWidthChars(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return 0;
        var longestWord = header.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length).DefaultIfEmpty(0).Max();
        return Math.Min(longestWord, HeaderWordCap) + HeaderSafetyChars;
    }

    // ── Cells ───────────────────────────────────────────────────────────────────────────────────────────

    private static TableCell HeaderCell(string text, int widthTwips, MergedCellValues? vMerge, bool centred)
    {
        var props = new TableCellProperties(
            new TableCellWidth { Width = widthTwips.ToString(), Type = TableWidthUnitValues.Dxa });
        if (vMerge is { } vm)
            props.Append(new VerticalMerge { Val = vm });
        props.Append(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });

        var run = new Run(new Text(text ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve })
        {
            RunProperties = new RunProperties(new Bold())
        };
        var para = new Paragraph(run);
        if (centred)
            para.ParagraphProperties = new ParagraphProperties(new Justification { Val = JustificationValues.Center });
        return new TableCell(props, para);
    }

    private static TableCell BandHeaderCell(string text, int widthTwips, MergedCellValues hMerge)
    {
        var props = new TableCellProperties(
            new TableCellWidth { Width = widthTwips.ToString(), Type = TableWidthUnitValues.Dxa },
            new HorizontalMerge { Val = hMerge },
            new Shading { Val = ShadingPatternValues.Clear, Fill = BandShade },
            new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });

        var run = new Run(new Text(text ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve })
        {
            RunProperties = new RunProperties(new Bold())
        };
        return new TableCell(props,
            new Paragraph(new ParagraphProperties(new Justification { Val = JustificationValues.Center }), run));
    }

    private static TableCell DataCell(string text, int widthTwips, bool centred, bool noWrap)
    {
        var props = new TableCellProperties(
            new TableCellWidth { Width = widthTwips.ToString(), Type = TableWidthUnitValues.Dxa });
        if (noWrap)
            props.Append(new NoWrap());

        var para = new Paragraph(new Run(new Text(text ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve }));
        if (centred)
            para.ParagraphProperties = new ParagraphProperties(new Justification { Val = JustificationValues.Center });
        return new TableCell(props, para);
    }

    // ── Header / footer / officials (mirrors DocxResultProtocolWriter) ────────────────────────────────────

    private static Paragraph CentredParagraph(string text, bool bold, int sizeHalfPoints)
    {
        var props = new RunProperties();
        if (bold)
            props.Append(new Bold());
        props.Append(new FontSize { Val = sizeHalfPoints.ToString() });
        var run = new Run { RunProperties = props };
        AppendMultilineText(run, text);
        return new Paragraph(
            new ParagraphProperties(new Justification { Val = JustificationValues.Center }), run);
    }

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

    private static Paragraph CaptionParagraph(string text, bool bold)
    {
        var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        if (bold)
            run.RunProperties = new RunProperties(new Bold());
        return new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "40" }), run);
    }

    private static void AppendOfficials(Body body, SummaryProtocolDocument doc, int rightTabTwips)
    {
        if (doc.Officials.Count == 0)
            return;
        body.Append(new Paragraph());
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

    private static Run Run(string text)
    {
        var run = new Run();
        AppendMultilineText(run, text);
        return run;
    }

    private static int RightTabPosition(ProtocolOrientation orientation)
    {
        var pageWidth = orientation == ProtocolOrientation.Landscape ? A4HeightTwips : A4WidthTwips;
        return (int)pageWidth - 720 - 720;
    }

    private static SectionProperties PageSettings(ProtocolOrientation orientation, FooterReference? footer)
    {
        var landscape = orientation == ProtocolOrientation.Landscape;
        var size = landscape
            ? new PageSize { Width = A4HeightTwips, Height = A4WidthTwips, Orient = PageOrientationValues.Landscape }
            : new PageSize { Width = A4WidthTwips, Height = A4HeightTwips };
        var sectPr = new SectionProperties();
        if (footer is not null)
            sectPr.Append(footer);
        sectPr.Append(size);
        sectPr.Append(new PageMargin
        {
            Top = 720, Bottom = 720, Left = 720, Right = 720, Header = 0, Footer = footer is not null ? 360u : 0u, Gutter = 0
        });
        return sectPr;
    }

    private static FooterReference? AppendFooterPart(MainDocumentPart main, SummaryProtocolDocument doc)
    {
        if (doc.Footer is not { } f)
            return null;

        var rightTab = RightTabPosition(doc.Orientation);
        var p = new Paragraph(new ParagraphProperties(new Tabs(
            new TabStop { Val = TabStopValues.Center, Position = rightTab / 2 },
            new TabStop { Val = TabStopValues.Right, Position = rightTab })));
        p.Append(Run(f.SoftwareName));
        p.Append(new Run(new TabChar()));
        var generated = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
        p.Append(Run(f.GeneratedLabel.Length > 0 ? $"{f.GeneratedLabel}: {generated}" : generated));
        p.Append(new Run(new TabChar()));
        if (f.PageLabel.Length > 0)
            p.Append(Run($"{f.PageLabel} "));
        p.Append(new SimpleField(new Run(new Text("1"))) { Instruction = "PAGE" });

        var footer = new Footer(p);
        var part = main.AddNewPart<FooterPart>();
        part.Footer = footer;
        part.Footer.Save();
        return new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(part) };
    }
}
