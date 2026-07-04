using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Layout;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

/// <summary>
/// Renders the shared protocol document preview into a host <see cref="Grid"/> as a print-faithful mock-up:
/// the group sections are stacked exactly as the .docx export lays them out — each a bold group caption, an
/// optional course sub-caption, a boxed column-header row, then border-less data rows — in Times New Roman at
/// a compact size, with the table filling the page width (star columns). Every header row is a drag source +
/// drop target, so columns can be reordered from any section; the whole target/dragged column is highlighted
/// across all sections and an insertion line marks where the column will land.
///
/// Used by both the results-protocol and start-protocol pages — each owns a <see cref="ProtocolPreviewTable"/>
/// bound to its <see cref="IProtocolPreviewHost"/> and the "PreviewTableHost" grid in its XAML. Built in code
/// (not XAML) because the column set and section count are dynamic and the headers carry the drag interaction.
/// </summary>
public sealed class ProtocolPreviewTable
{
    // In-process drag payload: the dragged column's key (its enum name). DataFormat needs a reference type,
    // so the key travels as a string; in-process keeps it local (never serialized to the OS clipboard).
    private static readonly DataFormat<string> ColumnFormat =
        DataFormat.CreateInProcessFormat<string>("orientpyx-protocol-column");

    // Print-faithful look: serif font, compact size, only the header row boxed (data rows border-less). The
    // sizes run a touch larger than the .docx point sizes because the on-screen preview reads smaller than the
    // printed sheet at the same nominal size — bumped so the preview better matches the actual Word output.
    private const string SerifFont = "Times New Roman";
    // Sized so the preview matches the .docx (13 pt body / ~40 rows per page). These run a touch larger than the
    // nominal .docx point sizes because the on-screen sheet reads smaller than the printed page at the same size.
    private const double BodyFontSize = 15.5;
    private const double CaptionFontSize = 17;
    private const double SubcaptionFontSize = 15.5;

    // A header wraps between words; columns whose longest word is no longer than this are kept on one line.
    // Matches DocxResultProtocolWriter.HeaderWordCap.
    private const int HeaderWordCap = 8;

    // Sizing of a WRAPPING free-text column's body (matches DocxResultProtocolWriter): target = mean cell
    // length × WrapMeanSlack, clamped to [WrapColumnMinChars, WrapColumnMaxChars]; long outliers wrap.
    private const double WrapMeanSlack = 1.35;
    private const int WrapColumnMinChars = 6;
    private const int WrapColumnMaxChars = 20;

    private static readonly FontFamily Serif = new(SerifFont);
    private static readonly IBrush HeaderBorder = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));

    // Column highlight while dragging: a translucent accent wash over the hovered column, a fainter wash over
    // the dragged column, and a thick insertion line where the dragged column would land.
    private static readonly IBrush DropColumnFill = new SolidColorBrush(Color.FromArgb(0x33, 0x6D, 0x4A, 0xFF));
    private static readonly IBrush DraggedColumnFill = new SolidColorBrush(Color.FromArgb(0x22, 0x6D, 0x4A, 0xFF));
    private static readonly IBrush DropLineBrush = new SolidColorBrush(Color.FromRgb(0x6D, 0x4A, 0xFF));

    private readonly Grid _host;
    private IProtocolPreviewHost? _previewHost;

    // All cells of each column across every section (header + body), kept so a drag can tint a whole column.
    // Index = column index. Header cells carry no fill (only a box), body cells are transparent, so the wash
    // is applied/cleared uniformly.
    private readonly List<List<Border>> _columnCells = [];

    // The drop-insertion line, spanning the whole table. Repositioned during drag-over, hidden otherwise.
    private Rectangle? _dropLine;

    public ProtocolPreviewTable(Grid host)
    {
        _host = host;
    }

    /// <summary>Points the table at a new host VM (or null), (re)subscribing to its preview collections.</summary>
    public void Bind(IProtocolPreviewHost? host)
    {
        if (_previewHost is { } old)
        {
            old.Preview.Columns.CollectionChanged -= OnPreviewChanged;
            old.Preview.Sections.CollectionChanged -= OnPreviewChanged;
            old.Preview.PropertyChanged -= OnPreviewPropertyChanged;
        }
        _previewHost = host;
        if (_previewHost is { } h)
        {
            h.Preview.Columns.CollectionChanged += OnPreviewChanged;
            h.Preview.Sections.CollectionChanged += OnPreviewChanged;
            h.Preview.PropertyChanged += OnPreviewPropertyChanged;
        }
        Rebuild();
    }

    private void OnPreviewChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    // Orientation changes the page's printable width, so the content-fit column widths must be recomputed —
    // rebuild on IsLandscape. (Other header-text properties don't affect the table, so they're ignored.)
    private void OnPreviewPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProtocolPreviewViewModel.IsLandscape))
            Rebuild();
    }

    private void Rebuild()
    {
        _host.Children.Clear();
        _host.ColumnDefinitions.Clear();
        _host.RowDefinitions.Clear();
        _columnCells.Clear();
        _dropLine = null;

        if (_previewHost is not { } host)
            return;
        var columns = host.Preview.Columns;
        var sections = host.Preview.Sections;
        if (columns.Count == 0 || sections.Count == 0)
            return;

        // Explicit, content-proportional pixel widths — computed with the SAME algorithm the .docx writer uses
        // (widest content per column across every section, scaled to fill the page width), so the preview lines
        // up with the generated document. Recomputed on every Rebuild, which fires whenever columns are
        // reordered, shown or hidden — so the widths always reflect the current column set and data.
        var widths = ComputeColumnWidths(columns, sections, out var captions);
        for (var c = 0; c < columns.Count; c++)
            _host.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(widths[c], GridUnitType.Pixel)));

        for (var c = 0; c < columns.Count; c++)
            _columnCells.Add([]);

        var row = 0;

        // Banded layout (judges' start protocol): every section is a shaded full-width minute band, so the boxed
        // column header is drawn ONCE at the very top and then each section is just its band + rows — the sheet
        // reads as one running list with start-minute dividers (matches the .docx + the printed form).
        var banded = sections.Count > 0 && AllBanded(sections);
        if (banded)
        {
            _host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < columns.Count; c++)
            {
                var header = BuildHeaderCell(columns[c], captions[c]);
                WireDrag(header);
                Grid.SetColumn(header, c);
                Grid.SetRow(header, row);
                _host.Children.Add(header);
                _columnCells[c].Add(header);
            }
            row++;
        }

        for (var s = 0; s < sections.Count; s++)
        {
            var section = sections[s];

            if (section.IsBanded)
            {
                // Shaded full-width caption band (bold, centred) — the minute divider.
                _host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                AddBandRow(section.GroupName, row, columns.Count, topMargin: s == 0 ? 0 : 4);
                row++;
            }
            else
            {
                // Group caption (bold) — spans all columns. A little top gap between sections. The course-setter
                // (начальник дистанції), when present, is shown right-aligned on the same caption row.
                _host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                AddSpanningText(section.GroupName, row, columns.Count, bold: true, size: CaptionFontSize,
                    topMargin: s == 0 ? 0 : 12);
                if (section.CourseSetter.Length > 0)
                    AddSpanningText(section.CourseSetter, row, columns.Count, bold: true, size: CaptionFontSize,
                        topMargin: s == 0 ? 0 : 12, alignRight: true);
                row++;

                // Course sub-caption (only when present).
                if (section.Subcaption.Length > 0)
                {
                    _host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    AddSpanningText(section.Subcaption, row, columns.Count, bold: false, size: SubcaptionFontSize,
                        topMargin: 0, foreground: Brushes.DimGray);
                    row++;
                }

                // Per-section header row — boxed; each cell is a drag source + drop target.
                _host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (var c = 0; c < columns.Count; c++)
                {
                    var header = BuildHeaderCell(columns[c], captions[c]);
                    WireDrag(header);

                    Grid.SetColumn(header, c);
                    Grid.SetRow(header, row);
                    _host.Children.Add(header);
                    _columnCells[c].Add(header);
                }
                row++;
            }

            // Body rows. Normal sections are border-less (just a bottom hairline); the banded judges' protocol
            // draws a full cell grid so its rows sit in boxes like the printed sheet (matches the .docx).
            foreach (var bodyRow in section.Rows)
            {
                _host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (var c = 0; c < columns.Count; c++)
                {
                    var text = c < bodyRow.Cells.Count ? bodyRow.Cells[c] : string.Empty;
                    var cell = BuildBodyCell(text, bodyRow.IsTeamHeader, columns[c], boxed: banded);
                    WireDrag(cell); // drag anywhere in a column, not just its header
                    Grid.SetColumn(cell, c);
                    Grid.SetRow(cell, row);
                    _host.Children.Add(cell);
                    _columnCells[c].Add(cell);
                }
                row++;
            }

            // Rank-derivation line under the table (Додаток 89), when the group awards a rank and its column is
            // shown — spans every column, like the .docx's caption paragraph.
            if (section.HasRankCalculation)
            {
                _host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                AddSpanningText(section.RankCalculation, row, columns.Count, bold: false,
                    size: SubcaptionFontSize, topMargin: 2, foreground: Brushes.DimGray);
                row++;
            }
        }

        // The drop-insertion line, spanning every row. Hidden until a drag hovers a header.
        _dropLine = new Rectangle
        {
            Width = 3,
            Fill = DropLineBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
            IsVisible = false,
            Margin = new Thickness(-1.5, 0, 0, 0)
        };
        Grid.SetColumn(_dropLine, 0);
        Grid.SetRow(_dropLine, 0);
        Grid.SetRowSpan(_dropLine, row);
        _host.Children.Add(_dropLine);
    }

    // ── Content-proportional column widths (mirrors the .docx writer so the preview matches the document) ──

    // The preview sheet's geometry, kept in sync with LandscapeToPageWidthConverter + the sheet padding in the
    // protocol Views, so the table fills the same printable width the document does. (Short side × A4 ratio for
    // the long side; minus the sheet's left+right padding for the content width.)
    private const double SheetShortSide = 720;          // LandscapeToPageWidthConverter.PortraitShortSide
    private const double A4Ratio = 297.0 / 210.0;
    private const double SheetPadding = 26;             // the white sheet Border's Padding in the Views

    // Per-column pixel widths sized to the widest content in each column (across the header and every section's
    // rows), then scaled to fill the page's printable width. One shared set for all sections — content-sized but
    // equal between groups. Same shape as DocxResultProtocolWriter.ComputeColumnWidths so the two agree.
    private List<double> ComputeColumnWidths(
        IReadOnlyList<ProtocolPreviewColumn> columns,
        IReadOnlyList<ProtocolPreviewSection> sections,
        out string[] captions)
    {
        var count = columns.Count;

        // Char counts per column (mirrors DocxResultProtocolWriter.ComputeColumnWidths):
        //  • dataChars      — body content width: the longest cell for non-wrapping (short-code) columns, but
        //    only the TYPICAL value (mean × slack, capped) for wrapping free-text columns so long outliers wrap.
        //  • shortWordChars — the short caption's longest word (or the full word when no abbreviation). Also
        //    inviolable: the abbreviation is the fallback so it must always fit on one line.
        //  • fullWordChars  — the full caption's longest word. PREFERRED: fit it when there's room, else fall
        //    back to the short caption.
        //  • naturalChars   — full caption length, used only to share out leftover slack.
        const double charPx = BodyFontSize * 0.52; // ≈ average glyph advance for the serif body font
        const double padPx = 8;                    // BuildBodyCell horizontal padding (4 + 4)
        const double minColPx = 26;

        var shortWordChars = new int[count];
        var fullWordChars = new int[count];
        for (var c = 0; c < count; c++)
        {
            var full = columns[c].Caption ?? string.Empty;
            var shrt = columns[c].ShortCaption ?? string.Empty;
            shortWordChars[c] = HeaderWidthChars(shrt.Length > 0 ? shrt : full);
            fullWordChars[c] = HeaderWidthChars(full);
        }

        // Gather the longest cell + the mean over NON-EMPTY cells per column. Empty cells are excluded from the
        // mean so a mostly-blank column (ДЮСШ) is sized to its typical FILLED value, not collapsed by blanks.
        var maxCell = new int[count];
        var sumCell = new long[count];
        var filledCount = new int[count];
        foreach (var section in sections)
            foreach (var bodyRow in section.Rows)
                for (var c = 0; c < count && c < bodyRow.Cells.Count; c++)
                {
                    var len = bodyRow.Cells[c]?.Length ?? 0;
                    maxCell[c] = Math.Max(maxCell[c], len);
                    if (len > 0)
                    {
                        sumCell[c] += len;
                        filledCount[c]++;
                    }
                }

        var dataChars = new int[count];
        var naturalChars = new int[count];
        for (var c = 0; c < count; c++)
        {
            if (columns[c].BodyWraps && filledCount[c] > 0)
            {
                var mean = (double)sumCell[c] / filledCount[c];
                var target = Math.Clamp((int)Math.Ceiling(mean * WrapMeanSlack), WrapColumnMinChars, WrapColumnMaxChars);
                dataChars[c] = Math.Min(maxCell[c], target);
            }
            else
            {
                dataChars[c] = maxCell[c];
            }
            // Slack "want" = body content width only (NOT the full header length), so a long-header/short-data
            // column ("Дата народження" over "16.05.2018") doesn't greedily soak up slack and stretch.
            naturalChars[c] = dataChars[c];
        }

        var dataFloor = new double[count];      // inviolable: data AND the abbreviation must fit
        var preferredFloor = new double[count]; // + the full header word, when there's room
        var natural = new double[count];
        var dataTotal = 0.0;
        for (var c = 0; c < count; c++)
        {
            var hard = Math.Max(dataChars[c], shortWordChars[c]);
            dataFloor[c] = Math.Max(minColPx, hard * charPx + padPx);
            preferredFloor[c] = Math.Max(dataFloor[c], fullWordChars[c] * charPx + padPx);
            natural[c] = Math.Max(preferredFloor[c], naturalChars[c] * charPx + padPx);
            dataTotal += dataFloor[c];
        }

        // Per-column shrink priority (1 = never narrowed; 4 = yields first/furthest) and the matching shrink floor
        // each column may be squeezed to. Mirrors DocxResultProtocolWriter so the preview squeezes the same columns.
        var priority = new int[count];
        var shrinkFloor = new double[count];
        for (var c = 0; c < count; c++)
        {
            priority[c] = columns[c].ShrinkPriority;
            shrinkFloor[c] = ShrinkFloor(priority[c], dataFloor[c], preferredFloor[c], minColPx);
        }

        // Scale to the sheet's printable width (the inner content width of the page Border).
        var landscape = _previewHost?.Preview.IsLandscape == true;
        var sheetWidth = landscape ? SheetShortSide * A4Ratio : SheetShortSide;
        var printable = sheetWidth - 2 * SheetPadding;

        var widths = DistributeWidths(dataFloor, preferredFloor, natural, shrinkFloor, priority, dataTotal, printable);

        // Pick the caption per column: use the FULL caption only when its WHOLE text fits the column on one line
        // (so a multi-word header like "Дата народження" abbreviates instead of wrapping at the space); else the
        // short caption. Mirrors DocxResultProtocolWriter.
        captions = new string[count];
        for (var c = 0; c < count; c++)
        {
            var full = columns[c].Caption ?? string.Empty;
            var shrt = columns[c].ShortCaption ?? string.Empty;
            var fullNeeds = full.Length * charPx + padPx;
            captions[c] = shrt.Length > 0 && fullNeeds > widths[c] ? shrt : full;
        }
        return widths;
    }

    // The smallest a column may be squeezed to under width pressure, by shrink priority (mirrors
    // DocxResultProtocolWriter.ShrinkFloor): priority 1 keeps its full preferred floor; 2/3/4 may dip below the
    // data floor toward a fraction of it (higher priority ⇒ further), never below an absolute readable minimum.
    private static double ShrinkFloor(int priority, double dataFloor, double preferredFloor, double absoluteMin) =>
        priority switch
        {
            <= 1 => preferredFloor,
            2 => Math.Max(absoluteMin, dataFloor * 0.85),
            3 => Math.Max(absoluteMin, dataFloor * 0.70),
            _ => Math.Max(absoluteMin, dataFloor * 0.55),
        };

    // The 3-tier distribution shared with the .docx writer (see DocxResultProtocolWriter.DistributeWidths):
    //  1. guarantee every data floor — if they overflow, share the deficit across the shrinkable columns by
    //     shrink weight (4→3, 3→2, 2→1, 1→0), each capped at its shrink floor; protected columns are untouched;
    //  2. raise toward the full-header floors, sharing proportionally when they don't all fit (long headers
    //     fall back to the abbreviation first);
    //  3. hand any remaining slack out by natural-size want (the name column grows).
    private static List<double> DistributeWidths(double[] dataFloor, double[] preferredFloor, double[] natural,
        double[] shrinkFloor, int[] priority, double dataTotal, double printable)
    {
        var count = dataFloor.Length;
        var widths = new List<double>(count);

        // Tier 1: data floors don't all fit — squeeze the shrinkable columns down toward their shrink floors,
        // taking the most from the highest-priority columns first; protected columns keep their data floor.
        if (dataTotal >= printable)
        {
            for (var c = 0; c < count; c++)
                widths.Add(dataFloor[c]);
            ShrinkByDeficit(widths, shrinkFloor, priority, dataTotal - printable);
            return widths;
        }

        // Tier 2.
        for (var c = 0; c < count; c++)
            widths.Add(dataFloor[c]);
        var toPreferred = printable - dataTotal;
        var headerWant = 0.0;
        var hWant = new double[count];
        for (var c = 0; c < count; c++)
        {
            hWant[c] = preferredFloor[c] - dataFloor[c];
            headerWant += hWant[c];
        }
        if (headerWant >= toPreferred)
        {
            for (var c = 0; c < count; c++)
                widths[c] += headerWant > 0 ? hWant[c] * toPreferred / headerWant : 0;
            return widths;
        }

        // Tier 3.
        for (var c = 0; c < count; c++)
            widths[c] = preferredFloor[c];
        var slack = printable - widths.Sum();
        var wantTotal = 0.0;
        var want = new double[count];
        for (var c = 0; c < count; c++)
        {
            want[c] = Math.Max(0, natural[c] - preferredFloor[c]);
            wantTotal += want[c];
        }
        var assigned = 0.0;
        for (var c = 0; c < count; c++)
        {
            widths[c] += wantTotal > 0 ? want[c] * slack / wantTotal : slack / count;
            assigned += widths[c];
        }
        // Hand any rounding remainder to the first (name) column so the row spans the full width exactly.
        if (count > 0)
            widths[0] += printable - assigned;
        return widths;
    }

    // The shrink weight of each priority (mirrors DocxResultProtocolWriter.ShrinkWeight): 4→3, 3→2, 2→1, 1→0.
    private static double ShrinkWeight(int priority) => priority switch { 4 => 3, 3 => 2, 2 => 1, _ => 0 };

    // Removes a total of <paramref name="deficit"/> px by sharing it across ALL shrinkable columns at once, in
    // proportion to their shrink weight (4→3, 3→2, 2→1, 1→0). A column pushed below its shrink floor is capped
    // there and its leftover share is redistributed over the rest (repeated until it settles). If the shrinkable
    // columns can't absorb it all, the rest is scaled out of everything proportionally. Mirrors
    // DocxResultProtocolWriter.ShrinkByDeficit so the preview matches the document.
    private static void ShrinkByDeficit(List<double> widths, double[] shrinkFloor, int[] priority, double deficit)
    {
        if (deficit <= 0)
            return;

        var count = widths.Count;
        var weight = new double[count];
        var open = new bool[count];
        for (var c = 0; c < count; c++)
        {
            weight[c] = ShrinkWeight(priority[c]);
            open[c] = weight[c] > 0 && widths[c] > shrinkFloor[c];
        }

        for (var pass = 0; pass < count && deficit > 1e-6; pass++)
        {
            var weightTotal = 0.0;
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
                var share = deficit * weight[c] / weightTotal;
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

        // Last resort: even the shrinkable columns couldn't absorb it — scale everything down proportionally.
        if (deficit > 1e-6)
        {
            var total = widths.Sum();
            var target = total - deficit;
            if (total > 0 && target > 0)
                for (var c = 0; c < count; c++)
                    widths[c] = widths[c] * target / total;
        }
    }

    // The header's contribution to a column's FLOOR: the longest single word, capped at HeaderWordCap, plus a
    // one-char safety margin. Short header words stay on one line; long ones may wrap (the column then falls
    // back to the abbreviation). Mirrors DocxResultProtocolWriter.HeaderWidthChars (incl. the safety char) so
    // the preview makes the same full-vs-abbreviated choice the document does.
    private const int HeaderSafetyChars = 1;

    private static int HeaderWidthChars(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return 0;
        var longestWord = 0;
        foreach (var word in header.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            longestWord = Math.Max(longestWord, word.Length);
        return Math.Min(longestWord, HeaderWordCap) + HeaderSafetyChars;
    }

    // Light-grey caption-band fill (matches the .docx D9D9D9 shading).
    private static readonly IBrush BandFill = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9));
    private static readonly IBrush BandBorder = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));

    private static bool AllBanded(IReadOnlyList<ProtocolPreviewSection> sections)
    {
        foreach (var s in sections)
            if (!s.IsBanded)
                return false;
        return true;
    }

    // A shaded full-width caption band (bold, centred) spanning every column — the judges' minute divider.
    private void AddBandRow(string text, int row, int columnCount, double topMargin)
    {
        var band = new Border
        {
            Background = BandFill,
            BorderBrush = BandBorder,
            BorderThickness = new Thickness(0.8),
            Padding = new Thickness(4, 2),
            Margin = new Thickness(0, topMargin, 0, 0),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = Serif,
                Foreground = Brushes.Black,
                FontWeight = FontWeight.Bold,
                FontSize = BodyFontSize,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
        Grid.SetColumn(band, 0);
        Grid.SetColumnSpan(band, columnCount);
        Grid.SetRow(band, row);
        _host.Children.Add(band);
    }

    private void AddSpanningText(string text, int row, int columnCount, bool bold, double size,
        double topMargin, IBrush? foreground = null, bool alignRight = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = Serif,
            Foreground = foreground ?? Brushes.Black,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            FontSize = size,
            HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(0, topMargin, 0, 2)
        };
        Grid.SetColumn(block, 0);
        Grid.SetColumnSpan(block, columnCount);
        Grid.SetRow(block, row);
        _host.Children.Add(block);
    }

    private static Border BuildHeaderCell(ProtocolPreviewColumn col, string caption)
    {
        var header = new Border
        {
            BorderBrush = HeaderBorder,
            BorderThickness = new Thickness(0.8),
            Padding = new Thickness(4, 2),
            // A transparent (non-null) background makes the WHOLE cell — not just the text — hit-testable, so
            // the drag works over empty space and empty columns too.
            Background = Brushes.Transparent,
            // The 4-arrow "move" cursor signals a column reorder by dragging.
            Cursor = new Cursor(StandardCursorType.SizeAll),
            DataContext = col,
            Child = new TextBlock
            {
                Text = caption,
                FontFamily = Serif,
                Foreground = Brushes.Black,
                FontWeight = FontWeight.Bold,
                FontSize = BodyFontSize,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        return header;
    }

    // A body cell carries its column in DataContext so it is ALSO a drag source/target — columns can be
    // reordered by dragging anywhere in the column, not just its header. Free-text columns wrap long values to
    // the next line (no ellipsis), matching the printed protocol; short-code columns (рік, № з/п, результат,
    // місце, кваліфікація, номер) never wrap — their column is always sized for the longest value.
    private static Border BuildBodyCell(string text, bool teamHeader, ProtocolPreviewColumn col, bool boxed) => new()
    {
        // Boxed (banded judges' protocol): a full cell grid, like the printed sheet — adjacent borders overlap to
        // a single rule. Otherwise just a hairline under each row (no inner grid), so normal sections read clean.
        BorderBrush = boxed ? HeaderBorder : new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE2)),
        BorderThickness = boxed ? new Thickness(0.8) : new Thickness(0, 0, 0, 0.5),
        Padding = new Thickness(4, 1.5),
        // Transparent (non-null) background so the entire cell area is hit-testable — the drag works over the
        // blank part of a cell and over empty cells, not only over the text glyphs.
        Background = Brushes.Transparent,
        Cursor = new Cursor(StandardCursorType.SizeAll), // 4-arrow move cursor
        DataContext = col,
        Child = new TextBlock
        {
            Text = text,
            FontFamily = Serif,
            Foreground = Brushes.Black,
            FontSize = BodyFontSize,
            FontWeight = teamHeader ? FontWeight.Bold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = col.BodyWraps ? TextWrapping.Wrap : TextWrapping.NoWrap
        }
    };

    // Makes a cell (header or body) a drag source + drop target for its column's reorder. Both kinds carry the
    // column in DataContext, so the same handlers serve both — letting the user grab a column anywhere in it.
    private void WireDrag(Border cell)
    {
        cell.PointerPressed += OnHeaderPointerPressed;
        cell.AddHandler(DragDrop.DragOverEvent, OnHeaderDragOver);
        cell.AddHandler(DragDrop.DropEvent, OnHeaderDrop);
        DragDrop.SetAllowDrop(cell, true);
    }

    // ── Column drag-reorder (from any cell in a column) ──────────────────────────────────────────────────

    private async void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: ProtocolPreviewColumn col } header)
            return;
        if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
            return;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ColumnFormat, col.Key));

        TintDraggedColumn(col.Key);
        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            ClearHighlight();
        }
    }

    private static bool TryGetDragged(DragEventArgs e, out string key)
    {
        if (e.DataTransfer.TryGetValue(ColumnFormat) is { } k)
        {
            key = k;
            return true;
        }
        key = string.Empty;
        return false;
    }

    private void OnHeaderDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: ProtocolPreviewColumn target } header ||
            _previewHost is not { } host || !TryGetDragged(e, out var draggedKey))
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;

        var targetIndex = IndexOfColumn(host, target.Key);
        if (targetIndex < 0)
            return;

        // Highlight the hovered column, keep the dragged column tinted, and place the insertion line at the
        // left or right edge of the hovered column depending on the pointer half.
        var insertAfter = e.GetPosition(header).X > header.Bounds.Width / 2;
        Highlight(draggedKey, targetIndex, insertAfter);
    }

    // Drop onto a column: place the dragged column before or after this one (the pointer's half decides). The
    // host resolves both keys against its full column list — so the move is unaffected by hidden columns. (The
    // previous version passed a raw index computed against the visible-only preview list, which landed wrong
    // whenever a column was hidden.)
    private void OnHeaderDrop(object? sender, DragEventArgs e)
    {
        ClearHighlight();

        if (sender is not Border { DataContext: ProtocolPreviewColumn target } header ||
            _previewHost is not { } host || !TryGetDragged(e, out var draggedKey))
            return;

        var insertAfter = e.GetPosition(header).X > header.Bounds.Width / 2;
        host.MoveColumnByKey(draggedKey, target.Key, insertAfter);
        e.Handled = true;
    }

    private static int IndexOfColumn(IProtocolPreviewHost host, string key)
    {
        for (var i = 0; i < host.Preview.Columns.Count; i++)
            if (host.Preview.Columns[i].Key == key)
                return i;
        return -1;
    }

    // ── Drag highlight ───────────────────────────────────────────────────────────────────────────────────

    // Tints the dragged column (a faint wash) at drag start, before any column is hovered.
    private void TintDraggedColumn(string draggedKey)
    {
        if (_previewHost is not { } host)
            return;
        ClearHighlight();
        var dragged = IndexOfColumn(host, draggedKey);
        if (dragged >= 0 && dragged < _columnCells.Count)
            FillColumn(dragged, DraggedColumnFill);
    }

    // Highlights the hovered (drop-target) column, keeps the dragged column tinted, and positions the
    // insertion line at the chosen edge of the hovered column.
    private void Highlight(string draggedKey, int targetIndex, bool insertAfter)
    {
        if (_previewHost is not { } host)
            return;
        ClearHighlight();

        var dragged = IndexOfColumn(host, draggedKey);
        if (dragged >= 0 && dragged < _columnCells.Count)
            FillColumn(dragged, DraggedColumnFill);
        if (targetIndex >= 0 && targetIndex < _columnCells.Count)
            FillColumn(targetIndex, DropColumnFill);

        if (_dropLine is { } line)
        {
            Grid.SetColumn(line, targetIndex);
            line.HorizontalAlignment = insertAfter ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            line.Margin = insertAfter ? new Thickness(0, 0, -1.5, 0) : new Thickness(-1.5, 0, 0, 0);
            line.IsVisible = true;
        }
    }

    private void FillColumn(int columnIndex, IBrush brush)
    {
        foreach (var cell in _columnCells[columnIndex])
            cell.Background = brush;
    }

    private void ClearHighlight()
    {
        foreach (var column in _columnCells)
            foreach (var cell in column)
                // Reset to transparent (NOT null) so cells stay fully hit-testable for the next drag.
                cell.Background = Brushes.Transparent;
        if (_dropLine is { } line)
            line.IsVisible = false;
    }
}
