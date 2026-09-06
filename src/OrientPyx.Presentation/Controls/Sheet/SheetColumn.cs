using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Controls;

/// <summary>
/// What a roster cell renders/edits. Drives <see cref="RosterCellFactory"/> and tells the table
/// which value path on the bound row the cell reads.
/// </summary>
public enum SheetCellKind
{
    /// <summary>A competition-level identity text field (surname, name, …) edited on the row.</summary>
    IdentityText,

    /// <summary>Digits-only <see cref="IdentityText"/>; the day-mode chip column.</summary>
    ChipText,

    /// <summary><see cref="IdentityText"/> tinted by the row's <c>PaymentStatus</c> («Оплата» vs total fee);
    /// see <see cref="OrientPyx.Presentation.Behaviors.PaymentHighlight"/>.</summary>
    PaymentText,

    /// <summary>HH:mm on the row's <see cref="SheetColumn.IdentityPath"/>; the day-mode start-time column.</summary>
    StartTimeText,

    /// <summary>Signed integer on the row's path — the day-mode «бонус» column, point-scoring days only.</summary>
    RowBonus,

    /// <summary>The competition-level birth date (CalendarDatePicker).</summary>
    BirthDate,

    /// <summary>A single day's group (ComboBox of <see cref="GroupOption"/>), bound to Days[i].</summary>
    Group,

    Chip,

    /// <summary>A single day's start time (TextBox HH:mm), bound to Days[i].</summary>
    StartTime,

    /// <summary>A single day's "out of competition" flag (CheckBox), bound to Days[i].</summary>
    OutOfCompetition,

    /// <summary>Days[i] «бонус» (signed int); greyed for non-members. The roster column.</summary>
    Bonus,

    /// <summary>Group combo on the row (GroupOptions/SelectedGroup), not Days[i] — the flat day-mode table.</summary>
    RowGroup,

    /// <summary>Region combo on the row; competition-level, so one column in both grids.
    /// Dropdown carries a "(none)" sentinel and a trailing "+ new".</summary>
    RowRegion,

    /// <summary>Same shape as <see cref="RowRegion"/>.</summary>
    RowClub,

    /// <summary>ДЮСШ; same shape as <see cref="RowRegion"/>.</summary>
    RowDussh,

    /// <summary>Rank combo on the row. Stored as text (the rank name); lists the app-level ranks plus the
    /// participant's own value when it is not among them. No "+ new" — ranks are edited on their own page.</summary>
    RowRank,

    /// <summary>CheckBox on the row's path; the participant "FSOU member" column.</summary>
    IdentityBool,

    /// <summary>A collapsed block's merged group cell (combo when shared, "різні" when days differ).</summary>
    CollapsedGroup,

    /// <summary>A collapsed block's merged chip cell (input when shared, "різні" when days differ).</summary>
    CollapsedChip,

    /// <summary>A collapsed block's merged start-time cell (input when shared, "різні" when days differ).</summary>
    CollapsedStartTime,

    /// <summary>A collapsed block's merged out-of-competition cell (CheckBox when shared, "різні" when days differ).</summary>
    CollapsedOutOfCompetition,

    /// <summary>Row's <c>PaysRaisedFee</c> (late entry fee); only when the competition enables it.</summary>
    RaisedFeeFlag,

    /// <summary>Read-only <c>FormattedTotalFee</c>, right-aligned; last in the participants table.</summary>
    TotalFee,

    /// <summary>Read-only label on the row's path (e.g. "FinishText") — the day-grid result columns.</summary>
    RowResultText,

    /// <summary>Status combo on the row — lets a judge override the computed status.</summary>
    RowStatus,

    /// <summary>Read-only <c>Days[i].{IdentityPath}</c>; greyed on days the participant doesn't run.</summary>
    ResultText,

    /// <summary>Days[i] status combo; greyed for non-members.</summary>
    Status,

    /// <summary>Merged read-only cell: the shared value, or "різні" when member days disagree.
    /// <see cref="SheetColumn.IdentityPath"/> is the merged-text property; a parallel <c>*Differs</c> drives "різні".</summary>
    CollapsedResultText,

    /// <summary>A collapsed result-status block's merged read-only cell (shared status code, or "різні").</summary>
    CollapsedStatus,

    /// <summary>The trailing delete-action button column.</summary>
    Actions,

    /// <summary>Page-supplied cell via <see cref="SheetColumn.CellBuilder"/> — how non-participant pages reuse
    /// the table without baking bindings into <see cref="RosterCellFactory"/>.</summary>
    Custom
}

/// <summary>
/// One leaf column in the roster table — the unit a header sub-cell and every body cell line up on.
/// Width is observable so the header and all (virtualized) rows share a single live width: the
/// resize grip writes here and every realized cell re-binds instantly. Pure presentation DTO.
/// </summary>
public sealed partial class SheetColumn : ObservableObject
{
    public SheetColumn(SheetCellKind kind)
    {
        Kind = kind;
    }

    /// <summary>What the cell renders/edits.</summary>
    public SheetCellKind Kind { get; }

    /// <summary>Resolved (already localized) sub-header text — e.g. "День 2", or an identity label.</summary>
    public string Header { get; set; } = string.Empty;

    /// <summary>
    /// Day index into the row's <c>Days</c> collection for per-day cells (Group/Chip); ignored for
    /// identity/collapsed/action columns.
    /// </summary>
    public int DayIndex { get; set; }

    /// <summary>
    /// Identity property name for <see cref="SheetCellKind.IdentityText"/> columns (e.g. "Surname"),
    /// used as the cell's two-way binding path on the row.
    /// </summary>
    public string IdentityPath { get; set; } = string.Empty;

    /// <summary>
    /// Property path on the bound row to sort by when this column's header is clicked. Empty ⇒ the
    /// column is not sortable (e.g. the actions column). For identity text this is the same as
    /// <see cref="IdentityPath"/>; per-day cells point at <c>Days[i].SortKey</c>-style paths.
    /// </summary>
    public string SortPath { get; set; } = string.Empty;

    /// <summary>
    /// The live, shared column width in pixels. The resize grip writes it; the header sub-cell and
    /// every (virtualized) body cell bind to it, so a drag resizes the whole column at once.
    /// </summary>
    [ObservableProperty]
    private double _width = DefaultWidth;

    /// <summary>
    /// True when the user has hidden this column. The header/row builders skip hidden leaves (a band
    /// with no visible leaf is dropped entirely). Observable so a toggle from the columns picker /
    /// header context menu rebuilds the table live. State is in-memory only.
    /// </summary>
    [ObservableProperty]
    private bool _isHidden;

    /// <summary>
    /// A stable identity for this column across rebuilds (collapse/expand, language change), so a
    /// hidden-column set survives them. Built from the kind plus the discriminating path/day index —
    /// NOT the header text, which is localized and changes with the language. The builder may override
    /// it (e.g. day-mode custom columns share one kind and need their header to disambiguate).
    /// </summary>
    public string Key
    {
        get => _key ??= $"{Kind}:{IdentityPath}:{SortPath}:{DayIndex}";
        set => _key = value;
    }
    private string? _key;

    /// <summary>
    /// A human-readable label for this column in the columns picker / context menu. Falls back to the
    /// header text; day sub-columns combine their band label so "День 1" reads unambiguously.
    /// </summary>
    public string PickerLabel { get; set; } = string.Empty;

    /// <summary>
    /// Property path on the bound row whose value the column is filtered by. Defaults to
    /// <see cref="SortPath"/> (the value sorting already uses); combo columns set it explicitly to the
    /// selected option's label path so filtering matches the visible text even though the column may
    /// not be sortable. Empty ⇒ the column cannot be filtered (e.g. the actions column).
    /// </summary>
    public string FilterPath
    {
        get => string.IsNullOrEmpty(_filterPath) ? SortPath : _filterPath;
        set => _filterPath = value;
    }
    private string _filterPath = string.Empty;

    /// <summary>
    /// Property path on the bound row whose value <b>copy</b> (Ctrl+C / range copy) reads, when the row
    /// is scrolled off-screen and its cell isn't realized. Defaults to <see cref="FilterPath"/>. Set it
    /// where the filter value differs from what the cell shows — e.g. the payment column filters by a
    /// status token but must copy the actual payment amount, and collapsed roster cells copy their
    /// merged display value. A <see cref="bool"/> value copies as a compact flag mark.
    /// </summary>
    public string CopyPath
    {
        get => string.IsNullOrEmpty(_copyPath) ? FilterPath : _copyPath;
        set => _copyPath = value;
    }
    private string _copyPath = string.Empty;

    /// <summary>True when this column can carry a filter (it exposes a value path and isn't the actions column).</summary>
    public bool Filterable => Kind != SheetCellKind.Actions && !string.IsNullOrEmpty(FilterPath);

    /// <summary>
    /// True when the filter popup should offer the "by status" mode (payment-status categories: empty /
    /// over / under / equal / not-a-number). Opt-in; set by the participant payment column. Its
    /// <see cref="FilterPath"/> must read a <c>PaymentStatus</c> token (the row's <c>PaymentStatusKey</c>).
    /// </summary>
    public bool StatusFilter { get; set; }

    /// <summary>Default starting width for content columns the builder doesn't fix explicitly.</summary>
    public const double DefaultWidth = 130;

    /// <summary>
    /// Smallest width the resize grip allows for any column — narrow enough that only the header's
    /// sort button stays visible (the label trims to nothing), so every column can be squeezed down
    /// to reveal/keep its sort handle. 10px left pad + the ~21px sort button + its right margin.
    /// </summary>
    public const double SortHandleMinWidth = 40;

    /// <summary>Smallest width the resize grip allows.</summary>
    public double MinWidth { get; set; } = SortHandleMinWidth;

    /// <summary>True when the builder set an explicit width (vs. the default), used when carrying widths.</summary>
    public bool WidthCapped { get; set; }

    /// <summary>
    /// For <see cref="SheetCellKind.Custom"/> columns: builds the cell's content control. Called
    /// once per realized row; the produced control inherits the row's DataContext, so its bindings
    /// resolve against the bound row view model. Ignored for every other kind.
    /// </summary>
    public System.Func<Avalonia.Controls.Control>? CellBuilder { get; set; }

    /// <summary>
    /// Bool property on the row: true ⇒ the whole cell (empty space included) is painted in
    /// <see cref="CellBackgroundBrush"/>, else transparent and hit-testable. Null ⇒ no tint.
    /// Pair with <see cref="CellBackgroundTooltipPath"/> to explain the tint on hover.
    /// </summary>
    public string? CellBackgroundPath { get; set; }

    /// <summary>The brush painted over a cell whose <see cref="CellBackgroundPath"/> reads true.</summary>
    public Avalonia.Media.IBrush? CellBackgroundBrush { get; set; }

    /// <summary>
    /// Optional per-cell background tint chosen by the row itself: an <see cref="Avalonia.Media.IBrush"/>
    /// (or null) property on the bound row painted over the whole cell — for a column that needs more than
    /// one colour (e.g. red for MP, yellow for any other bad finish status). Takes precedence over
    /// <see cref="CellBackgroundPath"/> when both are set. Pair with <see cref="CellBackgroundTooltipPath"/>.
    /// </summary>
    public string? CellBackgroundBrushPath { get; set; }

    /// <summary>
    /// Optional converter applied to <see cref="CellBackgroundBrushPath"/> so the bound property can be a
    /// non-brush value (e.g. a status enum) the view maps to a brush — keeping brushes out of the row VM.
    /// Null ⇒ the bound property is already an <see cref="Avalonia.Media.IBrush"/>.
    /// </summary>
    public Avalonia.Data.Converters.IValueConverter? CellBackgroundBrushConverter { get; set; }

    /// <summary>Optional string property read for the tinted cell's tooltip (empty ⇒ no tooltip). See <see cref="CellBackgroundPath"/>.</summary>
    public string? CellBackgroundTooltipPath { get; set; }

    /// <summary>
    /// Two-way string property on the row, for a plain text cell. Set ⇒ multi-row "fill down" paste:
    /// one clipboard line per successive row. Null ⇒ not a flat text column; paste stays single-cell.
    /// </summary>
    public string? PastePath { get; set; }

    /// <summary>
    /// Option-list column (region/club/group/rank/status). Paste resolves the text against the row's
    /// options by <see cref="ComboLabelPath"/> and assigns <see cref="ComboSelectedPath"/> ONLY on an
    /// exact 1:1 match (case-insensitive, trimmed) — a non-match leaves the cell alone, so paste can
    /// never invent a selection. Null ⇒ not a combo column.
    /// </summary>
    public string? ComboItemsPath { get; set; }

    /// <summary>The two-way selected-option path on the row, for combo paste. See <see cref="ComboItemsPath"/>.</summary>
    public string? ComboSelectedPath { get; set; }

    /// <summary>The label property on each option used to match a pasted value, for combo paste. See <see cref="ComboItemsPath"/>.</summary>
    public string? ComboLabelPath { get; set; }

    /// <summary>True when this column resolves pastes to an option by exact label match.</summary>
    public bool IsComboPaste => ComboItemsPath is not null && ComboSelectedPath is not null && ComboLabelPath is not null;

    /// <summary>
    /// True when this column holds a SportIdent chip number whose rental status can be toggled. The
    /// table's right-click menu (which already owns the rental registry and toggle command) appends a
    /// "mark (non-)rental" item to the default filter menu for such columns — so the rental toggle is a
    /// table-level menu extra, not a competing context menu on the cell. See the day/roster chip columns.
    /// </summary>
    public bool RentalChipColumn { get; set; }

    /// <summary>
    /// Row property summed in the status bar over the currently displayed (filtered) rows. Accepts a
    /// number or a numeric string (e.g. the free-text «Оплата»), parsed leniently. Empty ⇒ no sum.
    /// </summary>
    public string SummaryPath { get; set; } = string.Empty;

    /// <summary>True when this column contributes a sum to the status bar.</summary>
    public bool HasSummary => !string.IsNullOrEmpty(SummaryPath);

    /// <summary>
    /// For a payment-summary column: the property path to the per-row owed amount (the computed total
    /// entry fee). When set, the status bar shows a hover tooltip on this column's sum breaking the
    /// numbers down into already-paid (the sum) and still-owed (this total minus the payment, never
    /// below zero, summed over the displayed rows). Empty ⇒ the sum has no tooltip. See the payment column.
    /// </summary>
    public string SummaryOwedPath { get; set; } = string.Empty;

    /// <summary>True when this column's status-bar sum carries a paid/owed breakdown tooltip.</summary>
    public bool HasSummaryOwed => !string.IsNullOrEmpty(SummaryOwedPath);

    /// <summary>
    /// True when the status bar renders the row-count ("shown / total") under this column rather than in
    /// a separate fixed area. Set on the leading «Номер» column so the count sits under it, compact
    /// ("4 з 344") with the full text ("Показано 4 з 344") on hover. Opt-in; the participants tables only.
    /// </summary>
    public bool ShowCount { get; set; }

    /// <summary>
    /// Property path on the bound row (or <c>Days[i].…</c> for a per-day result cell) to a string the cell
    /// shows as a hover tooltip. Set on the «Бали» result column to show the per-control score breakdown.
    /// Empty ⇒ no per-cell tooltip. Only honoured by the read-only result-text cells.
    /// </summary>
    public string ToolTipPath { get; set; } = string.Empty;
}

/// <summary>
/// A top-level header unit: either a single ungrouped identity column (spanning both header tiers)
/// or a collapsible field block (a band over one column per day, or one merged column when
/// collapsed). The band is the unit that drag-reorders as a whole in v2.
/// </summary>
public sealed class SheetBand
{
    public enum BandKind
    {
        /// <summary>An ungrouped column; its header spans both tiers (no sub-row).</summary>
        Identity,

        /// <summary>A field block (Groups/Chips); a band label over its day sub-columns.</summary>
        FieldBlock
    }

    public SheetBand(BandKind kind, IReadOnlyList<SheetColumn> columns)
    {
        Kind = kind;
        Columns = columns;
    }

    public BandKind Kind { get; }

    /// <summary>The leaf columns under this band, left-to-right.</summary>
    public IReadOnlyList<SheetColumn> Columns { get; }

    /// <summary>Resolved (localized) band/identity header text shown on the top tier.</summary>
    public string Header { get; set; } = string.Empty;

    /// <summary>The source field block, for the collapse toggle; null for identity bands.</summary>
    public RosterFieldBlockViewModel? Block { get; set; }

    /// <summary>True when this is a field block currently collapsed to one merged column.</summary>
    public bool IsCollapsed => Block?.IsCollapsed ?? false;
}
