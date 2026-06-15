using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Controls;

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

    /// <summary>A start-time text field (HH:mm) edited directly on the row by its
    /// <see cref="SheetColumn.IdentityPath"/>. Used by the day-mode start-time column.</summary>
    StartTimeText,

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

    /// <summary>True when this column can carry a filter (it exposes a value path and isn't the actions column).</summary>
    public bool Filterable => Kind != SheetCellKind.Actions && !string.IsNullOrEmpty(FilterPath);

    /// <summary>Default starting width for content columns the builder doesn't fix explicitly.</summary>
    public const double DefaultWidth = 130;

    /// <summary>Smallest width the resize grip allows.</summary>
    public double MinWidth { get; set; } = 48;

    /// <summary>True when the builder set an explicit width (vs. the default), used when carrying widths.</summary>
    public bool WidthCapped { get; set; }

    /// <summary>
    /// For <see cref="SheetCellKind.Custom"/> columns: builds the cell's content control. Called
    /// once per realized row; the produced control inherits the row's DataContext, so its bindings
    /// resolve against the bound row view model. Ignored for every other kind.
    /// </summary>
    public System.Func<Avalonia.Controls.Control>? CellBuilder { get; set; }

    /// <summary>
    /// The two-way bindable string property on the bound row this column edits, when the column is a
    /// plain editable text cell. Set, it enables multi-row "fill down" paste: a clipboard with several
    /// newline-separated lines pasted onto a cell writes one line per successive row straight to this
    /// property (reusing the property's normal change/save handling). Null ⇒ the column has no flat
    /// text value (combo/date/custom cell) and paste stays single-cell.
    /// </summary>
    public string? PastePath { get; set; }

    /// <summary>
    /// True when this column holds a SportIdent chip number whose rental status can be toggled. The
    /// table's right-click menu (which already owns the rental registry and toggle command) appends a
    /// "mark (non-)rental" item to the default filter menu for such columns — so the rental toggle is a
    /// table-level menu extra, not a competing context menu on the cell. See the day/roster chip columns.
    /// </summary>
    public bool RentalChipColumn { get; set; }
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
