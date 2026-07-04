using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Reactive;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Controls;

/// <summary>
/// The app's shared editable table — a spreadsheet-style grid built imperatively from a
/// <see cref="SheetColumn"/>/<see cref="SheetBand"/> model. Every editable screen (control points,
/// groups, days, chips, and the participant roster) uses it; it replaced the Avalonia-DataGrid-based
/// <c>SheetDataGrid</c>.
///
/// Most pages build a flat one-tier header with <see cref="SheetColumnBuilder"/>. The participant
/// roster ("Мандатка") additionally uses the feature the DataGrid could not express: a true two-tier
/// (banded) header where per-day field columns sit under a spanning band label
/// (see <c>RosterColumnBuilder</c>). Rows are virtualized by an inner <see cref="ListBox"/>; the
/// header is frozen vertically and scrolls horizontally in lockstep with the body via one shared
/// outer scroller. Excel-style selection + focus-to-edit and Delete-with-confirmation are built in.
/// </summary>
public sealed class SheetTable : TemplatedControl
{
    // ── Bindable properties ───────────────────────────────────────────────────────────────────────
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<SheetTable, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<ILocalizationService?> LocalizationProperty =
        AvaloniaProperty.Register<SheetTable, ILocalizationService?>(nameof(Localization));

    public static readonly StyledProperty<IReadOnlyList<EventDay>?> DaysProperty =
        AvaloniaProperty.Register<SheetTable, IReadOnlyList<EventDay>?>(nameof(Days));

    public static readonly StyledProperty<IEnumerable<RosterFieldBlockViewModel>?> BlocksProperty =
        AvaloniaProperty.Register<SheetTable, IEnumerable<RosterFieldBlockViewModel>?>(nameof(Blocks));

    public static readonly StyledProperty<ICommand?> ToggleBlockCommandProperty =
        AvaloniaProperty.Register<SheetTable, ICommand?>(nameof(ToggleBlockCommand));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<SheetTable, object?>(nameof(SelectedItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<ICommand?> DeleteCommandProperty =
        AvaloniaProperty.Register<SheetTable, ICommand?>(nameof(DeleteCommand));

    /// <summary>The shared rental-chip set chip cells highlight against (bold-red when not rental).</summary>
    public static readonly StyledProperty<RentalChipRegistry?> RentalChipsProperty =
        AvaloniaProperty.Register<SheetTable, RentalChipRegistry?>(nameof(RentalChips));

    /// <summary>Command invoked (with the chip number) when a chip cell is double-clicked, to toggle it in the rental DB.</summary>
    public static readonly StyledProperty<ICommand?> ToggleRentalChipCommandProperty =
        AvaloniaProperty.Register<SheetTable, ICommand?>(nameof(ToggleRentalChipCommand));

    /// <summary>
    /// Property path to a <see cref="bool"/> on the bound row; when true, every cell's background in
    /// that row is tinted red (e.g. an unrecognised chip on the finish-read log). Null ⇒ no row tint.
    /// </summary>
    public static readonly StyledProperty<string?> RowHighlightPathProperty =
        AvaloniaProperty.Register<SheetTable, string?>(nameof(RowHighlightPath));

    /// <summary>
    /// Pre-built bands supplied by the caller. When set, the table renders these verbatim and does
    /// NOT build columns from <see cref="Days"/>/<see cref="Blocks"/> — this is how the flat day-mode
    /// table reuses the control without the roster's per-day banding.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<SheetBand>?> BandsProperty =
        AvaloniaProperty.Register<SheetTable, IReadOnlyList<SheetBand>?>(nameof(Bands));

    /// <summary>The competition's entry-fee discounts; drives the per-discount checkbox columns the
    /// roster auto-builds. Ignored when <see cref="Bands"/> is supplied (the day-mode table bakes the
    /// fee columns into its bands itself).</summary>
    public static readonly StyledProperty<IReadOnlyList<EntryFeeDiscount>?> DiscountsProperty =
        AvaloniaProperty.Register<SheetTable, IReadOnlyList<EntryFeeDiscount>?>(nameof(Discounts));

    /// <summary>Whether the raised-fee flag column is shown (roster auto-build only).</summary>
    public static readonly StyledProperty<bool> RaisedFeeEnabledProperty =
        AvaloniaProperty.Register<SheetTable, bool>(nameof(RaisedFeeEnabled));

    /// <summary>Whether the team column is shown (roster auto-build only; team disciplines).</summary>
    public static readonly StyledProperty<bool> ShowTeamProperty =
        AvaloniaProperty.Register<SheetTable, bool>(nameof(ShowTeam));

    /// <summary>Whether the «Бали» (score) result column is shown (roster auto-build only; point-scoring days).</summary>
    public static readonly StyledProperty<bool> ShowScoreProperty =
        AvaloniaProperty.Register<SheetTable, bool>(nameof(ShowScore));

    /// <summary>
    /// A stable id identifying this table's saved view in <c>views.json</c> (e.g. "participants.day").
    /// Persistence is active only when both this and <see cref="LayoutStore"/> are set; otherwise the
    /// table's column order/width/visibility stay in-memory only (the historical behaviour).
    /// </summary>
    public static readonly StyledProperty<string?> LayoutKeyProperty =
        AvaloniaProperty.Register<SheetTable, string?>(nameof(LayoutKey));

    /// <summary>The store that loads/saves this table's view (per-competition <c>views.json</c>).</summary>
    public static readonly StyledProperty<ITableLayoutStore?> LayoutStoreProperty =
        AvaloniaProperty.Register<SheetTable, ITableLayoutStore?>(nameof(LayoutStore));

    /// <summary>
    /// Page-supplied free text shown on the right of the status bar (e.g. "chips in rental: 12,
    /// unassigned: 3"). The status bar always shows the row counts and any per-column sums; this is an
    /// extra system-info line the page fills. Setting it (or any summary column existing) makes the bar
    /// visible.
    /// </summary>
    public static readonly StyledProperty<string?> StatusInfoProperty =
        AvaloniaProperty.Register<SheetTable, string?>(nameof(StatusInfo));

    public string? StatusInfo
    {
        get => GetValue(StatusInfoProperty);
        set => SetValue(StatusInfoProperty, value);
    }

    /// <summary>Raised when the user asks to delete a row via the keyboard; arg = skip-confirm.</summary>
    public event EventHandler<SheetDeleteEventArgs>? DeleteRequested;

    /// <summary>
    /// Raised when the user picks "bulk edit this column" from a header's context menu; arg = the leaf
    /// column. The page opens its bulk-edit modal with that column preselected. Only fires for columns
    /// <see cref="CanBulkEditColumn"/> accepts (the menu item is hidden otherwise).
    /// </summary>
    public event EventHandler<SheetColumn>? BulkEditColumnRequested;

    /// <summary>
    /// Predicate the header menu consults before offering its "bulk edit this column" item, set by the
    /// page that owns which columns can be bulk-edited. Null ⇒ no column offers the item.
    /// </summary>
    public Func<SheetColumn, bool>? CanBulkEditColumn { get; set; }

    /// <summary>
    /// The leaf column the focused cell belongs to, or null when nothing in the table is focused. The
    /// page reads it to preselect the bulk-edit field for the cell the user was last in.
    /// </summary>
    public SheetColumn? FocusedColumn => FindFocusedCell()?.Column;

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public ILocalizationService? Localization
    {
        get => GetValue(LocalizationProperty);
        set => SetValue(LocalizationProperty, value);
    }

    public IReadOnlyList<EventDay>? Days
    {
        get => GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    public IEnumerable<RosterFieldBlockViewModel>? Blocks
    {
        get => GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    public ICommand? ToggleBlockCommand
    {
        get => GetValue(ToggleBlockCommandProperty);
        set => SetValue(ToggleBlockCommandProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    /// <summary>Delete the row passed as command parameter (confirm flow lives in the VM command).</summary>
    public ICommand? DeleteCommand
    {
        get => GetValue(DeleteCommandProperty);
        set => SetValue(DeleteCommandProperty, value);
    }

    /// <summary>Caller-supplied bands; when set, used instead of building from Days/Blocks.</summary>
    public IReadOnlyList<SheetBand>? Bands
    {
        get => GetValue(BandsProperty);
        set => SetValue(BandsProperty, value);
    }

    public IReadOnlyList<EntryFeeDiscount>? Discounts
    {
        get => GetValue(DiscountsProperty);
        set => SetValue(DiscountsProperty, value);
    }

    public bool RaisedFeeEnabled
    {
        get => GetValue(RaisedFeeEnabledProperty);
        set => SetValue(RaisedFeeEnabledProperty, value);
    }

    public bool ShowTeam
    {
        get => GetValue(ShowTeamProperty);
        set => SetValue(ShowTeamProperty, value);
    }

    public bool ShowScore
    {
        get => GetValue(ShowScoreProperty);
        set => SetValue(ShowScoreProperty, value);
    }

    public string? LayoutKey
    {
        get => GetValue(LayoutKeyProperty);
        set => SetValue(LayoutKeyProperty, value);
    }

    public ITableLayoutStore? LayoutStore
    {
        get => GetValue(LayoutStoreProperty);
        set => SetValue(LayoutStoreProperty, value);
    }

    public RentalChipRegistry? RentalChips
    {
        get => GetValue(RentalChipsProperty);
        set => SetValue(RentalChipsProperty, value);
    }

    public ICommand? ToggleRentalChipCommand
    {
        get => GetValue(ToggleRentalChipCommandProperty);
        set => SetValue(ToggleRentalChipCommandProperty, value);
    }

    public string? RowHighlightPath
    {
        get => GetValue(RowHighlightPathProperty);
        set => SetValue(RowHighlightPathProperty, value);
    }

    // ── Template parts ────────────────────────────────────────────────────────────────────────────
    private SheetHeaderPanel? _header;
    private ListBox? _body;
    private ScrollViewer? _headerScroll;
    private ScrollViewer? _bodyScroll;
    private ScrollViewer? _statusScroll;
    private SheetStatusPanel? _statusPanel;
    private Border? _statusBar;
    private Border? _statusSumsRow;
    private TextBlock? _statusCounts;
    private TextBlock? _statusInfo;
    private TextBox? _search;
    private Button? _searchClear;

    // The full band set as built (every column, hidden or not) — the input to filtering, the basis for
    // reorder/width carry. _visibleBands is what the header and rows actually render: the same bands
    // with hidden leaves dropped (and any band left with no visible leaf removed).
    private IReadOnlyList<SheetBand> _bands = [];
    private IReadOnlyList<SheetBand> _visibleBands = [];
    private RosterCellFactory? _cellFactory;
    private bool _editing;

    /// <summary>Raised after the column set (or any column's hidden state) changes, so a columns picker
    /// bound to this table can refresh its checkbox list.</summary>
    public event EventHandler? ColumnsChanged;

    public SheetTable()
    {
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnBubbleKeyDown, RoutingStrategies.Bubble);
        AddHandler(PointerPressedEvent, OnTunnelPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnCellRightClick, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnTunnelPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnTunnelPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnTunnelPointerWheel, RoutingStrategies.Tunnel);
    }

    // A focused ComboBox eats the mouse wheel to cycle its SelectedItem, so scrolling the table while a
    // combo cell is active would silently change that cell's value. Intercept the wheel in tunnel (the
    // table sees it before the combo): when it targets a closed combo, scroll the body ourselves and
    // swallow the event so the combo never spins. Open dropdowns keep their own wheel scrolling.
    private void OnTunnelPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_bodyScroll is null || e.Source is not Visual source)
            return;

        var combo = source as ComboBox ?? source.FindAncestorOfType<ComboBox>();
        if (combo is null || combo.IsDropDownOpen)
            return;

        // Mirror the ListBox's normal wheel step (vertical; Shift = horizontal) onto the body scroller.
        const double step = 50;
        var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        var offset = _bodyScroll.Offset;
        _bodyScroll.Offset = (e.KeyModifiers & KeyModifiers.Shift) != 0
            ? offset.WithX(offset.X - delta * step)
            : offset.WithY(offset.Y - delta * step);
        e.Handled = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Inputs that shape the columns ⇒ rebuild; the items source/selection are pushed to the body.
        if (change.Property == DaysProperty || change.Property == BlocksProperty ||
            change.Property == LocalizationProperty || change.Property == ToggleBlockCommandProperty ||
            change.Property == BandsProperty || change.Property == RentalChipsProperty ||
            change.Property == ToggleRentalChipCommandProperty ||
            change.Property == DiscountsProperty || change.Property == RaisedFeeEnabledProperty ||
            change.Property == ShowTeamProperty || change.Property == ShowScoreProperty)
        {
            Rebuild();
        }
        else if (change.Property == ItemsSourceProperty && _body is not null)
        {
            HookItemsSource();
            ApplySortedView();
        }
        else if (change.Property == SelectedItemProperty && _body is not null)
        {
            _body.SelectedItem = SelectedItem;
        }
        else if (change.Property == StatusInfoProperty)
        {
            UpdateStatusBar();
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        // The banded header is built in code (its column set is dynamic); host it in a template slot.
        if (Localization is not null && e.NameScope.Find<Decorator>("PART_HeaderSlot") is { } slot)
        {
            _header = new SheetHeaderPanel(Localization);
            slot.Child = _header;
        }

        _headerScroll = e.NameScope.Find<ScrollViewer>("PART_HeaderScroll");

        // Global search box: typing re-filters the body live; the ✕ clears it and is shown only while
        // there is text. Escape (handled here so it works while the box has focus) clears + returns focus
        // to the table body.
        _search = e.NameScope.Find<TextBox>("PART_Search");
        _searchClear = e.NameScope.Find<Button>("PART_SearchClear");
        if (_search is not null)
        {
            _search.Text = _globalSearch;
            _search.GetObservable(TextBox.TextProperty).Subscribe(new SearchTextSync(this));
            _search.KeyDown += OnSearchKeyDown;
        }
        if (_searchClear is not null)
            _searchClear.Click += (_, _) =>
            {
                if (_search is not null)
                {
                    _search.Text = string.Empty;
                    _search.Focus();
                }
            };

        // Status bar parts. The per-column sums live in their own slot (a grid mirroring the leaf
        // columns), hosted in a scroller slaved to the body's horizontal offset like the header.
        _statusScroll = e.NameScope.Find<ScrollViewer>("PART_StatusScroll");
        _statusBar = e.NameScope.Find<Border>("PART_StatusBar");
        _statusSumsRow = e.NameScope.Find<Border>("PART_StatusSumsRow");
        _statusCounts = e.NameScope.Find<TextBlock>("PART_StatusCounts");
        _statusInfo = e.NameScope.Find<TextBlock>("PART_StatusInfo");
        if (e.NameScope.Find<Decorator>("PART_StatusSlot") is { } statusSlot)
        {
            _statusPanel = new SheetStatusPanel();
            statusSlot.Child = _statusPanel;
        }

        _body = e.NameScope.Find<ListBox>("PART_Body");
        if (_body is not null)
        {
            HookItemsSource();
            _body.ItemsSource = ItemsSource;
            _body.SelectedItem = SelectedItem;
            _body.SelectionChanged += (_, _) => SelectedItem = _body.SelectedItem;
            // A row container realized/recycled during scroll may carry a stale range highlight; refresh
            // its cells against the current selection rectangle as soon as it's prepared.
            _body.ContainerPrepared += (_, args) => UpdateContainerRange(args.Container);
            // The body's own ScrollViewer is created with its template; grab it once realized and
            // slave the header's horizontal offset to it so the two scroll left/right together.
            _body.TemplateApplied += (_, args) =>
            {
                _bodyScroll = args.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
                if (_bodyScroll is not null)
                    _bodyScroll.GetObservable(ScrollViewer.OffsetProperty).Subscribe(new OffsetSync(this));
            };
        }

        Rebuild();
    }

    // ── Column rebuild ────────────────────────────────────────────────────────────────────────────
    /// <summary>Forces a column rebuild — call after a collapse/expand toggle (RosterColumnsChanged).</summary>
    public void Rebuild()
    {
        if (Localization is null)
            return;

        // Seed order/hidden/widths from the saved per-competition view the first time we build for this
        // (table + competition). Done before building bands so ApplyBandOrder/hidden re-apply pick it up.
        LoadLayoutIfNeeded();

        _cellFactory = new RosterCellFactory(Localization, RequestDelete, RentalChips);

        // Caller-supplied bands (flat day-mode table) take precedence over roster auto-building.
        if (Bands is not null)
        {
            _bands = Bands;
        }
        else
        {
            if (Days is null || Blocks is null)
                return;
            var builder = new RosterColumnBuilder(Localization);
            _bands = builder.Build(Days, AsList(Blocks), Discounts ?? [], RaisedFeeEnabled, ShowTeam, ShowScore, _bands);
        }

        // Apply any user reorder (drag), keyed by a stable band signature so it survives rebuilds.
        _bands = ApplyBandOrder(_bands);

        // Re-apply the persisted hidden-key set: every rebuild creates fresh SheetColumn instances
        // (language change, collapse toggle, day-set change), so the hidden flag is restored by key.
        // Refresh each active filter's header from the matching fresh column too, so a chip's label
        // re-localizes on a language change (the filter itself survives by key).
        foreach (var band in _bands)
            foreach (var col in band.Columns)
            {
                col.IsHidden = _hiddenKeys.Contains(col.Key);
                // Restore a saved width by key — the first build has no previous bands for CarryWidths
                // to copy from, so saved widths would otherwise be lost until the next rebuild.
                if (_savedWidths is not null && _savedWidths.TryGetValue(col.Key, out var w))
                    col.Width = w;
                if (_filters.TryGetValue(col.Key, out var filter))
                    filter.Header = string.IsNullOrEmpty(col.PickerLabel) ? col.Header : col.PickerLabel;
            }

        // What the header and rows render: the bands with hidden leaves dropped.
        _visibleBands = ComputeVisibleBands(_bands);

        if (_header is not null)
        {
            _header.ToggleBlock = ToggleBlockCommand;
            _header.SortBy = (column, additive) => ApplySort(column, additive);
            _header.MoveBand = MoveBand;
            _header.HideColumn = HideColumn;
            _header.BulkEditColumn = column => BulkEditColumnRequested?.Invoke(this, column);
            _header.CanBulkEdit = column => CanBulkEditColumn?.Invoke(column) == true;
            _header.ColumnResized = PersistLayout;
            _header.FilterColumn = ShowColumnFilter;
            _header.RemoveFilter = column => ClearColumnFilter(column.Key);
            _header.HasFilter = column => _filters.ContainsKey(column.Key);
            _header.SortColumn = _sortColumn;
            _header.SortDescending = _sortDescending;
            _header.SortLevels = _sortLevels;
            _header.Rebuild(_visibleBands);
        }

        // The status bar's per-column sums grid mirrors the same visible leaf columns as the header.
        _statusPanel?.Rebuild(_visibleBands);

        // (Re)stamp rows so their cell hosts pick up the current column set. The row grid's structure
        // depends only on the current _bands (identical for every row), so containers are safe to
        // recycle as the body scrolls — cell content is binding-driven and re-points to the new row's
        // DataContext on reuse. A new FuncDataTemplate instance on each Rebuild() forces ListBox to
        // discard the old containers, so collapse/expand/language/day-set changes still regenerate the
        // per-row grids cleanly. Recycling is the main win against scroll allocation / GC churn at 600 rows.
        if (_body is not null)
            _body.ItemTemplate = new FuncDataTemplate<object>((_, _) => BuildRow(), supportsRecycling: true);

        ApplySortedView();
        ColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Column visibility ─────────────────────────────────────────────────────────────────────────
    // Keys of the columns the user has hidden. Persisted at the table level (not on a column instance)
    // because every Rebuild() makes fresh SheetColumn instances — the set is re-applied by key there.
    private readonly HashSet<string> _hiddenKeys = new();

    // ── Column filters ────────────────────────────────────────────────────────────────────────────
    // Active filters keyed by SheetColumn.Key (parallel to _hiddenKeys, and for the same reason: fresh
    // column instances on every Rebuild are matched back by key). Filtering is display-only — it shapes
    // the body's item list in ApplySortedView and never touches the bound source collection.
    private readonly Dictionary<string, SheetFilter> _filters = new();

    // Filter keys in the order they were first added, so Shift+F3 can clear the most recent one when no
    // filtered column is in focus. Re-applying an existing column's filter keeps its original position.
    private readonly List<string> _filterOrder = new();

    // ── Global search ─────────────────────────────────────────────────────────────────────────────
    // A free-text "search every column" term, additive to the per-column filters: a row is shown only
    // when each whitespace-separated token appears (case-insensitively) in at least one of its visible
    // columns' displayed text. Display-only, like the column filters — it never touches the source
    // collection. Edited via the toolbar search box and refreshed through ApplySortedView.
    private string _globalSearch = string.Empty;
    private string _searchTerm = string.Empty;
    // Layout-tolerant variants of _searchTerm (wrong-keyboard-layout + s/i→ы), precomputed when the
    // term changes so PassesGlobalSearch only does substring checks per row. Empty when no search.
    private IReadOnlyList<string> _searchVariants = Array.Empty<string>();

    /// <summary>Raised after the active-filter set changes, so a filter-chips bar can refresh.</summary>
    public event EventHandler? FiltersChanged;

    /// <summary>The filters currently applied, in no particular order (for the chips bar).</summary>
    public IReadOnlyCollection<SheetFilter> ActiveFilters => _filters.Values;

    /// <summary>The filter on the given column key, or null if none.</summary>
    public SheetFilter? GetColumnFilter(string key)
        => _filters.TryGetValue(key, out var f) ? f : null;

    /// <summary>Applies (or replaces) a column's filter; an inactive filter clears it instead.</summary>
    public void SetColumnFilter(string key, SheetFilter filter)
    {
        if (!filter.IsActive)
        {
            ClearColumnFilter(key);
            return;
        }
        if (!_filters.ContainsKey(key))
            _filterOrder.Add(key);
        _filters[key] = filter;
        ApplySortedView();
        FiltersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a column's filter (no-op if none). Rebuilds the body view.</summary>
    public void ClearColumnFilter(string key)
    {
        if (_filters.Remove(key))
        {
            _filterOrder.Remove(key);
            ApplySortedView();
            FiltersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Removes the most recently added filter (no-op if none). Used by Shift+F3 when the focused
    /// column has no filter to clear.</summary>
    public void ClearLastFilter()
    {
        if (_filterOrder.Count == 0)
            return;
        ClearColumnFilter(_filterOrder[^1]);
    }

    /// <summary>Removes every active filter.</summary>
    public void ClearAllFilters()
    {
        if (_filters.Count == 0)
            return;
        _filters.Clear();
        _filterOrder.Clear();
        ApplySortedView();
        FiltersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The global "search all columns" term. Setting it re-filters the displayed rows so the whole
    /// (whitespace-trimmed) phrase appears as a substring of some visible column's text — spaces inside
    /// the term are matched literally, not split into independent tokens. Empty ⇒ no search.
    /// </summary>
    public string GlobalSearch
    {
        get => _globalSearch;
        set
        {
            var text = value ?? string.Empty;
            if (text == _globalSearch)
                return;
            _globalSearch = text;
            _searchTerm = text.Trim();
            _searchVariants = TextSearch.Variants(_searchTerm);
            // Debounce: typing fires this on every keystroke, and re-filtering scans every row's every
            // column (reflection-heavy). Coalesce a burst of keystrokes into one filter pass that runs
            // after a short pause, off the UI thread, so typing stays responsive at hundreds of rows.
            ScheduleSearchFilter();
        }
    }

    // ── Debounced search filtering ──────────────────────────────────────────────────────────────────
    // A timer that fires the actual re-filter a short moment after the last keystroke. Each keystroke
    // restarts it, so we filter once when typing pauses rather than on every character.
    private DispatcherTimer? _searchDebounce;
    // A monotonically increasing token: the background filter pass captures the current value and, when it
    // finishes, only applies its result if the token is still current (no newer keystroke has landed).
    private int _searchGeneration;
    private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(180);

    private void ScheduleSearchFilter()
    {
        _searchDebounce ??= new DispatcherTimer { Interval = SearchDebounceDelay };
        _searchDebounce.Stop();
        // Re-point the tick handler each schedule so it always runs the latest closure state; simpler than
        // sharing one handler and reading fields, and the timer only ever has this one subscriber.
        _searchDebounce.Tick -= OnSearchDebounceTick;
        _searchDebounce.Tick += OnSearchDebounceTick;
        _searchDebounce.Start();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce?.Stop();
        RunSearchFilterAsync();
    }

    // Computes the displayed list for the current search term OFF the UI thread (the reflection-heavy
    // filter/sort scan), then marshals only the body-assignment back to the UI thread. Guarded by a
    // generation token so a slow pass whose term is already stale is discarded instead of flickering the
    // old result back in.
    private async void RunSearchFilterAsync()
    {
        if (_body is null)
            return;

        var generation = ++_searchGeneration;

        // Snapshot the source on the UI thread (enumerating a live ObservableCollection off-thread is
        // unsafe); the per-row property reads inside BuildDisplayedItems are plain getters and safe to run
        // on the pool thread.
        var snapshot = new List<object?>();
        foreach (var item in ItemsSource ?? Array.Empty<object?>())
            snapshot.Add(item);

        List<object?>? displayed;
        try
        {
            displayed = await System.Threading.Tasks.Task.Run(() => BuildDisplayedItems(snapshot)).ConfigureAwait(true);
        }
        catch
        {
            // A row property throwing mid-scan (rare) shouldn't wedge search — fall back to a synchronous
            // pass on the UI thread, which matches the old behaviour.
            if (generation == _searchGeneration)
                ApplySortedView();
            return;
        }

        // A newer keystroke already scheduled another pass; drop this now-stale result.
        if (generation != _searchGeneration || _body is null)
            return;

        ClearRange();
        if (displayed is null)
        {
            // Search cleared and nothing else filters/sorts: bind the live source directly again.
            var unsorted = new List<object?>();
            foreach (var item in ItemsSource ?? Array.Empty<object?>())
                unsorted.Add(item);
            _sortedItems = unsorted;
            _body.ItemsSource = ItemsSource;
        }
        else
        {
            _sortedItems = displayed;
            _body.ItemsSource = displayed;
        }
        UpdateStatusBar();
    }

    /// <summary>Moves keyboard focus to the toolbar's global-search box (Ctrl+F). No-op until templated.</summary>
    public void FocusSearch()
    {
        if (_search is null)
            return;
        _search.Focus();
        _search.SelectAll();
    }

    // The search box's text changed: push it to the search term (re-filters) and toggle the ✕ button.
    private void OnSearchTextChanged(string? text)
    {
        GlobalSearch = text ?? string.Empty;
        if (_searchClear is not null)
            _searchClear.IsVisible = !string.IsNullOrEmpty(text);
    }

    // Escape in the search box clears it and hands focus back to the table body, so the user can resume
    // keyboard cell navigation; an empty Escape just moves focus out without changing anything.
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        if (_search is { } box && !string.IsNullOrEmpty(box.Text))
            box.Text = string.Empty;
        _body?.Focus();
    }

    /// <summary>Forwards the search box's Text changes to <see cref="OnSearchTextChanged"/>.</summary>
    private sealed class SearchTextSync(SheetTable owner) : IObserver<string?>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(string? value) => owner.OnSearchTextChanged(value);
    }

    /// <summary>
    /// The rows currently displayed, in on-screen order — the active column sort and filters applied.
    /// Equals the bound source order when nothing is sorted or filtered. Kept in sync on every
    /// filter/sort/source/rebuild by <see cref="ApplySortedView"/>.
    /// </summary>
    public IReadOnlyList<object?> VisibleItems => _sortedItems;

    /// <summary>The distinct displayed values currently in a column, naturally sorted, for the values picker.</summary>
    public IReadOnlyList<string> DistinctValues(SheetColumn column)
    {
        var set = new HashSet<string>();
        foreach (var item in ItemsSource ?? Array.Empty<object?>())
            if (item is not null)
                set.Add(CellText(column, item));
        var list = new List<string>(set);
        list.Sort(NaturalCompare);
        return list;
    }

    /// <summary>The leaf column with the given key in the current band set, or null if absent.</summary>
    public SheetColumn? FindColumnByKey(string key)
    {
        foreach (var band in _bands)
            foreach (var col in band.Columns)
                if (col.Key == key)
                    return col;
        return null;
    }

    // The text a column renders for a row — the single source of truth shared by filtering, the values
    // picker and the cell "filter by this value" menu, so all three agree on "the value of this cell".
    // Combo/option columns read their label path; dates format to match the dd.MM.yyyy display.
    private static string CellText(SheetColumn column, object row)
        => FormatValue(ReadPath(row, column.FilterPath));

    // The text COPY reads for a cell of an off-screen (unrealized) row — uses CopyPath, which defaults
    // to FilterPath but is overridden where the filter value differs from the display (payment amount
    // vs. its status token, collapsed merged values). A realized cell is read directly instead (its
    // rendered text wins); this is the fallback only.
    private static string CopyText(SheetColumn column, object row)
        => FormatValue(ReadPath(row, column.CopyPath));

    // Shared value→display formatter for the readers above.
    private static string FormatValue(object? value)
        => value switch
        {
            null => string.Empty,
            DateTimeOffset dto => dto.ToString("dd.MM.yyyy"),
            DateTime dt => dt.ToString("dd.MM.yyyy"),
            // Fees/sums are whole numbers — format like the fee cells (FormatSum: trim trailing zeros,
            // invariant) so copy/filter agree with what's on screen, no stray ",0".
            decimal dec => FormatSum(dec),
            double dbl => FormatSum((decimal)dbl),
            // A bool flag copies as the same compact mark a realized checkbox does.
            bool b => b ? CheckedMark : string.Empty,
            _ => value.ToString() ?? string.Empty
        };

    // True when a row passes every active filter (an absent/now-hidden column's filter is ignored) AND
    // the global search term. The two are independent gates; both must pass.
    private bool PassesFilters(object? row)
    {
        if (row is null)
            return true;
        foreach (var filter in _filters.Values)
        {
            var col = FindColumnByKey(filter.ColumnKey);
            if (col is null)
                continue;
            if (!filter.Matches(CellText(col, row)))
                return false;
        }
        return PassesGlobalSearch(row);
    }

    // True when the whole search phrase appears (case-insensitively) as a substring of at least one
    // visible column's text. The phrase is matched literally — spaces inside it are part of the term, so
    // "4 в 1" only matches a cell that actually contains "4 в 1", not any cell that happens to contain a
    // "4", a "в" and a "1" separately. An empty term passes everything. Hidden columns are excluded so a
    // search matches what the user can actually see, mirroring the column filters. The term is expanded
    // via TextSearch into wrong-keyboard-layout and s/i→ы variants — a cell matches any of them.
    private bool PassesGlobalSearch(object row)
    {
        if (_searchTerm.Length == 0)
            return true;

        foreach (var band in _visibleBands)
            foreach (var col in band.Columns)
            {
                if (!col.Filterable)
                    continue;
                var cell = CellText(col, row);
                foreach (var variant in _searchVariants)
                    if (cell.IndexOf(variant, StringComparison.CurrentCultureIgnoreCase) >= 0)
                        return true;
            }
        return false;
    }

    // Opens the per-column filter editor anchored at the given control (header cell or cell).
    private void ShowColumnFilter(SheetColumn column, Control anchor)
    {
        if (Localization is null || !column.Filterable)
            return;
        var popup = new ColumnFilterPopup(this, column, Localization);
        popup.Show(anchor);
    }

    // Header-menu entry point: anchor the popup at the header cell that was right-clicked.
    private void ShowColumnFilter(SheetColumn column)
    {
        var anchor = _header is not null && _header.HeaderCellFor(column) is { } cell ? cell : (Control)this;
        ShowColumnFilter(column, anchor);
    }

    /// <summary>
    /// The leaf columns the user can show/hide, in display order, with a readable label each. The
    /// trailing action column and any column without a picker label are excluded — only real data
    /// columns are listable. Used by the columns picker to render its checkbox list.
    /// </summary>
    public IReadOnlyList<SheetColumn> ToggleableColumns()
    {
        var list = new List<SheetColumn>();
        // Any column carrying a PickerLabel is toggleable — including the participants tables' Actions
        // (delete) column, which now sets one. Tables whose delete column has no label (the generic
        // SheetColumnBuilder.DeleteAction) stay excluded.
        foreach (var band in _bands)
            foreach (var col in band.Columns)
                if (!string.IsNullOrEmpty(col.PickerLabel))
                    list.Add(col);
        return list;
    }

    /// <summary>Shows or hides the column with the given key, then rebuilds. No-op if already in that state.</summary>
    public void SetColumnHidden(string key, bool hidden)
    {
        var changed = hidden ? _hiddenKeys.Add(key) : _hiddenKeys.Remove(key);
        if (changed)
        {
            Rebuild();
            PersistLayout();
            ColumnVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>True if the column with this key is currently shown (not in the hidden set).</summary>
    public bool IsColumnVisible(string key) => !_hiddenKeys.Contains(key);

    /// <summary>Raised after a column is hidden or shown, so a page can lazily (re)compute the data
    /// behind a column only while it's visible.</summary>
    public event EventHandler? ColumnVisibilityChanged;

    // Hide a column from the header context menu.
    private void HideColumn(SheetColumn column) => SetColumnHidden(column.Key, true);

    // Drops hidden leaves from each band; a band with no visible leaf is removed entirely. Returns the
    // input list unchanged (same references) when nothing is hidden, so the common case allocates nothing.
    private static IReadOnlyList<SheetBand> ComputeVisibleBands(IReadOnlyList<SheetBand> bands)
    {
        var anyHidden = false;
        foreach (var b in bands)
            foreach (var c in b.Columns)
                if (c.IsHidden) { anyHidden = true; break; }

        if (!anyHidden)
            return bands;

        var result = new List<SheetBand>(bands.Count);
        foreach (var band in bands)
        {
            var visible = new List<SheetColumn>(band.Columns.Count);
            foreach (var c in band.Columns)
                if (!c.IsHidden)
                    visible.Add(c);
            if (visible.Count == 0)
                continue;
            result.Add(visible.Count == band.Columns.Count
                ? band
                : new SheetBand(band.Kind, visible) { Header = band.Header, Block = band.Block });
        }
        return result;
    }

    // ── Band reorder (drag) ─────────────────────────────────────────────────────────────────────
    // The desired top-level band order, as stable signatures. Null until the user reorders.
    private List<string>? _bandOrder;

    // The active sort, as an ordered list of levels: sort first by [0], ties broken by [1], and so on.
    // Empty ⇒ unsorted (source order). A plain header click replaces the whole list with one level;
    // Shift+click appends/toggles an additional level; the custom-sort dialog sets the list wholesale.
    private readonly List<SortLevel> _sortLevels = new();

    // The primary (first) sort level's column/direction, kept in sync so the header arrow indicator and
    // the existing single-level fast path keep working unchanged.
    private SheetColumn? _sortColumn => _sortLevels.Count > 0 ? _sortLevels[0].Column : null;
    private bool _sortDescending => _sortLevels.Count > 0 && _sortLevels[0].Descending;

    // ── Persisted view (per-competition views.json) ─────────────────────────────────────────────
    // Saved widths by column key, applied onto freshly built columns (the first build has no previous
    // bands for CarryWidths to copy from). Loaded once per (LayoutKey + competition).
    private Dictionary<string, double>? _savedWidths;
    // "<LayoutKey>|<scopeId>" the cached layout was loaded for; cleared on a competition/key change so
    // Rebuild reloads (and never carries one competition's view into another).
    private string? _loadedFor;

    // Loads the saved view for (LayoutKey + current competition) the first time it is needed, seeding
    // _bandOrder / _hiddenKeys / _savedWidths. Reloads when the competition (or key) changes, so one
    // competition's view never carries into another. No-op when persistence isn't configured.
    private void LoadLayoutIfNeeded()
    {
        if (LayoutStore is not { } store || LayoutKey is not { } key)
            return;

        var scope = $"{key}|{store.CurrentScopeId ?? string.Empty}";
        if (_loadedFor == scope)
            return;
        _loadedFor = scope;

        // Reset any in-memory view from the previous competition before applying the new one.
        _bandOrder = null;
        _hiddenKeys.Clear();
        _savedWidths = null;

        var layout = store.Load(key);
        if (layout is null)
            return;

        if (layout.Order.Count > 0)
            _bandOrder = new List<string>(layout.Order);
        foreach (var hidden in layout.Hidden)
            _hiddenKeys.Add(hidden);
        _savedWidths = new Dictionary<string, double>();
        foreach (var (colKey, col) in layout.Columns)
            if (col.Width is { } w)
                _savedWidths[colKey] = w;
    }

    // Serializes the current order / hidden set / widths and saves them for this table. Called after a
    // hide/show, a band reorder, or a finished column resize. Loading seeds the cache fields directly
    // (not through these mutation paths), so there's no save-back loop to guard against. No-op when
    // persistence isn't configured.
    private void PersistLayout()
    {
        if (LayoutStore is not { } store || LayoutKey is not { } key)
            return;

        var layout = new TableLayout
        {
            Order = new List<string>(_bandOrder ?? CurrentBandSignatures()),
            Hidden = new List<string>(_hiddenKeys),
        };
        // Refresh the in-memory width cache as we serialize, so the next Rebuild re-applies the CURRENT
        // widths by key (line ~335) rather than the stale values loaded once at session start. Without
        // this, resizing a column then hiding/reordering any column reverts the resize on rebuild.
        _savedWidths = new Dictionary<string, double>();
        foreach (var band in _bands)
            foreach (var col in band.Columns)
                if (!string.IsNullOrEmpty(col.Key))
                {
                    layout.Columns[col.Key] = new ColumnLayout { Width = col.Width };
                    if (col.Width is { } w)
                        _savedWidths[col.Key] = w;
                }

        store.Save(key, layout);
    }

    // The signatures of the current bands in their built order (used when the user hasn't reordered but
    // we still want to persist a stable order alongside widths/hidden).
    private List<string> CurrentBandSignatures()
    {
        var sigs = new List<string>(_bands.Count);
        foreach (var band in _bands)
            sigs.Add(Signature(band));
        return sigs;
    }

    private static string Signature(SheetBand band)
    {
        // Field blocks are identified by their block reference; identity/action bands by first kind+header.
        if (band.Block is not null)
            return "block:" + band.Block.Field;
        return "id:" + band.Columns[0].Kind + ":" + band.Header;
    }

    private IReadOnlyList<SheetBand> ApplyBandOrder(IReadOnlyList<SheetBand> bands)
    {
        if (_bandOrder is null)
            return bands;

        var bySig = new Dictionary<string, SheetBand>();
        foreach (var b in bands)
            bySig[Signature(b)] = b;

        var ordered = new List<SheetBand>(bands.Count);
        foreach (var sig in _bandOrder)
            if (bySig.Remove(sig, out var b))
                ordered.Add(b);
        // Any new bands not in the saved order (e.g. a day was added) go to the end in build order.
        foreach (var b in bands)
            if (bySig.ContainsValue(b))
                ordered.Add(b);
        return ordered.Count == bands.Count ? ordered : bands;
    }

    // The header reorders over the VISIBLE bands, so its from/to are indices into _visibleBands. Map
    // them onto the full _bands (which may contain hidden bands between them) by signature, then move.
    private void MoveBand(int from, int to)
    {
        if (from < 0 || from >= _visibleBands.Count || to < 0 || to >= _visibleBands.Count || from == to)
            return;

        var fromSig = Signature(_visibleBands[from]);
        var toSig = Signature(_visibleBands[to]);

        var order = new List<string>(_bands.Count);
        foreach (var b in _bands)
            order.Add(Signature(b));

        var fromIndex = order.IndexOf(fromSig);
        var toIndex = order.IndexOf(toSig);
        if (fromIndex < 0 || toIndex < 0)
            return;

        order.RemoveAt(fromIndex);
        order.Insert(toIndex, fromSig);
        _bandOrder = order;
        Rebuild();
        PersistLayout();
    }

    // Keep the sorted view fresh when rows are added/removed in the source collection.
    private INotifyCollectionChanged? _hookedSource;

    private void HookItemsSource()
    {
        if (_hookedSource is not null)
            _hookedSource.CollectionChanged -= OnSourceCollectionChanged;
        _hookedSource = ItemsSource as INotifyCollectionChanged;
        if (_hookedSource is not null)
            _hookedSource.CollectionChanged += OnSourceCollectionChanged;
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => ApplySortedView();

    // ── Sorting ─────────────────────────────────────────────────────────────────────────────────
    // One level of a multi-column sort: the column and its direction. The sort is an ordered list of
    // these — rows are ordered by the first, ties broken by the second, and so on.
    public sealed class SortLevel(SheetColumn column, bool descending)
    {
        public SheetColumn Column { get; } = column;
        public bool Descending { get; set; } = descending;
    }

    // A plain header click: cycles unsorted → ascending → descending on that column, REPLACING any
    // existing multi-level sort with a single level. Shift+click instead adds/toggles this column as an
    // additional (secondary, tertiary, …) sort level, preserving the earlier levels. Sorting reorders a
    // display copy of the items; the source collection is left untouched so the VM's selection/delete
    // keep working by reference.
    private void ApplySort(SheetColumn column) => ApplySort(column, additive: false);

    private void ApplySort(SheetColumn column, bool additive)
    {
        if (string.IsNullOrEmpty(column.SortPath))
            return;

        var existing = _sortLevels.Find(l => l.Column.Key == column.Key);
        if (additive)
        {
            // Shift+click: toggle this column's direction if it's already a level, else append it.
            if (existing is not null)
                existing.Descending = !existing.Descending;
            else
                _sortLevels.Add(new SortLevel(column, descending: false));
        }
        else
        {
            // Plain click: if this column is ALREADY the sole primary sort, flip its direction; otherwise
            // start a fresh single-level ascending sort on it (discarding any multi-level sort).
            var flipToDescending = _sortLevels.Count == 1 && _sortLevels[0].Column.Key == column.Key && !_sortLevels[0].Descending;
            _sortLevels.Clear();
            _sortLevels.Add(new SortLevel(column, descending: flipToDescending));
        }

        RefreshSortHeader();
        ApplySortedView();
        SortChanged?.Invoke(this, EventArgs.Empty);
    }

    // Pushes the current primary level to the header (arrow indicator) and the full level list (so it can
    // paint the "1/2/3" rank badges on secondary columns), then rebuilds the header visuals.
    private void RefreshSortHeader()
    {
        if (_header is null)
            return;
        _header.SortColumn = _sortColumn;
        _header.SortDescending = _sortDescending;
        _header.SortLevels = _sortLevels;
        _header.Rebuild(_visibleBands);
    }

    /// <summary>Raised after the active sort (columns or directions) changes, so a sort dialog can refresh.</summary>
    public event EventHandler? SortChanged;

    /// <summary>The active sort levels, in priority order (first = primary). Empty ⇒ unsorted.</summary>
    public IReadOnlyList<SortLevel> SortLevels => _sortLevels;

    /// <summary>The leaf columns that can take part in a sort (have a sort path, are toggleable), in
    /// display order — the choices offered by the custom-sort dialog.</summary>
    public IReadOnlyList<SheetColumn> SortableColumns()
    {
        var list = new List<SheetColumn>();
        foreach (var band in _bands)
            foreach (var col in band.Columns)
                if (!string.IsNullOrEmpty(col.SortPath) && !string.IsNullOrEmpty(col.PickerLabel))
                    list.Add(col);
        return list;
    }

    /// <summary>Replaces the whole sort with the given ordered levels (as (column key, descending) pairs).
    /// Unknown keys are ignored. Used by the custom-sort dialog's Apply.</summary>
    public void SetSortLevels(IReadOnlyList<(string Key, bool Descending)> levels)
    {
        _sortLevels.Clear();
        foreach (var (key, desc) in levels)
        {
            var col = FindColumnByKey(key);
            if (col is not null && !string.IsNullOrEmpty(col.SortPath))
                _sortLevels.Add(new SortLevel(col, desc));
        }
        RefreshSortHeader();
        ApplySortedView();
        SortChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clears the sort entirely (back to source order).</summary>
    public void ClearSort()
    {
        if (_sortLevels.Count == 0)
            return;
        _sortLevels.Clear();
        RefreshSortHeader();
        ApplySortedView();
        SortChanged?.Invoke(this, EventArgs.Empty);
    }

    // The body's current item order (sorted view, or the source order when unsorted). Cached so row
    // navigation (MoveRow) doesn't re-materialise and re-scan the whole collection on every keystroke.
    private IReadOnlyList<object?> _sortedItems = Array.Empty<object?>();

    private void ApplySortedView()
    {
        if (_body is null)
            return;

        // A pending debounced search pass would clobber this result a moment later; cancel it. Any
        // structural change routed through here (sort, filter, source change, rebuild) already reflects
        // the current search term via BuildDisplayedItems, so the queued pass is redundant.
        _searchDebounce?.Stop();
        _searchGeneration++;

        // The view is about to change order/membership, which invalidates the range's row indices.
        // Drop any multi-cell selection so it can't highlight (or copy) the wrong rows.
        ClearRange();

        // When nothing is sorted or filtered, bind the live source directly (cheapest path — keeps
        // optimistic row add/delete reflecting instantly). BuildDisplayedItems returns null to signal this.
        var displayed = BuildDisplayedItems(ItemsSource);
        if (displayed is null)
        {
            var unsorted = new List<object?>();
            foreach (var item in ItemsSource ?? Array.Empty<object?>())
                unsorted.Add(item);
            _sortedItems = unsorted;
            _body.ItemsSource = ItemsSource;
            UpdateStatusBar();
            return;
        }

        _sortedItems = displayed;
        _body.ItemsSource = displayed;
        UpdateStatusBar();
    }

    // Computes the filtered + sorted display list from a source, doing only reflective row reads (no UI
    // access) so it can run on a background thread for the debounced search path. Returns null when
    // nothing is sorted or filtered, meaning the caller should bind the live source directly. `source`
    // is any snapshot/collection of rows; callers on a pool thread must pass a snapshot, not a live one.
    private List<object?>? BuildDisplayedItems(IEnumerable? source)
    {
        var hasFilters = _filters.Count > 0 || _searchTerm.Length > 0;

        if (_sortColumn is null || string.IsNullOrEmpty(_sortColumn.SortPath) || source is null)
        {
            if (!hasFilters)
                return null;

            var filtered = new List<object?>();
            foreach (var item in source ?? Array.Empty<object?>())
                if (PassesFilters(item))
                    filtered.Add(item);
            return filtered;
        }

        // Multi-level Schwartzian transform: read every level's sort key for each item once (reflection is
        // the costly part), then compare the key arrays level by level. Avoids re-reading operands on every
        // comparison. Filtered rows are dropped before keying so we don't sort what we won't show.
        var levels = _sortLevels;
        var keyed = new List<(object?[] Keys, int Index, object Item)>();
        var index = 0;
        foreach (var item in source)
        {
            if (item is null || !PassesFilters(item))
                continue;
            var keys = new object?[levels.Count];
            for (var i = 0; i < levels.Count; i++)
                keys[i] = ReadPath(item, levels[i].Column.SortPath);
            keyed.Add((keys, index++, item));
        }

        keyed.Sort((a, b) =>
        {
            for (var i = 0; i < levels.Count; i++)
            {
                var cmp = CompareKeys(a.Keys[i], b.Keys[i]);
                if (cmp != 0)
                    return levels[i].Descending ? -cmp : cmp;
            }
            // All levels equal: keep the source order (stable sort) rather than an arbitrary shuffle.
            return a.Index.CompareTo(b.Index);
        });

        var items = new List<object?>(keyed.Count);
        foreach (var (_, _, item) in keyed)
            items.Add(item);
        return items;
    }

    // ── Status bar ────────────────────────────────────────────────────────────────────────────────
    // Recomputes the bar from the current displayed (filtered/sorted) view: total + shown row counts,
    // each summary column's sum, and the page-supplied system-info text. The whole bar collapses when
    // there is nothing to show (no summary columns, no info text). Called on every view change
    // (filter/sort/source/rebuild) and when StatusInfo changes.
    private void UpdateStatusBar()
    {
        if (_statusBar is null)
            return;

        var summaryColumns = SummaryColumns();
        var hasInfo = !string.IsNullOrWhiteSpace(StatusInfo);
        var hasCountColumn = HasCountColumn();

        // The bar is opt-in: it appears only for tables that declare a summary/count column (the
        // participants tables) or whose page supplies system-info text. Other tables (control points,
        // groups, …) get no status bar, keeping their appearance unchanged.
        if (summaryColumns.Count == 0 && !hasInfo && !hasCountColumn)
        {
            _statusBar.IsVisible = false;
            return;
        }
        _statusBar.IsVisible = true;

        // Total = the full bound source; shown = the displayed view (after filtering). _sortedItems is
        // the displayed set (it equals the source when nothing is filtered).
        var total = Count(ItemsSource);
        var shown = _sortedItems.Count;
        var (countText, countTip) = CountText(shown, total);

        // The count sits under the «Номер» column when a ShowCount column declared a cell; otherwise it
        // falls back to the fixed left area. Only one of the two is shown at a time.
        if (_statusPanel?.HasCountCell == true)
        {
            _statusPanel.SetCount(countText, countTip);
            if (_statusCounts is not null)
                _statusCounts.IsVisible = false;
        }
        else if (_statusCounts is not null)
        {
            _statusCounts.IsVisible = true;
            _statusCounts.Text = countTip;
        }

        if (_statusInfo is not null)
            _statusInfo.Text = StatusInfo ?? string.Empty;

        // Per-column sums over the displayed rows.
        // The sums row hosts both the count cell (under «Номер») and the per-column sums, so it shows
        // whenever either is present.
        if (_statusSumsRow is not null)
            _statusSumsRow.IsVisible = summaryColumns.Count > 0 || _statusPanel?.HasCountCell == true;
        _summaryColumns = summaryColumns;
        if (summaryColumns.Count > 0)
        {
            HookRowsForSums();
            WriteSums(summaryColumns);
        }
        else
        {
            UnhookRows();
        }
    }

    private List<SheetColumn> _summaryColumns = new();

    private void WriteSums(List<SheetColumn> summaryColumns)
    {
        if (_statusPanel is null)
            return;
        foreach (var col in summaryColumns)
        {
            var paid = SumColumn(col);
            _statusPanel.SetSum(col.Key, FormatSum(paid), col.HasSummaryOwed ? PaymentTooltip(col, paid) : null);
        }
    }

    // The payment column's hover tooltip: a "вже заплачено / ще мають заплатити" breakdown over the
    // displayed rows. Already-paid is the column sum (passed in); still-owed sums each row's shortfall
    // (computed total fee minus its payment, never below zero) so an overpaid row doesn't cancel an
    // unpaid one. Falls back to plain labels when no localization is set.
    private string PaymentTooltip(SheetColumn column, decimal paid)
    {
        decimal owed = 0m;
        foreach (var row in _sortedItems)
        {
            if (row is null)
                continue;
            TryReadDecimal(ReadPath(row, column.SummaryPath), out var rowPaid);
            if (TryReadDecimal(ReadPath(row, column.SummaryOwedPath), out var fee))
            {
                var shortfall = fee - rowPaid;
                if (shortfall > 0m)
                    owed += shortfall;
            }
        }

        var paidText = FormatSum(paid);
        var owedText = FormatSum(owed);
        if (Localization is not { } loc)
            return $"Вже заплачено: {paidText}\nЩе мають заплатити: {owedText}";
        return loc.Get("Sheet.Status.PaidLine").Replace("{0}", paidText)
            + "\n"
            + loc.Get("Sheet.Status.OwedLine").Replace("{0}", owedText);
    }

    // ── Live sums on inline edits ───────────────────────────────────────────────────────────────────
    // A summed value (the fee total, the typed payment) changes as the user edits; to keep the footer
    // sums live we subscribe to the displayed rows' PropertyChanged. A change to a property that feeds a
    // sum schedules one coalesced recompute (Background priority) so a burst of edits costs one pass.
    private readonly HashSet<INotifyPropertyChanged> _summedRows = new();
    private bool _sumRecomputeQueued;
    // The leading property-name segment of each summary path (e.g. "TotalEntryFee", "Payment"), so a
    // row change to an unrelated property doesn't trigger a recompute.
    private HashSet<string> _summedProps = new();

    private void HookRowsForSums()
    {
        // Rebuild the watched-property set from the current summary columns.
        _summedProps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var col in _summaryColumns)
        {
            var first = col.SummaryPath.Split('.', '[')[0];
            if (first.Length > 0)
                _summedProps.Add(first);
            // The payment column's tooltip also reacts to the owed (total-fee) value, so watch it too.
            if (col.HasSummaryOwed)
            {
                var owedFirst = col.SummaryOwedPath.Split('.', '[')[0];
                if (owedFirst.Length > 0)
                    _summedProps.Add(owedFirst);
            }
        }

        // Reconcile subscriptions to the current displayed rows: drop rows no longer shown, add new ones.
        var current = new HashSet<INotifyPropertyChanged>();
        foreach (var row in _sortedItems)
            if (row is INotifyPropertyChanged inpc)
                current.Add(inpc);

        foreach (var old in _summedRows)
            if (!current.Contains(old))
                old.PropertyChanged -= OnSummedRowChanged;
        foreach (var row in current)
            if (_summedRows.Add(row))
                row.PropertyChanged += OnSummedRowChanged;
        _summedRows.RemoveWhere(r => !current.Contains(r));
    }

    private void UnhookRows()
    {
        foreach (var row in _summedRows)
            row.PropertyChanged -= OnSummedRowChanged;
        _summedRows.Clear();
    }

    private void OnSummedRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null || !_summedProps.Contains(e.PropertyName) || _sumRecomputeQueued)
            return;
        _sumRecomputeQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _sumRecomputeQueued = false;
            if (_summaryColumns.Count > 0)
                WriteSums(_summaryColumns);
        }, DispatcherPriority.Background);
    }

    // True when a visible leaf column asks for the row count to be shown under it.
    private bool HasCountColumn()
    {
        foreach (var band in _visibleBands)
            foreach (var col in band.Columns)
                if (col.ShowCount)
                    return true;
        return false;
    }

    // The compact count text ("4 з 344", or just "344" when nothing is filtered) and the full-text hover
    // tooltip ("Показано 4 з 344"). Falls back to a plain "shown / total" when no localization is set.
    private (string Text, string Tooltip) CountText(int shown, int total)
    {
        var filtered = _filters.Count > 0 || _searchTerm.Length > 0;
        if (Localization is not { } loc)
            return filtered ? ($"{shown} / {total}", $"{shown} / {total}") : (total.ToString(), total.ToString());

        var text = filtered
            ? loc.Get("Sheet.Status.CountFilteredShort").Replace("{0}", shown.ToString()).Replace("{1}", total.ToString())
            : loc.Get("Sheet.Status.CountShort").Replace("{0}", total.ToString());
        var tip = filtered
            ? loc.Get("Sheet.Status.CountFiltered").Replace("{0}", shown.ToString()).Replace("{1}", total.ToString())
            : loc.Get("Sheet.Status.Count").Replace("{0}", total.ToString());
        return (text, tip);
    }

    // The visible leaf columns that contribute a status-bar sum, in display order.
    private List<SheetColumn> SummaryColumns()
    {
        var list = new List<SheetColumn>();
        foreach (var band in _visibleBands)
            foreach (var col in band.Columns)
                if (col.HasSummary)
                    list.Add(col);
        return list;
    }

    // Sums a column's numeric value over the currently displayed rows. The value may be a real number
    // or a numeric string (the free-text «Оплата» field); non-numeric / blank cells contribute 0.
    private decimal SumColumn(SheetColumn column)
    {
        decimal sum = 0m;
        foreach (var row in _sortedItems)
        {
            if (row is null)
                continue;
            if (TryReadDecimal(ReadPath(row, column.SummaryPath), out var value))
                sum += value;
        }
        return sum;
    }

    // Leniently coerces a cell value to a decimal: a real number is used directly; a string is parsed
    // (invariant first, then the current culture) after trimming spaces and common thousands gaps so a
    // typed "1 200,50" still counts. Anything unparseable ⇒ false (contributes nothing to the sum).
    private static bool TryReadDecimal(object? value, out decimal result)
    {
        switch (value)
        {
            case null:
                result = 0m;
                return false;
            case decimal d:
                result = d;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case double db:
                result = (decimal)db;
                return true;
            case float f:
                result = (decimal)f;
                return true;
        }

        var text = value.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            result = 0m;
            return false;
        }
        text = text.Replace(" ", string.Empty);
        const System.Globalization.NumberStyles styles = System.Globalization.NumberStyles.Number;
        if (decimal.TryParse(text, styles, System.Globalization.CultureInfo.InvariantCulture, out result))
            return true;
        // A comma decimal separator ("120,50") fails invariant parsing; retry against the UI culture.
        return decimal.TryParse(text, styles, System.Globalization.CultureInfo.CurrentCulture, out result);
    }

    // Formats a column sum like the fee cells: no currency symbol, trim trailing zeros.
    private static string FormatSum(decimal value)
        => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static int Count(IEnumerable? source)
    {
        if (source is null)
            return 0;
        if (source is System.Collections.ICollection collection)
            return collection.Count;
        var n = 0;
        foreach (var _ in source)
            n++;
        return n;
    }

    private static int CompareKeys(object? va, object? vb)
    {
        if (va is null && vb is null) return 0;
        if (va is null) return -1;
        if (vb is null) return 1;
        // Genuine numbers / dates / etc. keep their native ordering; text uses the natural comparison
        // so digit runs sort by value ("Група 2" before "Група 10"), not by character ("1" before "2").
        if (va is string || vb is string)
            return NaturalCompare(va.ToString(), vb.ToString());
        if (va is IComparable ca && va.GetType() == vb.GetType())
            return ca.CompareTo(vb);
        return NaturalCompare(va.ToString(), vb.ToString());
    }

    // Natural (human) string order: digit runs are compared by numeric value, the rest case-insensitively.
    // So "Група 2" < "Група 10" and "КП-9" < "КП-10", instead of the plain lexicographic "10" < "2".
    private static int NaturalCompare(string? sa, string? sb)
    {
        sa ??= string.Empty;
        sb ??= string.Empty;

        int ia = 0, ib = 0;
        while (ia < sa.Length && ib < sb.Length)
        {
            char ca = sa[ia], cb = sb[ib];
            bool da = char.IsDigit(ca), db = char.IsDigit(cb);

            if (da && db)
            {
                // Skip leading zeros so "007" and "7" compare equal in magnitude.
                int sa0 = ia, sb0 = ib;
                while (ia < sa.Length && sa[ia] == '0') ia++;
                while (ib < sb.Length && sb[ib] == '0') ib++;

                int na = ia; while (na < sa.Length && char.IsDigit(sa[na])) na++;
                int nb = ib; while (nb < sb.Length && char.IsDigit(sb[nb])) nb++;

                int lenA = na - ia, lenB = nb - ib;
                if (lenA != lenB)
                    return lenA < lenB ? -1 : 1; // more (non-zero) digits = larger number
                for (int k = 0; k < lenA; k++)
                {
                    char xa = sa[ia + k], xb = sb[ib + k];
                    if (xa != xb)
                        return xa < xb ? -1 : 1;
                }
                ia = na; ib = nb;
                // Equal magnitude: the one with fewer leading zeros sorts first for a stable order.
                if (ia - sa0 != ib - sb0)
                    return (ia - sa0) < (ib - sb0) ? -1 : 1;
            }
            else if (da != db)
            {
                // A digit run sorts before a non-digit at the same position ("1" before "a").
                return da ? -1 : 1;
            }
            else
            {
                char la = char.ToUpperInvariant(ca), lb = char.ToUpperInvariant(cb);
                if (la != lb)
                    return string.Compare(la.ToString(), lb.ToString(), StringComparison.CurrentCulture);
                ia++; ib++;
            }
        }

        return (sa.Length - ia).CompareTo(sb.Length - ib);
    }

    // Caches reflected PropertyInfo per (declaring type, property name) so a sort over 600 rows does not
    // re-resolve the same property hundreds of times. Misses (no such property) are cached as null.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), System.Reflection.PropertyInfo?> PropertyCache = new();

    // Reads a dotted property path (e.g. "SelectedGroup.Label" or "Days[0].Chip") off an object via
    // reflection, tolerating nulls and indexers along the way.
    private static object? ReadPath(object? root, string path)
    {
        var current = root;
        foreach (var rawSegment in path.Split('.'))
        {
            if (current is null)
                return null;
            var segment = rawSegment;
            int? index = null;
            var bracket = segment.IndexOf('[');
            if (bracket >= 0 && segment.EndsWith(']'))
            {
                var inner = segment.Substring(bracket + 1, segment.Length - bracket - 2);
                if (int.TryParse(inner, out var idx))
                    index = idx;
                segment = segment.Substring(0, bracket);
            }

            var type = current.GetType();
            var prop = PropertyCache.GetOrAdd((type, segment), static key => key.Item1.GetProperty(key.Item2));
            if (prop is null)
                return null;
            current = prop.GetValue(current);

            if (index is { } i && current is System.Collections.IList list)
                current = i >= 0 && i < list.Count ? list[i] : null;
        }
        return current;
    }

    private static List<RosterFieldBlockViewModel> AsList(IEnumerable<RosterFieldBlockViewModel> blocks)
    {
        var list = new List<RosterFieldBlockViewModel>();
        foreach (var b in blocks)
            list.Add(b);
        return list;
    }

    // ── Row assembly ──────────────────────────────────────────────────────────────────────────────
    // One body row: a horizontal Grid mirroring the header's leaf columns, each cell width bound to
    // the same SheetColumn.Width so header and rows stay aligned.
    private Control BuildRow()
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        var col = 0;
        foreach (var band in _visibleBands)
        {
            foreach (var column in band.Columns)
            {
                var def = new ColumnDefinition { MinWidth = column.MinWidth };
                def[!ColumnDefinition.WidthProperty] = new Avalonia.Data.Binding(nameof(SheetColumn.Width))
                {
                    Source = column,
                    Converter = PixelToGridLength.Instance
                };
                grid.ColumnDefinitions.Add(def);

                var cell = new SheetCell(column, col) { Content = _cellFactory!.Build(column) };
                if (column.Kind == SheetCellKind.PaymentText)
                {
                    // Tint the whole cell by the row's payment status (so empty cells colour too), not the
                    // inner label's text footprint. The cell inherits the row DataContext; the converter
                    // returns a transparent brush for the no-tint case so the cell stays hit-testable.
                    cell[!TemplatedControl.BackgroundProperty] =
                        new Avalonia.Data.Binding(nameof(ParticipantRosterRowViewModel.PaymentStatus))
                        {
                            Converter = Behaviors.PaymentHighlight.Instance
                        };
                }
                else if (column.Kind == SheetCellKind.BirthDate)
                {
                    // Red-tint the birth-date cell when the participant's birth year is outside their group's
                    // allowed age window (same red as an unrecognised finish-read chip). Both the day-grid and
                    // roster row VMs expose this bool under the same name, so one binding serves both tables.
                    cell[!TemplatedControl.BackgroundProperty] =
                        new Avalonia.Data.Binding(nameof(ParticipantRosterRowViewModel.BirthDateViolatesAge))
                        {
                            Converter = Behaviors.RowHighlight.Instance
                        };
                    // Explain the breach on hover. The bound text is empty when within the window, which
                    // suppresses the tooltip; both row VMs expose this property under the same name.
                    cell[!ToolTip.TipProperty] =
                        new Avalonia.Data.Binding(nameof(ParticipantRosterRowViewModel.AgeViolationTooltip));
                }
                else if (RowHighlightPath is { Length: > 0 } highlightPath)
                {
                    // Whole-row tint (e.g. an unrecognised chip on the finish-read log): every cell in the
                    // row binds the same row-level bool path, so the tint covers the row, not one column.
                    cell[!TemplatedControl.BackgroundProperty] = new Avalonia.Data.Binding(highlightPath)
                    {
                        Converter = Behaviors.RowHighlight.Instance
                    };
                }
                Grid.SetColumn(cell, col);
                grid.Children.Add(cell);
                col++;
            }
        }
        return grid;
    }

    // Keep the frozen header (and the status-bar sums row) lined up with the body's horizontal scroll.
    private void SyncHeaderOffset(Vector bodyOffset)
    {
        if (_headerScroll is not null)
            _headerScroll.Offset = new Vector(bodyOffset.X, 0);
        if (_statusScroll is not null)
            _statusScroll.Offset = new Vector(bodyOffset.X, 0);
    }

    /// <summary>Forwards the body scroller's offset to <see cref="SyncHeaderOffset"/>.</summary>
    private sealed class OffsetSync(SheetTable owner) : IObserver<Vector>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(Vector value) => owner.SyncHeaderOffset(value);
    }

    private void RequestDelete(object row)
    {
        if (DeleteCommand?.CanExecute(row) == true)
            DeleteCommand.Execute(row);
    }

    private void ToggleRental(string chip)
    {
        if (ToggleRentalChipCommand?.CanExecute(chip) == true)
            ToggleRentalChipCommand.Execute(chip);
    }

    // ── Excel-style edit + delete keyboard handling ───────────────────────────────────────────────
    private bool _ctrlDown;
    // The leaf-column index of the currently selected cell, carried across rows during up/down nav.
    // This is also the anchor column of any multi-cell selection (anchor = the first cell selected).
    private int _focusedColumn;

    // ── Multi-cell range selection (Excel-style) ──────────────────────────────────────────────────
    // The rectangle is the inclusive min/max of the anchor and active corners, in (_sortedItems row
    // index, leaf-column index) coordinates. The anchor is also the focus/edit cell and never moves
    // while extending; a plain (non-Shift) click/arrow collapses the range back to a single cell.
    private int _anchorRow = -1;
    private int _activeRow = -1;
    private int _activeCol = -1;
    // Pointer in a press-drag selection: the cell pressed, until the pointer enters a different cell.
    private bool _dragging;
    // True once a press-drag actually extended the selection past the anchor cell — used on release to
    // suppress the deferred "open the editor" (a drag-select must not drop into edit mode).
    private bool _dragExtended;
    // A resting cell's edit is deferred from press to release so a press-and-drag selects without editing.
    private LazyEditCell? _pendingEditCell;
    private Point _pendingEditPoint;

    // ── Edge autoscroll (Excel/Google-Sheets style) ──────────────────────────────────────────────────
    // While drag-selecting, holding the pointer near (or past) the body's viewport edge scrolls the body
    // toward that edge on a timer and keeps extending the selection to the cell now under the cursor, so
    // a range can grow beyond what's on screen without releasing the button.
    private DispatcherTimer? _edgeScrollTimer;
    private bool _edgeScrollActive;
    // The per-tick scroll delta (px) computed from how deep into the edge margin the pointer is; updated
    // on each move so scrolling accelerates as the cursor pushes further past the edge.
    private Vector _edgeScrollDelta;
    // The last drag-pointer position, in table coordinates — re-hit-tested on each timer tick.
    private Point _dragPoint;
    // How close to the viewport edge (px) the pointer must be to trigger autoscroll, and the speed cap.
    private const double EdgeScrollMargin = 28;
    private const double EdgeScrollMaxStep = 24;

    private bool HasRange => _anchorRow >= 0 && (_anchorRow != _activeRow || _focusedColumn != _activeCol);

    // The index of a row object in the current sorted/filtered view, or -1 if not present.
    private int RowIndexOf(object? row)
    {
        for (var i = 0; i < _sortedItems.Count; i++)
            if (Equals(_sortedItems[i], row))
                return i;
        return -1;
    }

    // The number of visible leaf columns (across all visible bands), matching BuildRow's layout.
    private int LeafColumnCount()
    {
        var count = 0;
        foreach (var band in _visibleBands)
            count += band.Columns.Count;
        return count;
    }

    // Collapse any active range to the single cell at (row, col): anchor = active = that cell, and
    // clear the highlight. Called on every plain selection so the range follows the focused cell.
    private void CollapseRange(int row, int col)
    {
        _anchorRow = _activeRow = row;
        _focusedColumn = _activeCol = col;
        RepaintRange();
    }

    // Drop the selection entirely (no anchor) and clear any highlight. Used when the view re-sorts or
    // re-filters and the cached row indices no longer mean anything.
    private void ClearRange()
    {
        _anchorRow = _activeRow = _activeCol = -1;
        RepaintRange();
    }

    private void OnTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _ctrlDown = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        // A click anywhere in a cell selects that cell (Excel-style focused-cell outline) and — since
        // every editable cell now rests as plain text — immediately enters edit mode: a LazyEditCell
        // materialises its editor and opens it (caret in a TextBox, dropdown for a combo/date). A click
        // that already landed directly on a live editor (TextBox/ComboBox/date picker) is left alone so
        // the caret lands where clicked.
        var cell = CellFromSource(e.Source as Visual);
        if (cell is null)
            return;

        // An open combo dropdown is a light-dismiss popup: a press on a different cell dismisses it, but
        // that dismissing press is swallowed by the overlay, so the cell the user actually clicked never
        // gets selected/entered — the dropdown just closes and the click is lost. When we detect a press
        // on a cell OTHER than the one whose combo is open, close that dropdown ourselves and re-dispatch
        // the selection to the clicked cell, so a single click moves to it (and opens its editor) as if
        // no dropdown had been open. The press itself is handled so it can't also re-toggle anything.
        if (OpenComboCell() is { } openCell && !ReferenceEquals(openCell, cell))
        {
            CloseOpenCombo();
            e.Handled = true;
            var target = cell;
            Dispatcher.UIThread.Post(() => EnterCellFromClick(target), DispatcherPriority.Input);
            return;
        }

        // Shift+Click extends the selection rectangle to the clicked cell, keeping the anchor (and the
        // focus/edit cell) where it was. No row re-select, no begin-edit — it's a pure range gesture.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _anchorRow >= 0
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (RowItemFromCell(cell) is { } shiftRow)
            {
                e.Handled = true;
                ExtendRangeTo(RowIndexOf(shiftRow), cell.ColumnIndex);
                return;
            }
        }

        _focusedColumn = cell.ColumnIndex;

        // Select the clicked cell's row. The ListBox would normally do this itself on bubble, but a
        // click that lands in an editor (and focuses it / begins editing) never reaches that path, so
        // the row would stay unselected. Set it explicitly from the row's data context.
        if (RowItemFromCell(cell) is { } row)
        {
            SelectedItem = row;
            // A plain click is a new anchor: collapse any prior range onto this cell.
            CollapseRange(RowIndexOf(row), cell.ColumnIndex);
        }

        if (e.Source is Visual src && IsInsideEditor(src, cell))
            return; // let the live editor take the click (caret placement / text drag-select)

        // Arm a press-drag range selection: the user can now sweep out a rectangle without releasing.
        // Only for a press on a resting cell (not a live editor, handled above) so in-cell text
        // drag-select keeps working. We capture lazily on first move (see OnTunnelPointerMoved) so a
        // plain click that opens an editor isn't robbed of the pointer.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragging = true;
            _dragExtended = false;
        }

        // A resting lazy cell: focus the SheetCell for the outline now, but DEFER opening the editor to
        // pointer release. That way a press-and-drag selects a range without ever entering edit mode;
        // a plain click (press with no drag) opens the editor on release (see OnTunnelPointerReleased).
        if (FindLazyCell(cell.Content as Control) is { } lazy)
        {
            cell.Focus();
            _pendingEditCell = lazy;
            _pendingEditPoint = e.GetPosition(lazy);
            return;
        }

        cell.Focus();
    }

    // While the button is held after a press (the pointer is captured), sweeping over cells extends the
    // selection rectangle to the cell under the pointer — without stealing focus or beginning an edit.
    // We only treat it as a drag once the pointer reaches a *different* cell, so a plain click that
    // lands and opens an editor in place is unaffected.
    private void OnTunnelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        // Track the live pointer position (in table coordinates) so the edge-autoscroll timer can keep
        // extending the selection toward the cursor while the body scrolls, even with no new move events.
        _dragPoint = e.GetPosition(this);

        // First real move out of the anchor cell: grab the pointer so moves keep coming to us even when
        // the cursor leaves the table, and so the cell's editor doesn't also react to the drag. We grab
        // it as soon as the pointer leaves the anchor cell OR reaches an autoscroll edge.
        var leftAnchorCell = ExtendDragSelectionTo(_dragPoint);
        UpdateEdgeAutoScroll(_dragPoint);

        if (!leftAnchorCell && !_edgeScrollActive)
            return;

        if (e.Pointer.Captured != this)
            e.Pointer.Capture(this);
        e.Handled = true;
    }

    // Hit-test the cell under a table-space point and extend the selection rectangle to it. Returns true
    // when the pointer is over a cell other than the current active corner (a real drag step), so the
    // caller can decide to capture the pointer / mark the gesture a drag. Shared by the move handler and
    // the edge-autoscroll timer.
    private bool ExtendDragSelectionTo(Point point)
    {
        // Hit-test by position rather than e.Source: once we capture the pointer the source is the
        // capture target, not the cell under the cursor.
        SheetCell? cell = null;
        foreach (var hit in this.GetVisualsAt(point))
            if (CellFromSource(hit) is { } c) { cell = c; break; }
        if (cell is null || RowItemFromCell(cell) is not { } row)
            return false;

        var rowIndex = RowIndexOf(row);
        if (rowIndex == _activeRow && cell.ColumnIndex == _activeCol)
            return false; // still in the same cell — nothing to extend

        // A real drag-select: cancel the deferred edit so releasing won't drop into edit mode.
        _dragExtended = true;
        _pendingEditCell = null;
        ExtendRangeTo(rowIndex, cell.ColumnIndex);
        return true;
    }

    private void OnTunnelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        StopEdgeAutoScroll();
        if (e.Pointer.Captured == this)
            e.Pointer.Capture(null);

        // A plain click (press with no drag) opens the editor we deferred at press time. A drag-select
        // (the pointer left the anchor cell) leaves _pendingEditCell cleared, so no edit begins.
        if (!_dragExtended && _pendingEditCell is { } lazy)
        {
            _editingLazy = lazy;
            lazy.BeginEdit(open: true, caretAt: _pendingEditPoint);
        }
        _pendingEditCell = null;
    }

    // Recompute the autoscroll direction/speed from where the drag pointer sits relative to the body's
    // viewport, then start or stop the timer. Within EdgeScrollMargin of an edge (or beyond it) we scroll
    // toward that edge; the further past the margin the cursor is, the larger the per-tick step (capped).
    private void UpdateEdgeAutoScroll(Point point)
    {
        if (_bodyScroll is null)
        {
            StopEdgeAutoScroll();
            return;
        }

        // The body viewport in table coordinates (the scroller occupies the body's bounds).
        var topLeft = _bodyScroll.TranslatePoint(new Point(0, 0), this) ?? new Point(0, 0);
        var bounds = new Rect(topLeft, _bodyScroll.Bounds.Size);
        var extent = _bodyScroll.Extent;
        var viewport = _bodyScroll.Viewport;
        var offset = _bodyScroll.Offset;

        double dx = 0, dy = 0;
        // Vertical: only when there's hidden content in that direction, so we don't spin uselessly.
        if (point.Y < bounds.Top + EdgeScrollMargin && offset.Y > 0)
            dy = -EdgeStep(bounds.Top + EdgeScrollMargin - point.Y);
        else if (point.Y > bounds.Bottom - EdgeScrollMargin && offset.Y < extent.Height - viewport.Height - 0.5)
            dy = EdgeStep(point.Y - (bounds.Bottom - EdgeScrollMargin));
        // Horizontal: same, mirrored.
        if (point.X < bounds.Left + EdgeScrollMargin && offset.X > 0)
            dx = -EdgeStep(bounds.Left + EdgeScrollMargin - point.X);
        else if (point.X > bounds.Right - EdgeScrollMargin && offset.X < extent.Width - viewport.Width - 0.5)
            dx = EdgeStep(point.X - (bounds.Right - EdgeScrollMargin));

        _edgeScrollDelta = new Vector(dx, dy);
        if (dx == 0 && dy == 0)
        {
            StopEdgeAutoScroll();
            return;
        }

        if (_edgeScrollTimer is null)
        {
            _edgeScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _edgeScrollTimer.Tick += OnEdgeScrollTick;
        }
        _edgeScrollActive = true;
        if (!_edgeScrollTimer.IsEnabled)
            _edgeScrollTimer.Start();
    }

    // Maps the pointer's overshoot past the edge margin (px) to a per-tick scroll step, ramping linearly
    // from a gentle 4px at the margin to EdgeScrollMaxStep deep past it.
    private static double EdgeStep(double overshoot)
        => Math.Clamp(4 + overshoot * 0.6, 4, EdgeScrollMaxStep);

    private void StopEdgeAutoScroll()
    {
        _edgeScrollActive = false;
        _edgeScrollTimer?.Stop();
    }

    // One autoscroll step: nudge the body offset (clamped to its scrollable range) toward the edge, then
    // re-hit-test the (stationary) cursor against the freshly scrolled rows and extend the selection.
    private void OnEdgeScrollTick(object? sender, EventArgs e)
    {
        if (!_dragging || _bodyScroll is null)
        {
            StopEdgeAutoScroll();
            return;
        }

        var extent = _bodyScroll.Extent;
        var viewport = _bodyScroll.Viewport;
        var offset = _bodyScroll.Offset;
        var maxX = Math.Max(0, extent.Width - viewport.Width);
        var maxY = Math.Max(0, extent.Height - viewport.Height);
        var newOffset = new Vector(
            Math.Clamp(offset.X + _edgeScrollDelta.X, 0, maxX),
            Math.Clamp(offset.Y + _edgeScrollDelta.Y, 0, maxY));
        if (newOffset == offset)
            return; // already at the edge in that direction
        _bodyScroll.Offset = newOffset;

        // Extend the selection to whatever cell now sits under the unchanged cursor. Deferred to Loaded so
        // the rows realized by the scroll above exist before we hit-test them.
        Dispatcher.UIThread.Post(() =>
        {
            if (_dragging)
                ExtendDragSelectionTo(_dragPoint);
        }, DispatcherPriority.Loaded);
    }

    // The cell whose combo dropdown is currently open (a lazy combo cell we put into edit, or a combo we
    // opened directly), or null when nothing is open. Used to redirect a click that lands on a different
    // cell while a dropdown is open: the popup's light dismiss swallows that click, so we close it
    // ourselves and re-dispatch the entry to the clicked cell.
    private SheetCell? OpenComboCell()
    {
        Visual? combo = _editingLazy?.Editor is ComboBox { IsDropDownOpen: true } lazyCombo ? lazyCombo
            : _openCombo is { IsDropDownOpen: true } open ? open
            : null;
        if (combo is null)
            return null;
        var v = (Visual?)combo;
        while (v is not null)
        {
            if (v is SheetCell cell)
                return cell;
            v = v.GetVisualParent();
        }
        return null;
    }

    private void CloseOpenCombo()
    {
        if (_editingLazy?.Editor is ComboBox { IsDropDownOpen: true } lazyCombo)
            lazyCombo.IsDropDownOpen = false;
        if (_openCombo is { IsDropDownOpen: true })
            _openCombo.IsDropDownOpen = false;
    }

    // Select and enter a cell as if it had just been clicked: select its row, collapse the range onto it,
    // focus it, and (for a lazy cell) open its editor. Used to complete a click that we had to intercept
    // because an open combo dropdown would otherwise have swallowed it (see OnTunnelPointerPressed).
    private void EnterCellFromClick(SheetCell cell)
    {
        _focusedColumn = cell.ColumnIndex;
        if (RowItemFromCell(cell) is { } row)
        {
            SelectedItem = row;
            CollapseRange(RowIndexOf(row), cell.ColumnIndex);
        }
        cell.Focus();
        if (FindLazyCell(cell.Content as Control) is { } lazy)
        {
            _editingLazy = lazy;
            lazy.BeginEdit(open: true);
        }
    }

    // Right-click on a body cell offers "filter by this value" (a Values filter pinned to that cell's
    // text) and, when the column already has a filter, "clear filter". Left untouched: left click and
    // the focus-to-edit flow. Skips non-filterable columns (actions / no value path).
    //
    // Each item shows its keyboard shortcut on the right; the shortcut keys themselves are wired in
    // OnCellShortcutKey so the menu and the keyboard stay in lockstep (one set of action helpers).
    private void OnCellRightClick(object? sender, PointerPressedEventArgs e)
    {
        if (Localization is null || e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.RightButtonPressed)
            return;
        var cell = CellFromSource(e.Source as Visual);
        if (cell is null || !cell.Column.Filterable)
            return;
        if (RowItemFromCell(cell) is not { } row)
            return;

        e.Handled = true; // don't let the right-press begin editing / move selection
        var column = cell.Column;
        var value = CellText(column, row);

        var menu = new StackPanel { Spacing = 2, Margin = new Thickness(4) };
        var flyout = new Flyout
        {
            Placement = PlacementMode.Pointer,
            Content = new LayoutTransformControl
            {
                LayoutTransform = SheetColumnsButton.BuildUiScaleTransform(),
                Child = menu
            }
        };

        // A ghost button whose content is the localized label on the left and a grey shortcut hint on
        // the right. Clicking hides the flyout then runs the action.
        Button MenuItem(string textKey, string shortcut, Action onClick)
        {
            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var label = new TextBlock { Text = Localization.Get(textKey), VerticalAlignment = VerticalAlignment.Center };
            var hint = new TextBlock
            {
                Text = shortcut,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(24, 0, 0, 0),
                Foreground = (IBrush?)this.FindResource("TextSecondary") ?? Brushes.Gray
            };
            Grid.SetColumn(label, 0);
            Grid.SetColumn(hint, 1);
            content.Children.Add(label);
            content.Children.Add(hint);

            var b = new Button
            {
                Classes = { "ghost" },
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 6)
            };
            b.Click += (_, _) => { flyout.Hide(); onClick(); };
            menu.Children.Add(b);
            return b;
        }

        MenuItem("Sheet.Filter.ByValue", "F3", () => FilterByValue(column, value));

        if (_filters.ContainsKey(column.Key))
            MenuItem("Sheet.Filter.Remove", "Shift+F3", () => ClearColumnFilter(column.Key));

        // Column-specific extra: a chip column adds a "mark (non-)rental" item, labelled for the chip's
        // current state. Filter-by-value stays the default for every cell; this is the per-column add-on.
        var chip = column.RentalChipColumn ? value.Trim() : string.Empty;
        if (chip.Length > 0 && ToggleRentalChipCommand?.CanExecute(chip) == true)
        {
            var isRental = RentalChips?.Contains(chip) == true;
            MenuItem(isRental ? "Participants.Chip.UnmarkRental" : "Participants.Chip.MarkRental",
                "F4", () => ToggleRental(chip));
        }

        // Column-specific extra: a "bulk edit this column" item when the page reports the column is
        // bulk-editable. It opens the page's bulk-edit modal preselected to this column.
        if (CanBulkEditColumn?.Invoke(column) == true)
            MenuItem("Sheet.Columns.BulkEdit", "F6", () => BulkEditColumnRequested?.Invoke(this, column));

        // Hiding the selected cell's column — also available from the header menu, here as a cell action.
        MenuItem("Sheet.Columns.Hide", "Ctrl+H", () => HideColumn(column));

        flyout.ShowAt(cell);
    }

    // Apply a "filter by this value" Values filter for the column (shared by the menu and the F3 key).
    private void FilterByValue(SheetColumn column, string value)
    {
        SetColumnFilter(column.Key, new SheetFilter
        {
            ColumnKey = column.Key,
            Header = string.IsNullOrEmpty(column.PickerLabel) ? column.Header : column.PickerLabel,
            Mode = SheetFilterMode.Values,
            AllowedValues = new HashSet<string> { value }
        });

        // Re-binding the body's ItemsSource destroyed the focused SheetCell, so keyboard focus would
        // otherwise escape the table (leaving the row visually selected but no cell focused, and a
        // follow-up Shift+F3 then misses the table's own handler). The filtered-by-its-own-value row
        // survives the filter, so put focus back on its same-column cell once the body relayouts.
        Dispatcher.UIThread.Post(FocusSelectedRowCell, DispatcherPriority.Loaded);
    }

    // The key chords that map to a cell context-menu action: F3 (filter), Shift+F3 (clear), F4 (rental),
    // F6 (bulk-edit), Ctrl+H (hide). Recognised before the edit branch in OnTunnelKeyDown so they fire
    // even while a cell is being edited — they're keys a text editor never needs.
    private static bool IsCellShortcut(KeyEventArgs e)
    {
        if (e.Key == Key.H && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return true;
        var plain = (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None;
        return plain && e.Key is Key.F3 or Key.F4 or Key.F6;
    }

    // Keyboard shortcuts for the cell context-menu actions, dispatched against the focused cell's
    // column + row. Mirrors OnCellRightClick exactly: each key runs the same action, and is a no-op
    // when that action isn't available for the column (non-filterable, not a chip, not bulk-editable).
    // Returns true if the key was an action key (so the caller can mark it handled).
    private bool OnCellShortcutKey(KeyEventArgs e)
    {
        // Ctrl+H hides the focused cell's column regardless of filterability (actions aside).
        if (e.Key == Key.H && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (FindFocusedCell()?.Column is { } hideCol && hideCol.Filterable)
                HideColumn(hideCol);
            return true;
        }

        // The remaining shortcuts are unmodified F-keys (Shift+F3 is the one exception).
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var plain = (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None;
        if (!plain || e.Key is not (Key.F3 or Key.F4 or Key.F6))
            return false;

        // Shift+F3 clears a filter even with no column in focus: the focused column's filter if it has
        // one, otherwise the most recently added filter on the table. Handled before the filterable-cell
        // gate so it works when no cell (or a non-filterable one) is selected.
        if (e.Key == Key.F3 && shift)
        {
            var focusedKey = FindFocusedCell()?.Column?.Key;
            if (focusedKey is not null && _filters.ContainsKey(focusedKey))
                ClearColumnFilter(focusedKey);
            else
                ClearLastFilter();
            return true;
        }

        if (FindFocusedCell() is not { } cell || !cell.Column.Filterable)
            return e.Key is Key.F3 or Key.F4 or Key.F6; // swallow the key even with nothing to act on
        if (RowItemFromCell(cell) is not { } row)
            return true;

        var column = cell.Column;
        var value = CellText(column, row);

        switch (e.Key)
        {
            case Key.F3:
                FilterByValue(column, value);
                return true;
            case Key.F4:
                var chip = column.RentalChipColumn ? value.Trim() : string.Empty;
                if (chip.Length > 0 && ToggleRentalChipCommand?.CanExecute(chip) == true)
                    ToggleRental(chip);
                return true;
            case Key.F6:
                if (CanBulkEditColumn?.Invoke(column) == true)
                    BulkEditColumnRequested?.Invoke(this, column);
                return true;
        }
        return false;
    }

    // The row item a cell belongs to: the cell inherits the row container's DataContext (the item).
    private object? RowItemFromCell(SheetCell cell)
    {
        var row = cell.DataContext;
        if (row is null || _body?.ItemsSource is null)
            return row;
        // Make sure it's actually one of the rows we display (guards against header/stray contexts).
        foreach (var item in _body.ItemsSource)
            if (ReferenceEquals(item, row))
                return row;
        return null;
    }

    // Walks up from a hit-tested visual to the SheetCell that contains it (or null).
    private static SheetCell? CellFromSource(Visual? source)
    {
        var v = source;
        while (v is not null)
        {
            if (v is SheetCell cell)
                return cell;
            v = v.GetVisualParent();
        }
        return null;
    }

    // The lazy cell a SheetCell hosts, if any: either its direct content or the first visible one nested
    // in a wrapper (the roster's per-day backdrop Panel, or a collapsed block's combo/"різні" Panel).
    private static LazyEditCell? FindLazyCell(Control? content)
    {
        if (content is null)
            return null;
        if (content is LazyEditCell direct)
            return direct;
        foreach (var d in content.GetVisualDescendants())
            if (d is LazyEditCell lazy && lazy.IsVisible && lazy.IsEnabled)
                return lazy;
        return null;
    }

    // True when the clicked visual is (inside) an interactive editor within the cell.
    private static bool IsInsideEditor(Visual source, SheetCell cell)
    {
        var v = source;
        while (v is not null && v != cell)
        {
            if (v is TextBox or ComboBox or CalendarDatePicker or Button or CheckBox)
                return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+F focuses the toolbar's global-search box from anywhere in the table (incl. mid-edit),
        // selecting any existing term so the user can type over it. Handled first so it wins over the
        // cell-edit / navigation branches below.
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _search is not null)
        {
            e.Handled = true;
            FocusSearch();
            return;
        }

        // The focused element decides whether we're already editing (a focused TextBox in a cell).
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

        // A combo opened via Enter drives its own keyboard: arrows move the highlight, Enter commits,
        // Escape closes. When the dropdown opens focus lands on a ComboBoxItem *inside the popup*, not
        // on the ComboBox itself, so resolve the owning combo by walking up. Stay out of its way so
        // cell-nav doesn't hijack those keys — except we step down a row after a commit, like text cells.
        if (OwningComboBox(focused as Visual) is { } combo)
        {
            if (e.Key == Key.Enter && combo.IsDropDownOpen)
                Dispatcher.UIThread.Post(() => MoveRow(+1), DispatcherPriority.Background);
            return;
        }

        _editing = focused is TextBox;

        // Context-menu action shortcuts must work even while a cell is in edit mode (focus is on the
        // cell's TextBox). They're F-keys / Ctrl+H — keys a text editor never needs — so intercept them
        // before the editing branch: commit the open edit first (so e.g. "filter by value" sees what was
        // just typed), then run the action on the next cycle once the bound value has propagated.
        if (IsCellShortcut(e))
        {
            e.Handled = true;
            if (_editing)
            {
                CommitEdit();
                Dispatcher.UIThread.Post(() => OnCellShortcutKey(e), DispatcherPriority.Background);
            }
            else
            {
                OnCellShortcutKey(e);
            }
            return;
        }

        if (_editing)
        {
            var box = focused as TextBox;

            // Ctrl+V while editing a fill-down column: a *multi-line* clipboard should fill down the
            // rows, but a single value should still paste into the caret as usual. We can't read the
            // clipboard synchronously to decide, so swallow the key (stops the TextBox's default paste)
            // and decide asynchronously in PasteWhileEditing.
            if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control)
                && box is not null && FindFocusedCell()?.Column?.PastePath is not null)
            {
                e.Handled = true;
                PasteWhileEditing(box);
                return;
            }

            // Enter/Escape leave edit mode by returning focus to the cell host (the TextBox commits on
            // LostFocus); Enter then advances down a row like a spreadsheet.
            if (e.Key is Key.Enter or Key.Escape && FindFocusedCell() is { } editingCell)
            {
                editingCell.Focus();
                e.Handled = true;
                if (e.Key == Key.Enter)
                    Dispatcher.UIThread.Post(() => MoveRow(+1), DispatcherPriority.Background);
                return;
            }

            // Up/Down commit the edit and move to the adjacent row's same-column cell, like a
            // spreadsheet. Without this the key would leak to the body ListBox, which moves its
            // selection but leaves no cell focused (forcing a second click to resume editing).
            if (e.Key is Key.Up or Key.Down && CommitEdit())
            {
                e.Handled = true;
                var delta = e.Key == Key.Down ? +1 : -1;
                Dispatcher.UIThread.Post(() => MoveRow(delta), DispatcherPriority.Background);
                return;
            }

            // Left/Right move the caret within the text until it reaches an edge; at the edge they
            // commit and step to the neighbouring cell (Tab still works mid-text for an explicit jump).
            if (e.Key == Key.Left && box is not null && box.CaretIndex == 0 && CommitEdit())
            {
                e.Handled = true;
                MoveColumn(-1);
                return;
            }
            if (e.Key == Key.Right && box is not null && box.CaretIndex >= (box.Text?.Length ?? 0) && CommitEdit())
            {
                e.Handled = true;
                MoveColumn(+1);
                return;
            }

            // Anything else (incl. mid-text arrows/Tab) stays with the editor for caret movement / typing.
            return;
        }

        // Shift+Arrow extends the selection rectangle from the anchor (which keeps focus), Excel-style.
        // Shift+Tab is left as plain column navigation, not range-extend.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _anchorRow >= 0
            && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var dCol = e.Key == Key.Left ? -1 : e.Key == Key.Right ? +1 : 0;
            var dRow = e.Key == Key.Up ? -1 : e.Key == Key.Down ? +1 : 0;
            if (ExtendRange(dRow, dCol))
                e.Handled = true;
            return;
        }

        // Arrow / Tab navigation between cells (only when a cell, not an editor, holds focus).
        switch (e.Key)
        {
            case Key.Left:
                if (MoveColumn(-1)) e.Handled = true;
                return;
            case Key.Right:
                if (MoveColumn(+1)) e.Handled = true;
                return;
            case Key.Tab:
                if (MoveColumn(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : +1)) e.Handled = true;
                return;
            case Key.Up:
                if (MoveRow(-1)) e.Handled = true;
                return;
            case Key.Down:
                if (MoveRow(+1)) e.Handled = true;
                return;
        }

        var ctrlOrAlt = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        // (Context-menu action shortcuts — F3/Shift+F3/F4/F6/Ctrl+H — are handled at the top of this
        //  method so they fire in edit mode too; see the IsCellShortcut check there.)

        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (CopyFocusedCell())
                e.Handled = true;
            return;
        }

        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // A multi-line clipboard pasted onto a fill-down-capable column writes one line per
            // successive row (Excel-style). Otherwise fall back to single-cell paste into the editor.
            e.Handled = true;
            Dispatcher.UIThread.Post(PasteFromClipboard, DispatcherPriority.Background);
            return;
        }

        if (e.Key is Key.Enter or Key.F2)
        {
            BeginEditFocusedCell();
            e.Handled = true;
            return;
        }

        if (!ctrlOrAlt && !string.IsNullOrEmpty(e.KeySymbol))
        {
            var symbol = e.KeySymbol;
            if (BeginEditFocusedCell())
            {
                e.Handled = true;
                Dispatcher.UIThread.Post(() => SeedEditor(symbol), DispatcherPriority.Background);
            }
        }
    }

    private void OnBubbleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
            return;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
            return;
        if (SelectedItem is null)
            return;

        e.Handled = true;
        DeleteRequested?.Invoke(this, new SheetDeleteEventArgs(SelectedItem, e.KeyModifiers.HasFlag(KeyModifiers.Control)));
    }

    // Find the focused cell and put its editor into edit. Every editable cell is now a LazyEditCell, so
    // we ask it to begin editing: a text cell focuses its TextBox for the caret; a combo/date cell opens
    // its list/calendar so the user can pick with the keyboard straight away. A few non-lazy cells
    // remain (a bare ComboBox or TextBox in a custom column), handled as before.
    private bool BeginEditFocusedCell()
    {
        if (FindFocusedCell()?.Content is not Control content)
            return false;

        // Editing happens on the anchor (focus) cell — collapse any active selection onto it first.
        if (HasRange)
            CollapseRange(_anchorRow, _focusedColumn);

        if (FindLazyCell(content) is { } lazy)
        {
            _editingLazy = lazy;
            lazy.BeginEdit(open: lazy.OpensOnEnter);
            return true;
        }

        if (FindVisibleCombo(content) is { } combo)
            return OpenCombo(combo);

        var box = content as TextBox ?? FindDescendantTextBox(content);
        if (box is null)
            return false;

        box.Focus();
        return true;
    }

    // The combo whose dropdown the table opened (via Enter/F2), so the key handler can recognise its
    // popup focus and step away. Cleared when it closes.
    private ComboBox? _openCombo;

    // The lazy cell currently being edited (set whenever we ask one to BeginEdit). Lets the key handler
    // resolve the live editor — whose dropdown popup is a separate visual tree from the focused element
    // — without the table tracking each combo's open/close lifecycle itself.
    private LazyEditCell? _editingLazy;

    // Open a combo's dropdown and move focus into it so arrow keys move the highlight and Enter
    // commits — no mouse needed. Focusing the ComboBox before opening lets its own key handling drive
    // the open popup; opening moves focus to the selected ComboBoxItem inside the popup.
    private bool OpenCombo(ComboBox combo)
    {
        _openCombo = combo;
        EventHandler? onClosed = null;
        onClosed = (_, _) =>
        {
            if (ReferenceEquals(_openCombo, combo))
                _openCombo = null;
            combo.DropDownClosed -= onClosed;
        };
        combo.DropDownClosed += onClosed;

        combo.Focus();
        combo.IsDropDownOpen = true;
        return true;
    }

    // The combo that currently owns keyboard focus: the focused element itself, the combo we opened
    // (focus is on a ComboBoxItem in its popup, a separate visual tree), or a combo we can walk up to.
    private ComboBox? OwningComboBox(Visual? focused)
    {
        if (focused is null)
            return null;
        // A lazy combo cell we put into edit: its dropdown popup is a separate visual tree, so resolve
        // it from the cell's live editor rather than by walking up from the focused popup item.
        if (_editingLazy?.Editor is ComboBox { IsDropDownOpen: true } lazyCombo)
            return lazyCombo;
        if (_openCombo is { IsDropDownOpen: true })
            return _openCombo;
        var v = focused;
        while (v is not null)
        {
            if (v is ComboBox combo)
                return combo;
            v = v.GetVisualParent();
        }
        return null;
    }

    // The combo a cell edits, if any. A cell may host a bare ComboBox or wrap it in a Panel (the
    // roster's collapsed group cell), so fall back to the first visible descendant combo.
    private static ComboBox? FindVisibleCombo(Control content)
    {
        if (content is ComboBox direct)
            return direct;
        foreach (var d in content.GetVisualDescendants())
            if (d is ComboBox combo && combo.IsVisible && combo.IsEnabled)
                return combo;
        return null;
    }

    // Leave edit mode by moving focus back to the cell host — the editor's TwoWay/LostFocus binding
    // commits as it loses focus. Returns false if there's no focused cell to fall back to.
    private bool CommitEdit()
    {
        if (FindFocusedCell() is not { } cell)
            return false;
        cell.Focus();
        return true;
    }

    private SheetCell? FindFocusedCell()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        while (focused is not null)
        {
            if (focused is SheetCell cell)
                return cell;
            focused = focused.GetVisualParent();
        }
        return null;
    }

    // ── Cell navigation ───────────────────────────────────────────────────────────────────────────
    // Move the focused cell left/right within its own row (delta = ±1). Returns false at the edges.
    private bool MoveColumn(int delta)
    {
        var current = FindFocusedCell();
        if (current?.GetVisualParent() is not Grid rowGrid)
            return false;

        var target = current.ColumnIndex + delta;
        foreach (var child in rowGrid.Children)
            if (child is SheetCell cell && cell.ColumnIndex == target)
            {
                cell.Focus();
                // Plain column move: the new cell becomes the anchor, collapsing any selection.
                CollapseRange(RowIndexOf(_body?.SelectedItem), target);
                return true;
            }
        return false;
    }

    // Move the selection to the row above/below (delta = ±1) and focus the cell at the same column.
    // Driving the ListBox selection scrolls the (possibly virtualized) target row into view first.
    private bool MoveRow(int delta)
    {
        if (_body?.ItemsSource is null)
            return false;

        // Use the cached current order (built in ApplySortedView) rather than re-materialising and
        // re-scanning the whole collection on every arrow keystroke.
        var items = _sortedItems;
        var index = -1;
        for (var i = 0; i < items.Count; i++)
            if (Equals(items[i], _body.SelectedItem))
            {
                index = i;
                break;
            }

        var target = index + delta;
        if (index < 0 || target < 0 || target >= items.Count)
            return false;

        _body.SelectedItem = items[target];
        _body.ScrollIntoView(target);
        // Plain row move: collapse any selection onto the new cell (same column, target row).
        CollapseRange(target, _focusedColumn);
        // The target row may need a layout pass to realize; focus its cell once it exists.
        Dispatcher.UIThread.Post(FocusSelectedRowCell, DispatcherPriority.Loaded);
        return true;
    }

    // Focus the cell at _focusedColumn in the currently selected row's container.
    private void FocusSelectedRowCell()
    {
        if (_body is null || _body.SelectedItem is null)
            return;
        if (_body.ContainerFromItem(_body.SelectedItem) is not Control container)
            return;

        foreach (var d in container.GetVisualDescendants())
            if (d is SheetCell cell && cell.ColumnIndex == _focusedColumn)
            {
                cell.Focus();
                return;
            }
    }

    private static TextBox? FindDescendantTextBox(Control root)
    {
        foreach (var d in root.GetVisualDescendants())
            if (d is TextBox box)
                return box;
        return null;
    }

    // ── Multi-cell range selection ──────────────────────────────────────────────────────────────────
    // Grow/shrink the selection rectangle by (dRow, dCol) from its current active corner; the anchor
    // (and focus/edit cell) stays put. Scrolls a newly-reached row into view. Returns false at edges.
    private bool ExtendRange(int dRow, int dCol)
    {
        if (_anchorRow < 0)
            return false;
        var row = Math.Clamp(_activeRow + dRow, 0, Math.Max(0, _sortedItems.Count - 1));
        var col = Math.Clamp(_activeCol + dCol, 0, Math.Max(0, LeafColumnCount() - 1));
        if (row == _activeRow && col == _activeCol)
            return false;
        ExtendRangeTo(row, col);
        if (dRow != 0)
            _body?.ScrollIntoView(row);
        return true;
    }

    // Set the rectangle's active corner to (row, col) and repaint the highlight. The anchor is left
    // untouched so focus and editing stay on the first selected cell.
    private void ExtendRangeTo(int row, int col)
    {
        if (_anchorRow < 0 || row < 0 || col < 0)
            return;
        _activeRow = row;
        _activeCol = col;
        RepaintRange();
        // Newly realized rows (after a scroll) need the highlight re-applied once laid out.
        Dispatcher.UIThread.Post(RepaintRange, DispatcherPriority.Loaded);
    }

    // Re-paint every realized body cell's InRange flag to match the current rectangle (or clear all,
    // when there's no multi-cell selection). Off-screen rows are virtualized away and re-painted when
    // they realize (see ExtendRangeTo's posted pass).
    private void RepaintRange()
    {
        if (_body is null)
            return;
        foreach (var d in _body.GetVisualDescendants())
            if (d is SheetCell cell)
                cell.InRange = CellInRange(cell);
    }

    // Refresh the range highlight on a single (just-realized) row container's cells.
    private void UpdateContainerRange(Control container)
    {
        foreach (var d in container.GetVisualDescendants())
            if (d is SheetCell cell)
                cell.InRange = CellInRange(cell);
    }

    // True when a cell falls inside the current selection rectangle (false when there's no range).
    private bool CellInRange(SheetCell cell)
    {
        if (!HasRange || cell.DataContext is not { } item)
            return false;
        var index = RowIndexOf(item);
        if (index < 0)
            return false;
        var minRow = Math.Min(_anchorRow, _activeRow);
        var maxRow = Math.Max(_anchorRow, _activeRow);
        var minCol = Math.Min(_focusedColumn, _activeCol);
        var maxCol = Math.Max(_focusedColumn, _activeCol);
        return index >= minRow && index <= maxRow && cell.ColumnIndex >= minCol && cell.ColumnIndex <= maxCol;
    }

    private void SeedEditor(string text)
    {
        if (FocusedEditor() is { } box)
        {
            box.Text = text;
            box.CaretIndex = text.Length;
        }
    }

    // Copy the focused cell's text to the clipboard (Ctrl+C). Returns false if there's no focused
    // cell or it has no copyable text. Reads the cell's display value rather than a binding path so
    // it works uniformly across every cell kind (text editors, group combos, dates, "різні").
    private bool CopyFocusedCell()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return false;

        // A multi-cell selection copies as TSV (tab between columns, newline between rows) so it
        // round-trips with Excel/Sheets and the table's own fill-down paste.
        if (HasRange)
        {
            _ = clipboard.SetTextAsync(BuildRangeTsv());
            return true;
        }

        if (FindFocusedCell() is not { } cell)
            return false;
        var text = ExtractCellText(cell.Content as Control);
        if (string.IsNullOrEmpty(text))
            return false;

        _ = clipboard.SetTextAsync(text);
        return true;
    }

    // Render the current selection rectangle as TSV (tab between columns, newline between rows). For
    // each cell we prefer the *realized* cell's rendered text (ExtractCellText) so copy matches exactly
    // what's on screen — the collapsed "<group> (n днів)" / "різні" summaries, combo labels, the payment
    // value (not its status), and discount check states. Rows scrolled out of view aren't realized, so
    // those fall back to CellText (the value-path reader). Off-screen composite/checkbox columns are the
    // only case where the two can differ — acceptable for a visible-selection copy.
    private string BuildRangeTsv()
    {
        var minRow = Math.Min(_anchorRow, _activeRow);
        var maxRow = Math.Max(_anchorRow, _activeRow);
        var minCol = Math.Min(_focusedColumn, _activeCol);
        var maxCol = Math.Max(_focusedColumn, _activeCol);

        // Map leaf-column index -> SheetColumn, in the same order BuildRow lays cells out.
        var columns = new List<SheetColumn>();
        foreach (var band in _visibleBands)
            foreach (var column in band.Columns)
                columns.Add(column);

        // Index the realized body cells by (row index in the sorted view, column index) for O(1) lookup.
        var realized = new Dictionary<(int Row, int Col), SheetCell>();
        if (_body is not null)
            foreach (var d in _body.GetVisualDescendants())
                if (d is SheetCell sc && sc.DataContext is { } item)
                {
                    var ri = RowIndexOf(item);
                    if (ri >= 0)
                        realized[(ri, sc.ColumnIndex)] = sc;
                }

        var sb = new System.Text.StringBuilder();
        for (var r = minRow; r <= maxRow && r < _sortedItems.Count; r++)
        {
            if (_sortedItems[r] is not { } row)
                continue;
            for (var c = minCol; c <= maxCol && c < columns.Count; c++)
            {
                if (c > minCol)
                    sb.Append('\t');
                // A realized cell is authoritative (even when blank) so we don't fall back and pick up
                // e.g. the payment column's status token for an empty payment.
                var text = realized.TryGetValue((r, c), out var cell)
                    ? ExtractCellText(cell.Content as Control) ?? string.Empty
                    : CopyText(columns[c], row);
                sb.Append(text);
            }
            if (r < maxRow)
                sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Snapshots the table's current on-screen view for export: the visible leaf columns (in display
    /// order, hidden columns dropped) as the header, then one row per displayed item (the active sort +
    /// filters applied — exactly what <see cref="VisibleItems"/> holds). Each cell uses the same text the
    /// user sees: a realized cell's rendered text wins (so combo labels, collapsed "різні"/"(n днів)"
    /// summaries, payment amounts and checkbox flags match the screen), falling back to the value-path
    /// reader for rows scrolled out of view. The trailing action column carries no value and is excluded.
    /// Returns an empty table (no columns/rows) when the table hasn't been built yet.
    /// </summary>
    public CsvParticipantData ExportView()
    {
        // The visible leaf columns, in the order BuildRow lays cells out; skip the action column (no value).
        var columns = new List<SheetColumn>();
        foreach (var band in _visibleBands)
            foreach (var column in band.Columns)
                if (column.Kind != SheetCellKind.Actions)
                    columns.Add(column);

        var header = new List<string>(columns.Count);
        foreach (var column in columns)
            header.Add(ColumnHeader(column));

        // Index the realized body cells by (row index in the displayed view, leaf-column index) so a
        // visible cell's rendered text is preferred — the same approach BuildRangeTsv uses.
        var realized = new Dictionary<(int Row, int Col), SheetCell>();
        if (_body is not null)
            foreach (var d in _body.GetVisualDescendants())
                if (d is SheetCell sc && sc.DataContext is { } item)
                {
                    var ri = RowIndexOf(item);
                    if (ri >= 0)
                        realized[(ri, sc.ColumnIndex)] = sc;
                }

        var rows = new List<IReadOnlyList<string>>(_sortedItems.Count);
        for (var r = 0; r < _sortedItems.Count; r++)
        {
            if (_sortedItems[r] is not { } row)
                continue;
            var cells = new string[columns.Count];
            for (var c = 0; c < columns.Count; c++)
                // The action column was dropped from `columns` but not from the cell layout, so map our
                // export column index back to the leaf index by header position is unnecessary: the
                // realized cell's ColumnIndex is the leaf index, which equals c here only when no column
                // before it was skipped. The action column is always last, so every kept column's leaf
                // index equals its export index — the lookup stays aligned.
                cells[c] = realized.TryGetValue((r, c), out var cell)
                    ? ExtractCellText(cell.Content as Control) ?? string.Empty
                    : CopyText(columns[c], row);
            rows.Add(cells);
        }

        return new CsvParticipantData { Header = header, Rows = rows };
    }

    // The export header text for a column: the band label + sub-header when a column sits under a named
    // band (e.g. "День 1 — Група"), otherwise the column's own header. Mirrors the picker-label idea so
    // a per-day column reads unambiguously in the file.
    private string ColumnHeader(SheetColumn column)
    {
        foreach (var band in _visibleBands)
        {
            if (!band.Columns.Contains(column))
                continue;
            if (band.Kind == SheetBand.BandKind.FieldBlock && !string.IsNullOrEmpty(band.Header)
                && !string.IsNullOrEmpty(column.Header))
                return $"{band.Header} — {column.Header}";
            break;
        }
        return string.IsNullOrEmpty(column.Header) ? column.PickerLabel : column.Header;
    }

    // A checkbox/flag cell copies as "TRUE" when checked, "" otherwise, so a bool column
    // (discounts, out-of-competition, «Член ФСОУ») round-trips as a readable flag.
    private const string CheckedMark = "TRUE";

    // Pulls the visible text out of a cell's content control, regardless of how it renders it.
    private static string? ExtractCellText(Control? content)
    {
        switch (content)
        {
            case null:
                return null;
            case TextBox box:
                return box.Text;
            case TextBlock block:
                return block.Text;
            case ComboBox combo:
                return combo.SelectedItem is GroupOption option ? option.Label : combo.SelectionBoxItem?.ToString();
            case CalendarDatePicker picker:
                return picker.SelectedDate?.ToString("dd.MM.yyyy");
            case CheckBox check:
                return check.IsChecked == true ? CheckedMark : string.Empty;
        }

        // Composite cells (per-day chip Panel with backdrop+editor, collapsed merged cells, the
        // out-of-competition checkbox): pick the first visible descendant that carries a value.
        foreach (var d in content.GetVisualDescendants())
        {
            if (d is not Control c || !c.IsVisible)
                continue;
            switch (c)
            {
                case TextBox box when !string.IsNullOrEmpty(box.Text):
                    return box.Text;
                case TextBlock block when !string.IsNullOrEmpty(block.Text):
                    return block.Text;
                case ComboBox combo:
                    var label = combo.SelectedItem is GroupOption option ? option.Label : combo.SelectionBoxItem?.ToString();
                    if (!string.IsNullOrEmpty(label))
                        return label;
                    break;
                case CheckBox check:
                    return check.IsChecked == true ? CheckedMark : string.Empty;
            }
        }
        return null;
    }

    // Ctrl+V intercepted while a fill-down column's editor is focused. A multi-line clipboard commits
    // the edit and fills down the rows (Excel-style); a single value is inserted at the caret, the same
    // as the TextBox's own paste would have done (we suppressed it to make this decision).
    private async void PasteWhileEditing(TextBox box)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text))
            return;

        var lines = SplitClipboardLines(text);
        var path = FindFocusedCell()?.Column?.PastePath;
        if (lines.Length > 1 && path is not null)
        {
            // Leave edit mode (commit the current cell) before filling down so the first line overwrites
            // it cleanly, then write the column straight to the row view models.
            if (CommitEdit())
                Dispatcher.UIThread.Post(() => FillDown(path, lines), DispatcherPriority.Background);
            return;
        }

        // Single value: splice it into the caret, replacing any selection, like a normal paste.
        var current = box.Text ?? string.Empty;
        var start = Math.Min(box.SelectionStart, box.SelectionEnd);
        var end = Math.Max(box.SelectionStart, box.SelectionEnd);
        start = Math.Clamp(start, 0, current.Length);
        end = Math.Clamp(end, 0, current.Length);
        box.Text = current[..start] + text + current[end..];
        box.CaretIndex = start + text.Length;
    }

    // Handle Ctrl+V on a focused (non-editing) cell. A clipboard with several newline-separated lines
    // pasted onto a fill-down-capable column (its SheetColumn carries a PastePath) is written one line
    // per successive row — the spreadsheet "paste a column" gesture. Anything else (single value, or a
    // column with no flat text path) falls back to opening the editor and pasting into it as before.
    private async void PasteFromClipboard()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text))
            return;

        var lines = SplitClipboardLines(text);
        var column = FindFocusedCell()?.Column;

        // A combo (option-list) column resolves the pasted value(s) to an option by exact label match and
        // only then changes the selection — a single value writes the focused row, several lines fill down
        // one row each. A line that matches no option leaves that row's selection untouched. Never opens
        // the dropdown / inserts free text, so pasting can't half-set a combo.
        if (column?.IsComboPaste == true)
        {
            FillDownCombo(column, lines.Length > 1 ? lines : new[] { text });
            return;
        }

        if (lines.Length > 1 && column?.PastePath is { } path && FillDown(path, lines))
            return;

        // Single value (or a column we can't fill down): open the cell's editor and paste into it. The
        // lazy cell focuses its editor on a posted Input-priority pass, so write the text afterwards on
        // a Background pass — the original single-cell paste flow.
        if (BeginEditFocusedCell())
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (FocusedEditor() is { } box)
                {
                    box.Text = text;
                    box.CaretIndex = text.Length;
                }
            }, DispatcherPriority.Background);
        }
    }

    // Write one clipboard line per row to the column's PastePath property, starting at the focused row
    // and walking down the current (sorted/filtered) view. Stops at the end of the table — extra lines
    // are dropped. Returns false if we can't locate the starting row. The two-way-bound property's own
    // setter raises change notification (and any debounced save), so no extra commit is needed.
    private bool FillDown(string path, string[] lines)
    {
        var items = _sortedItems;
        if (items.Count == 0 || _body?.SelectedItem is not { } start)
            return false;

        var startIndex = -1;
        for (var i = 0; i < items.Count; i++)
            if (Equals(items[i], start)) { startIndex = i; break; }
        if (startIndex < 0)
            return false;

        for (var i = 0; i < lines.Length && startIndex + i < items.Count; i++)
            SetStringPath(items[startIndex + i], path, lines[i]);
        return true;
    }

    // Combo fill-down: write one clipboard line per row, starting at the focused row, by resolving each
    // line to an option on THAT row and assigning it to the column's selected-option path. A line is only
    // applied when exactly one of the row's options matches it 1:1 by label (case-insensitive, trimmed);
    // a non-matching (or blank, or ambiguous) line leaves that row's selection unchanged. Each row reads
    // its own options list, so per-row option sets (e.g. each roster day's own GroupOptions) are honoured.
    private void FillDownCombo(SheetColumn column, string[] lines)
    {
        var items = _sortedItems;
        if (items.Count == 0 || _body?.SelectedItem is not { } start)
            return;

        var startIndex = -1;
        for (var i = 0; i < items.Count; i++)
            if (Equals(items[i], start)) { startIndex = i; break; }
        if (startIndex < 0)
            return;

        for (var i = 0; i < lines.Length && startIndex + i < items.Count; i++)
        {
            var row = items[startIndex + i];
            var value = lines[i].Trim();
            if (row is null || value.Length == 0)
                continue;
            if (ResolveComboOption(row, column, value) is { } option)
                SetOptionPath(row, column.ComboSelectedPath!, option);
        }
    }

    // Find the single option on a row whose label matches the pasted value 1:1 (case-insensitive,
    // trimmed). Returns null when no option matches OR more than one does (ambiguous) — the caller leaves
    // the cell unchanged in either case, so a paste can never guess between equally-named options.
    private static object? ResolveComboOption(object row, SheetColumn column, string value)
    {
        if (ReadPath(row, column.ComboItemsPath!) is not IEnumerable options)
            return null;

        object? match = null;
        foreach (var option in options)
        {
            if (option is null)
                continue;
            var label = ReadPath(option, column.ComboLabelPath!) as string;
            if (label is null || !string.Equals(label.Trim(), value, StringComparison.CurrentCultureIgnoreCase))
                continue;
            if (match is not null)
                return null; // ambiguous — two options share this label
            match = option;
        }
        return match;
    }

    // Assign a resolved option to the row's two-way selected-option property, walking any intermediate
    // path segments (e.g. "Days[2].SelectedGroup"). No-op if the path can't be resolved or written.
    private static void SetOptionPath(object row, string path, object option)
    {
        var segments = path.Split('.');
        object? current = row;
        for (var i = 0; i < segments.Length - 1 && current is not null; i++)
            current = ResolveSegment(current, segments[i]);
        if (current is null)
            return;

        var prop = current.GetType().GetProperty(segments[^1], BindingFlags.Public | BindingFlags.Instance);
        if (prop is { CanWrite: true } && prop.PropertyType.IsInstanceOfType(option))
            prop.SetValue(current, option);
    }

    // Split clipboard text into rows. Excel/Sheets copy a single column as CR/LF-separated lines (and a
    // trailing newline); a manually typed list is plain LF. We split on either and drop a single empty
    // trailing line, but keep interior blanks so a deliberately blank cell still clears its row.
    private static string[] SplitClipboardLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.EndsWith('\n'))
            normalized = normalized[..^1];
        return normalized.Split('\n');
    }

    // Resolve a value path like "FeeText" or "Days[2].Chip" against a row view model and assign a
    // string value to the final property. Walks intermediate property/indexer segments; a missing
    // segment or non-string target is a no-op (paste tolerates rows that don't carry the field).
    private static void SetStringPath(object? row, string path, string value)
    {
        if (row is null)
            return;

        var segments = path.Split('.');
        object? current = row;
        for (var i = 0; i < segments.Length - 1 && current is not null; i++)
            current = ResolveSegment(current, segments[i]);

        if (current is null)
            return;

        var last = segments[^1];
        var prop = current.GetType().GetProperty(last, BindingFlags.Public | BindingFlags.Instance);
        if (prop is { CanWrite: true } && prop.PropertyType == typeof(string))
            prop.SetValue(current, value);
    }

    // Resolve one path segment: a plain property name, or "Name[index]" for an indexed list access.
    private static object? ResolveSegment(object target, string segment)
    {
        var bracket = segment.IndexOf('[');
        if (bracket < 0)
            return target.GetType().GetProperty(segment, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);

        var name = segment[..bracket];
        if (!int.TryParse(segment[(bracket + 1)..].TrimEnd(']'), out var index))
            return null;
        var list = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);
        if (list is IList items && index >= 0 && index < items.Count)
            return items[index];
        return null;
    }

    private TextBox? FocusedEditor() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as TextBox;
}

/// <summary>A focusable cell host so the table can select cells and start editing on keystrokes.</summary>
internal sealed class SheetCell : ContentControl
{
    public SheetCell(SheetColumn column, int columnIndex)
    {
        Column = column;
        ColumnIndex = columnIndex;
        Focusable = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        // The transparent (not null) background that makes the whole cell area hit-testable is set via
        // the base SheetCell style, not here, so the :range selection style can override it. (A local
        // value set in the constructor would win over any style.)
    }

    public SheetColumn Column { get; }

    /// <summary>This cell's leaf-column index across the whole row, for up/down navigation.</summary>
    public int ColumnIndex { get; }

    /// <summary>True while this cell lies inside the current multi-cell selection rectangle. Drives a
    /// translucent highlight via the <c>:range</c> pseudo-class; the anchor still shows the focus outline.</summary>
    public static readonly StyledProperty<bool> InRangeProperty =
        AvaloniaProperty.Register<SheetCell, bool>(nameof(InRange));

    public bool InRange
    {
        get => GetValue(InRangeProperty);
        set => SetValue(InRangeProperty, value);
    }

    static SheetCell()
    {
        InRangeProperty.Changed.AddClassHandler<SheetCell>((cell, e) =>
            cell.PseudoClasses.Set(":range", e.NewValue is true));
    }

    private IDisposable? _selectedSub;

    // A tinted cell (payment/age/row highlight) paints its own Background over the ListBoxItem, so the
    // row-selection highlight is hidden under those columns. To keep selection visible everywhere, the
    // cell mirrors its containing row's selected state as a :selected pseudo-class and the SheetCell
    // template paints a translucent wash *above* the tint (see Styles/SheetTable.axaml). We track the
    // ListBoxItem via the visual tree once attached, since a virtualized cell has no owner at build time.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var row = this.FindAncestorOfType<ListBoxItem>();
        if (row is not null)
        {
            _selectedSub = row.GetObservable(ListBoxItem.IsSelectedProperty)
                .Subscribe(new AnonymousObserver<bool>(sel => PseudoClasses.Set(":selected", sel)));
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _selectedSub?.Dispose();
        _selectedSub = null;
        PseudoClasses.Set(":selected", false);
        base.OnDetachedFromVisualTree(e);
    }
}

/// <summary>Delete-key request payload.</summary>
public sealed class SheetDeleteEventArgs(object row, bool skipConfirm) : EventArgs
{
    public object Row { get; } = row;
    public bool SkipConfirm { get; } = skipConfirm;
}
