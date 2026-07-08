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

    /// <summary>
    /// A digits-only text field edited directly on the row by its <see cref="SheetColumn.IdentityPath"/>
    /// (like <see cref="IdentityText"/> but only accepts digits). Used by the day-mode chip column.
    /// </summary>
    ChipText,

    /// <summary>
    /// An identity text field (like <see cref="IdentityText"/>) that background-tints by the row's
    /// <c>PaymentStatus</c> — how «Оплата» compares to the computed total fee. Used by the participant
    /// payment column; see <see cref="OrientPyx.Presentation.Behaviors.PaymentHighlight"/>.
    /// </summary>
    PaymentText,

    /// <summary>A start-time text field (HH:mm) edited directly on the row by its
    /// <see cref="SheetColumn.IdentityPath"/>. Used by the day-mode start-time column.</summary>
    StartTimeText,

    /// <summary>
    /// A signed-integer text field (optional '-' then digits) edited directly on the row by its
    /// <see cref="SheetColumn.IdentityPath"/>. Used by the day-mode «бонус» points-correction column;
    /// only present on point-scoring days.
    /// </summary>
    RowBonus,

    /// <summary>The competition-level birth date (CalendarDatePicker).</summary>
    BirthDate,

    /// <summary>A single day's group (ComboBox of <see cref="GroupOption"/>), bound to Days[i].</summary>
    Group,

    /// <summary>A single day's chip (TextBox), bound to Days[i].</summary>
    Chip,

    /// <summary>A single day's start time (TextBox HH:mm), bound to Days[i].</summary>
    StartTime,

    /// <summary>A single day's "out of competition" flag (CheckBox), bound to Days[i].</summary>
    OutOfCompetition,

    /// <summary>A single day's «бонус» points correction (signed-integer TextBox), bound to Days[i].
    /// Disabled/greyed for non-members. The roster «Бонус» column.</summary>
    Bonus,

    /// <summary>
    /// A group ComboBox bound directly on the row (GroupOptions/SelectedGroup), not via Days[i].
    /// Used by the flat day-mode table where each row already represents a single day.
    /// </summary>
    RowGroup,

    /// <summary>
    /// A region ComboBox bound directly on the row (RegionOptions/SelectedRegion). Region is a
    /// competition-level participant field, so it is one column for the whole row (both the day grid
    /// and the roster). The dropdown carries a "(none)" sentinel and a trailing "+ new" option.
    /// </summary>
    RowRegion,

    /// <summary>A club ComboBox bound directly on the row (ClubOptions/SelectedClub); same shape as
    /// <see cref="RowRegion"/>.</summary>
    RowClub,

    /// <summary>A sports-school (ДЮСШ) ComboBox bound directly on the row (DusshOptions/SelectedDussh);
    /// same shape as <see cref="RowRegion"/>.</summary>
    RowDussh,

    /// <summary>
    /// A rank ComboBox bound directly on the row (RankOptions/SelectedRank). Rank is a competition-level
    /// participant field stored as text (the rank name); the dropdown carries a "(none)" sentinel and
    /// offers the application-level rank list, plus the participant's own value when it is not in the
    /// list (old/renamed). Unlike region/club it has no "+ new" option — ranks are edited on their page.
    /// </summary>
    RowRank,

    /// <summary>
    /// A competition-level boolean field (CheckBox) edited on the row by its
    /// <see cref="SheetColumn.IdentityPath"/>. Used by the participant "FSOU member" column.
    /// </summary>
    IdentityBool,

    /// <summary>A collapsed block's merged group cell (combo when shared, "різні" when days differ).</summary>
    CollapsedGroup,

    /// <summary>A collapsed block's merged chip cell (input when shared, "різні" when days differ).</summary>
    CollapsedChip,

    /// <summary>A collapsed block's merged start-time cell (input when shared, "різні" when days differ).</summary>
    CollapsedStartTime,

    /// <summary>A collapsed block's merged out-of-competition cell (CheckBox when shared, "різні" when days differ).</summary>
    CollapsedOutOfCompetition,

    /// <summary>
    /// A CheckBox bound to the row's <c>PaysRaisedFee</c> — marks a participant as paying the raised
    /// (late) start-entry fee. Only present when the competition has the raised fee enabled.
    /// </summary>
    RaisedFeeFlag,

    /// <summary>
    /// A read-only, right-aligned money label bound to the row's computed total entry fee
    /// (<c>FormattedTotalFee</c>). Placed at the end of the participants table.
    /// </summary>
    TotalFee,

    /// <summary>
    /// A read-only computed-result text label bound directly on the row by its
    /// <see cref="SheetColumn.IdentityPath"/> (e.g. "FinishText"). Used by the day-grid result columns.
    /// </summary>
    RowResultText,

    /// <summary>
    /// A finish-status ComboBox bound directly on the row (StatusOptions/SelectedStatus). The day-grid
    /// result status column; lets a judge override the computed status.
    /// </summary>
    RowStatus,

    /// <summary>
    /// A read-only computed-result text label for a single day, bound to <c>Days[i].{IdentityPath}</c>.
    /// Greyed on days the participant doesn't run. The roster result columns.
    /// </summary>
    ResultText,

    /// <summary>
    /// A finish-status ComboBox for a single day, bound to <c>Days[i].StatusOptions/SelectedStatus</c>.
    /// Disabled/greyed for non-members. The roster result status column.
    /// </summary>
    Status,

    /// <summary>
    /// A collapsed result block's merged read-only text cell: the shared per-day value, or "різні" when
    /// the member days disagree. The column's <see cref="SheetColumn.IdentityPath"/> carries the roster
    /// row's merged-text property; a parallel <c>*Differs</c> property drives the "різні" state.
    /// </summary>
    CollapsedResultText,

    /// <summary>A collapsed result-status block's merged read-only cell (shared status code, or "різні").</summary>
    CollapsedStatus,

    /// <summary>The trailing delete-action button column.</summary>
    Actions,

    /// <summary>
    /// A page-supplied cell: the column carries a <see cref="SheetColumn.CellBuilder"/> that builds
    /// the editor/display control. This is how non-participant pages (control points, groups, days,
    /// chips) reuse the table without baking their bindings into <see cref="RosterCellFactory"/>.
    /// </summary>
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
    /// Optional per-cell background tint: a bool property on the bound row that, when true, paints the
    /// whole cell in <see cref="CellBackgroundBrush"/> (false / not set ⇒ transparent, hit-testable).
    /// Tints the whole cell area — including empty space — the same way the payment/age columns do, but
    /// generically for any column. Null ⇒ no tint. Pair with <see cref="CellBackgroundTooltipPath"/> to
    /// explain the tint on hover.
    /// </summary>
    public string? CellBackgroundPath { get; set; }

    /// <summary>The brush painted over a cell whose <see cref="CellBackgroundPath"/> reads true.</summary>
    public Avalonia.Media.IBrush? CellBackgroundBrush { get; set; }

    /// <summary>Optional string property read for the tinted cell's tooltip (empty ⇒ no tooltip). See <see cref="CellBackgroundPath"/>.</summary>
    public string? CellBackgroundTooltipPath { get; set; }

    /// <summary>
    /// The two-way bindable string property on the bound row this column edits, when the column is a
    /// plain editable text cell. Set, it enables multi-row "fill down" paste: a clipboard with several
    /// newline-separated lines pasted onto a cell writes one line per successive row straight to this
    /// property (reusing the property's normal change/save handling). Null ⇒ the column has no flat
    /// text value (combo/date/custom cell) and paste stays single-cell.
    /// </summary>
    public string? PastePath { get; set; }

    /// <summary>
    /// Combo-paste descriptor for an option-list column (region/club/group/rank/status). When set, a
    /// paste (single value or a multi-line fill-down) onto this column does NOT write raw text: it
    /// resolves the pasted text against the row's options (<see cref="ComboItemsPath"/>) by their visible
    /// label (<see cref="ComboLabelPath"/>) and assigns the matching option to <see cref="ComboSelectedPath"/>
    /// ONLY when exactly one option matches 1:1 (case-insensitive, trimmed). A non-matching value leaves
    /// the cell unchanged — so pasting can never invent or half-match a selection. Null ⇒ not a combo column.
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
    /// Property path on the bound row to a numeric value this column sums in the table's status bar.
    /// When set, the status bar shows the total of this value across the currently displayed (filtered)
    /// rows, right-aligned under this column. The value may be a number (decimal/int/double) or a
    /// numeric string (e.g. the free-text «Оплата» field) — the table parses it leniently. Empty ⇒ the
    /// column has no footer sum. See the participant fee/payment columns.
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
