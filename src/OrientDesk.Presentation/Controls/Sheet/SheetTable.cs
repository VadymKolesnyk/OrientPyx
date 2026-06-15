using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
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
using Avalonia.Threading;
using Avalonia.VisualTree;
using OrientDesk.BusinessLogic.Entities;
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

    /// <summary>Raised when the user asks to delete a row via the keyboard; arg = skip-confirm.</summary>
    public event EventHandler<SheetDeleteEventArgs>? DeleteRequested;

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

    // ── Template parts ────────────────────────────────────────────────────────────────────────────
    private SheetHeaderPanel? _header;
    private ListBox? _body;
    private ScrollViewer? _headerScroll;
    private ScrollViewer? _bodyScroll;

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
            change.Property == DiscountsProperty || change.Property == RaisedFeeEnabledProperty)
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

        _body = e.NameScope.Find<ListBox>("PART_Body");
        if (_body is not null)
        {
            HookItemsSource();
            _body.ItemsSource = ItemsSource;
            _body.SelectedItem = SelectedItem;
            _body.SelectionChanged += (_, _) => SelectedItem = _body.SelectedItem;
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
            _bands = builder.Build(Days, AsList(Blocks), Discounts ?? [], RaisedFeeEnabled, _bands);
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
            _header.SortBy = ApplySort;
            _header.MoveBand = MoveBand;
            _header.HideColumn = HideColumn;
            _header.ColumnResized = PersistLayout;
            _header.FilterColumn = ShowColumnFilter;
            _header.RemoveFilter = column => ClearColumnFilter(column.Key);
            _header.HasFilter = column => _filters.ContainsKey(column.Key);
            _header.SortColumn = _sortColumn;
            _header.SortDescending = _sortDescending;
            _header.Rebuild(_visibleBands);
        }

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
        _filters[key] = filter;
        ApplySortedView();
        FiltersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a column's filter (no-op if none). Rebuilds the body view.</summary>
    public void ClearColumnFilter(string key)
    {
        if (_filters.Remove(key))
        {
            ApplySortedView();
            FiltersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Removes every active filter.</summary>
    public void ClearAllFilters()
    {
        if (_filters.Count == 0)
            return;
        _filters.Clear();
        ApplySortedView();
        FiltersChanged?.Invoke(this, EventArgs.Empty);
    }

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
    {
        var value = ReadPath(row, column.FilterPath);
        return value switch
        {
            null => string.Empty,
            DateTimeOffset dto => dto.ToString("dd.MM.yyyy"),
            DateTime dt => dt.ToString("dd.MM.yyyy"),
            _ => value.ToString() ?? string.Empty
        };
    }

    // True when a row passes every active filter (an absent/now-hidden column's filter is ignored).
    private bool PassesFilters(object? row)
    {
        if (row is null || _filters.Count == 0)
            return true;
        foreach (var filter in _filters.Values)
        {
            var col = FindColumnByKey(filter.ColumnKey);
            if (col is null)
                continue;
            if (!filter.Matches(CellText(col, row)))
                return false;
        }
        return true;
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
        foreach (var band in _bands)
            foreach (var col in band.Columns)
                if (col.Kind != SheetCellKind.Actions && !string.IsNullOrEmpty(col.PickerLabel))
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
        }
    }

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
    private SheetColumn? _sortColumn;
    private bool _sortDescending;

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
    // Click cycles: unsorted → ascending → descending → ascending… on the same column; a new column
    // starts ascending. Sorting reorders a display copy of the items; the source collection is left
    // untouched so the VM's selection/delete keep working by reference.
    private void ApplySort(SheetColumn column)
    {
        if (string.IsNullOrEmpty(column.SortPath))
            return;

        if (_sortColumn == column)
            _sortDescending = !_sortDescending;
        else
        {
            _sortColumn = column;
            _sortDescending = false;
        }

        if (_header is not null)
        {
            _header.SortColumn = _sortColumn;
            _header.SortDescending = _sortDescending;
            _header.Rebuild(_visibleBands); // refresh arrow indicators
        }
        ApplySortedView();
    }

    // The body's current item order (sorted view, or the source order when unsorted). Cached so row
    // navigation (MoveRow) doesn't re-materialise and re-scan the whole collection on every keystroke.
    private IReadOnlyList<object?> _sortedItems = Array.Empty<object?>();

    private void ApplySortedView()
    {
        if (_body is null)
            return;

        var hasFilters = _filters.Count > 0;

        if (_sortColumn is null || string.IsNullOrEmpty(_sortColumn.SortPath) || ItemsSource is null)
        {
            // Unsorted: when nothing is filtered, bind the live source directly (cheapest path — keeps
            // optimistic row add/delete reflecting instantly). Otherwise bind a filtered display copy.
            if (!hasFilters)
            {
                var unsorted = new List<object?>();
                foreach (var item in ItemsSource ?? Array.Empty<object?>())
                    unsorted.Add(item);
                _sortedItems = unsorted;
                _body.ItemsSource = ItemsSource;
                return;
            }

            var filtered = new List<object?>();
            foreach (var item in ItemsSource!)
                if (PassesFilters(item))
                    filtered.Add(item);
            _sortedItems = filtered;
            _body.ItemsSource = filtered;
            return;
        }

        var path = _sortColumn.SortPath;

        // Schwartzian transform: read each item's sort key once (reflection is the costly part), then
        // sort the (key, item) pairs. Avoids re-reading both operands on every comparison. Filtered rows
        // are dropped before keying so we don't sort what we won't show.
        var keyed = new List<(object? Key, object Item)>();
        foreach (var item in ItemsSource)
            if (item is not null && PassesFilters(item))
                keyed.Add((ReadPath(item, path), item));

        keyed.Sort((a, b) => CompareKeys(a.Key, b.Key));
        if (_sortDescending)
            keyed.Reverse();

        var items = new List<object?>(keyed.Count);
        foreach (var (_, item) in keyed)
            items.Add(item);

        _sortedItems = items;
        _body.ItemsSource = items;
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
                Grid.SetColumn(cell, col);
                grid.Children.Add(cell);
                col++;
            }
        }
        return grid;
    }

    // Keep the frozen header lined up with the body's horizontal scroll position.
    private void SyncHeaderOffset(Vector bodyOffset)
    {
        if (_headerScroll is not null)
            _headerScroll.Offset = new Vector(bodyOffset.X, 0);
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
    private int _focusedColumn;

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

        _focusedColumn = cell.ColumnIndex;

        // Select the clicked cell's row. The ListBox would normally do this itself on bubble, but a
        // click that lands in an editor (and focuses it / begins editing) never reaches that path, so
        // the row would stay unselected. Set it explicitly from the row's data context.
        if (RowItemFromCell(cell) is { } row)
            SelectedItem = row;

        if (e.Source is Visual src && IsInsideEditor(src, cell))
            return; // let the live editor take the click (caret placement)

        // A resting lazy cell: focus the SheetCell for the outline, then ask the cell to begin editing.
        if (FindLazyCell(cell.Content as Control) is { } lazy)
        {
            cell.Focus();
            _editingLazy = lazy;
            lazy.BeginEdit(open: true);
            return;
        }

        cell.Focus();
    }

    // Right-click on a body cell offers "filter by this value" (a Values filter pinned to that cell's
    // text) and, when the column already has a filter, "clear filter". Left untouched: left click and
    // the focus-to-edit flow. Skips non-filterable columns (actions / no value path).
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
        var byValue = new Button
        {
            Classes = { "ghost" },
            Content = Localization.Get("Sheet.Filter.ByValue"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6)
        };
        menu.Children.Add(byValue);

        var hasFilter = _filters.ContainsKey(column.Key);
        Button? clear = null;
        if (hasFilter)
        {
            clear = new Button
            {
                Classes = { "ghost" },
                Content = Localization.Get("Sheet.Filter.Remove"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 6)
            };
            menu.Children.Add(clear);
        }

        // Column-specific extra: a chip column adds a "mark (non-)rental" item, labelled for the chip's
        // current state. Filter-by-value stays the default for every cell; this is the per-column add-on.
        Button? rental = null;
        var chip = column.RentalChipColumn ? value.Trim() : string.Empty;
        if (chip.Length > 0 && ToggleRentalChipCommand?.CanExecute(chip) == true)
        {
            var isRental = RentalChips?.Contains(chip) == true;
            rental = new Button
            {
                Classes = { "ghost" },
                Content = Localization.Get(isRental ? "Participants.Chip.UnmarkRental" : "Participants.Chip.MarkRental"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 6)
            };
            menu.Children.Add(rental);
        }

        var flyout = new Flyout
        {
            Placement = PlacementMode.Pointer,
            Content = new LayoutTransformControl
            {
                LayoutTransform = SheetColumnsButton.BuildUiScaleTransform(),
                Child = menu
            }
        };
        byValue.Click += (_, _) =>
        {
            flyout.Hide();
            SetColumnFilter(column.Key, new SheetFilter
            {
                ColumnKey = column.Key,
                Header = string.IsNullOrEmpty(column.PickerLabel) ? column.Header : column.PickerLabel,
                Mode = SheetFilterMode.Values,
                AllowedValues = new HashSet<string> { value }
            });
        };
        if (clear is not null)
            clear.Click += (_, _) => { flyout.Hide(); ClearColumnFilter(column.Key); };
        if (rental is not null)
            rental.Click += (_, _) => { flyout.Hide(); ToggleRental(chip); };
        flyout.ShowAt(cell);
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
                _focusedColumn = target;
                cell.Focus();
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
        if (FindFocusedCell() is not { } cell)
            return false;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return false;

        var text = ExtractCellText(cell.Content as Control);
        if (string.IsNullOrEmpty(text))
            return false;

        _ = clipboard.SetTextAsync(text);
        return true;
    }

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
        }

        // Composite cells (per-day chip Panel with backdrop+editor, collapsed merged cells): pick the
        // first visible descendant that directly carries text.
        foreach (var d in content.GetVisualDescendants())
        {
            if (d is not Control c || !c.IsVisible)
                continue;
            var text = c switch
            {
                TextBox box => box.Text,
                TextBlock block => block.Text,
                ComboBox combo => combo.SelectedItem is GroupOption option ? option.Label : combo.SelectionBoxItem?.ToString(),
                _ => null
            };
            if (!string.IsNullOrEmpty(text))
                return text;
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
        // A transparent (not null) background makes the cell's whole area — including padding around a
        // short label — hit-testable, so a click anywhere in the cell, not just on its content, reaches
        // OnTunnelPointerPressed and begins editing.
        Background = Brushes.Transparent;
    }

    public SheetColumn Column { get; }

    /// <summary>This cell's leaf-column index across the whole row, for up/down navigation.</summary>
    public int ColumnIndex { get; }
}

/// <summary>Delete-key request payload.</summary>
public sealed class SheetDeleteEventArgs(object row, bool skipConfirm) : EventArgs
{
    public object Row { get; } = row;
    public bool SkipConfirm { get; } = skipConfirm;
}
