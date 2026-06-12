using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.Localization;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Controls;

/// <summary>
/// A purpose-built table for the participant roster ("Мандатка") that the Avalonia DataGrid cannot
/// express: a true two-tier (banded) header where per-day field columns sit under a spanning band
/// label. Rows are virtualized by an inner <see cref="ListBox"/>; the header is frozen vertically
/// and scrolls horizontally in lockstep with the body via one shared outer scroller.
///
/// Built imperatively from a <see cref="RosterColumn"/>/<see cref="RosterBand"/> model so columns
/// can be rebuilt on collapse/expand and language change. Excel-style selection + focus-to-edit and
/// Delete-with-confirmation match the rest of the app's tables (see <c>SheetDataGrid</c>).
/// </summary>
public sealed class RosterTable : TemplatedControl
{
    // ── Bindable properties ───────────────────────────────────────────────────────────────────────
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<RosterTable, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<ILocalizationService?> LocalizationProperty =
        AvaloniaProperty.Register<RosterTable, ILocalizationService?>(nameof(Localization));

    public static readonly StyledProperty<IReadOnlyList<EventDay>?> DaysProperty =
        AvaloniaProperty.Register<RosterTable, IReadOnlyList<EventDay>?>(nameof(Days));

    public static readonly StyledProperty<IEnumerable<RosterFieldBlockViewModel>?> BlocksProperty =
        AvaloniaProperty.Register<RosterTable, IEnumerable<RosterFieldBlockViewModel>?>(nameof(Blocks));

    public static readonly StyledProperty<ICommand?> ToggleBlockCommandProperty =
        AvaloniaProperty.Register<RosterTable, ICommand?>(nameof(ToggleBlockCommand));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<RosterTable, object?>(nameof(SelectedItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<ICommand?> DeleteCommandProperty =
        AvaloniaProperty.Register<RosterTable, ICommand?>(nameof(DeleteCommand));

    /// <summary>The shared rental-chip set chip cells highlight against (bold-red when not rental).</summary>
    public static readonly StyledProperty<RentalChipRegistry?> RentalChipsProperty =
        AvaloniaProperty.Register<RosterTable, RentalChipRegistry?>(nameof(RentalChips));

    /// <summary>Command invoked (with the chip number) when a chip cell is double-clicked, to toggle it in the rental DB.</summary>
    public static readonly StyledProperty<ICommand?> ToggleRentalChipCommandProperty =
        AvaloniaProperty.Register<RosterTable, ICommand?>(nameof(ToggleRentalChipCommand));

    /// <summary>
    /// Pre-built bands supplied by the caller. When set, the table renders these verbatim and does
    /// NOT build columns from <see cref="Days"/>/<see cref="Blocks"/> — this is how the flat day-mode
    /// table reuses the control without the roster's per-day banding.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<RosterBand>?> BandsProperty =
        AvaloniaProperty.Register<RosterTable, IReadOnlyList<RosterBand>?>(nameof(Bands));

    /// <summary>Raised when the user asks to delete a row via the keyboard; arg = skip-confirm.</summary>
    public event EventHandler<RosterDeleteEventArgs>? DeleteRequested;

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
    public IReadOnlyList<RosterBand>? Bands
    {
        get => GetValue(BandsProperty);
        set => SetValue(BandsProperty, value);
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
    private RosterHeaderPanel? _header;
    private ListBox? _body;
    private ScrollViewer? _headerScroll;
    private ScrollViewer? _bodyScroll;

    private IReadOnlyList<RosterBand> _bands = [];
    private RosterCellFactory? _cellFactory;
    private bool _editing;

    public RosterTable()
    {
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnBubbleKeyDown, RoutingStrategies.Bubble);
        AddHandler(PointerPressedEvent, OnTunnelPointerPressed, RoutingStrategies.Tunnel);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Inputs that shape the columns ⇒ rebuild; the items source/selection are pushed to the body.
        if (change.Property == DaysProperty || change.Property == BlocksProperty ||
            change.Property == LocalizationProperty || change.Property == ToggleBlockCommandProperty ||
            change.Property == BandsProperty || change.Property == RentalChipsProperty ||
            change.Property == ToggleRentalChipCommandProperty)
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
            _header = new RosterHeaderPanel(Localization);
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

        _cellFactory = new RosterCellFactory(Localization, RequestDelete, RentalChips, ToggleRental);

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
            _bands = builder.Build(Days, AsList(Blocks), _bands);
        }

        // Apply any user reorder (drag), keyed by a stable band signature so it survives rebuilds.
        _bands = ApplyBandOrder(_bands);

        if (_header is not null)
        {
            _header.ToggleBlock = ToggleBlockCommand;
            _header.SortBy = ApplySort;
            _header.MoveBand = MoveBand;
            _header.SortColumn = _sortColumn;
            _header.SortDescending = _sortDescending;
            _header.Rebuild(_bands);
        }

        // (Re)stamp rows so their cell hosts pick up the current column set. Recycling is off so a
        // rebuild (collapse/expand, language change) regenerates the per-row grids cleanly.
        if (_body is not null)
            _body.ItemTemplate = new FuncDataTemplate<object>((_, _) => BuildRow(), supportsRecycling: false);

        ApplySortedView();
    }

    // ── Band reorder (drag) ─────────────────────────────────────────────────────────────────────
    // The desired top-level band order, as stable signatures. Null until the user reorders.
    private List<string>? _bandOrder;
    private RosterColumn? _sortColumn;
    private bool _sortDescending;

    private static string Signature(RosterBand band)
    {
        // Field blocks are identified by their block reference; identity/action bands by first kind+header.
        if (band.Block is not null)
            return "block:" + band.Block.Field;
        return "id:" + band.Columns[0].Kind + ":" + band.Header;
    }

    private IReadOnlyList<RosterBand> ApplyBandOrder(IReadOnlyList<RosterBand> bands)
    {
        if (_bandOrder is null)
            return bands;

        var bySig = new Dictionary<string, RosterBand>();
        foreach (var b in bands)
            bySig[Signature(b)] = b;

        var ordered = new List<RosterBand>(bands.Count);
        foreach (var sig in _bandOrder)
            if (bySig.Remove(sig, out var b))
                ordered.Add(b);
        // Any new bands not in the saved order (e.g. a day was added) go to the end in build order.
        foreach (var b in bands)
            if (bySig.ContainsValue(b))
                ordered.Add(b);
        return ordered.Count == bands.Count ? ordered : bands;
    }

    private void MoveBand(int from, int to)
    {
        if (from < 0 || from >= _bands.Count || to < 0 || to >= _bands.Count || from == to)
            return;

        var order = new List<string>(_bands.Count);
        foreach (var b in _bands)
            order.Add(Signature(b));

        var moved = order[from];
        order.RemoveAt(from);
        order.Insert(to, moved);
        _bandOrder = order;
        Rebuild();
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
    private void ApplySort(RosterColumn column)
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
            _header.Rebuild(_bands); // refresh arrow indicators
        }
        ApplySortedView();
    }

    private void ApplySortedView()
    {
        if (_body is null)
            return;

        if (_sortColumn is null || string.IsNullOrEmpty(_sortColumn.SortPath) || ItemsSource is null)
        {
            _body.ItemsSource = ItemsSource;
            return;
        }

        var items = new List<object>();
        foreach (var item in ItemsSource)
            if (item is not null)
                items.Add(item);

        var path = _sortColumn.SortPath;
        items.Sort((a, b) => CompareByPath(a, b, path));
        if (_sortDescending)
            items.Reverse();

        _body.ItemsSource = items;
    }

    private static int CompareByPath(object a, object b, string path)
    {
        var va = ReadPath(a, path);
        var vb = ReadPath(b, path);
        if (va is null && vb is null) return 0;
        if (va is null) return -1;
        if (vb is null) return 1;
        if (va is IComparable ca && va.GetType() == vb.GetType())
            return ca.CompareTo(vb);
        return string.Compare(va.ToString(), vb.ToString(), StringComparison.CurrentCultureIgnoreCase);
    }

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

            var prop = current.GetType().GetProperty(segment);
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
    // the same RosterColumn.Width so header and rows stay aligned.
    private Control BuildRow()
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        var col = 0;
        foreach (var band in _bands)
        {
            foreach (var column in band.Columns)
            {
                var def = new ColumnDefinition { MinWidth = column.MinWidth };
                def[!ColumnDefinition.WidthProperty] = new Avalonia.Data.Binding(nameof(RosterColumn.Width))
                {
                    Source = column,
                    Converter = PixelToGridLength.Instance
                };
                grid.ColumnDefinitions.Add(def);

                var cell = new RosterCell(column) { Content = _cellFactory!.Build(column) };
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
    private sealed class OffsetSync(RosterTable owner) : IObserver<Vector>
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

    private void OnTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
        => _ctrlDown = e.KeyModifiers.HasFlag(KeyModifiers.Control);

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        // The focused element decides whether we're already editing (a focused TextBox in a cell).
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        _editing = focused is TextBox;

        if (_editing)
            return;

        var ctrlOrAlt = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (BeginEditFocusedCell())
            {
                e.Handled = true;
                Dispatcher.UIThread.Post(PasteIntoEditor, DispatcherPriority.Background);
            }
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
        DeleteRequested?.Invoke(this, new RosterDeleteEventArgs(SelectedItem, e.KeyModifiers.HasFlag(KeyModifiers.Control)));
    }

    // Find the focused cell and put its inner TextBox (if any) into edit by focusing it.
    private bool BeginEditFocusedCell()
    {
        if (FindFocusedCell()?.Content is not Control content)
            return false;

        var box = content as TextBox ?? FindDescendantTextBox(content);
        if (box is null)
            return false;

        box.Focus();
        return true;
    }

    private RosterCell? FindFocusedCell()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        while (focused is not null)
        {
            if (focused is RosterCell cell)
                return cell;
            focused = focused.GetVisualParent();
        }
        return null;
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

    private async void PasteIntoEditor()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || FocusedEditor() is not { } box)
            return;
        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
        {
            box.Text = text;
            box.CaretIndex = text.Length;
        }
    }

    private TextBox? FocusedEditor() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as TextBox;
}

/// <summary>A focusable cell host so the table can select cells and start editing on keystrokes.</summary>
internal sealed class RosterCell : ContentControl
{
    public RosterCell(RosterColumn column)
    {
        Column = column;
        Focusable = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    public RosterColumn Column { get; }
}

/// <summary>Delete-key request payload.</summary>
public sealed class RosterDeleteEventArgs(object row, bool skipConfirm) : EventArgs
{
    public object Row { get; } = row;
    public bool SkipConfirm { get; } = skipConfirm;
}
