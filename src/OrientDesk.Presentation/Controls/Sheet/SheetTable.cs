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

    private IReadOnlyList<SheetBand> _bands = [];
    private RosterCellFactory? _cellFactory;
    private bool _editing;

    public SheetTable()
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
    private SheetColumn? _sortColumn;
    private bool _sortDescending;

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
    // the same SheetColumn.Width so header and rows stay aligned.
    private Control BuildRow()
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        var col = 0;
        foreach (var band in _bands)
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

        // A click anywhere in a cell selects that cell (Excel-style focused-cell outline). We focus the
        // SheetCell host itself, not the inner editor, so a single click selects and a second
        // click / Enter / typing begins editing. A click that lands directly on an interactive editor
        // (TextBox/ComboBox/date picker) is left alone so the user can place the caret in one click.
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
            return; // let the editor take the click (caret placement)
        cell.Focus();
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
        DeleteRequested?.Invoke(this, new SheetDeleteEventArgs(SelectedItem, e.KeyModifiers.HasFlag(KeyModifiers.Control)));
    }

    // Find the focused cell and put its editor into edit by focusing it. A text cell focuses its
    // TextBox; a combo cell opens its dropdown and focuses it so the user can pick with the keyboard
    // straight away (Enter on the open list commits the highlighted item).
    private bool BeginEditFocusedCell()
    {
        if (FindFocusedCell()?.Content is not Control content)
            return false;

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

        var items = new List<object?>();
        foreach (var item in _body.ItemsSource)
            items.Add(item);

        var index = items.IndexOf(_body.SelectedItem);
        var target = index + delta;
        if (target < 0 || target >= items.Count)
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
internal sealed class SheetCell : ContentControl
{
    public SheetCell(SheetColumn column, int columnIndex)
    {
        Column = column;
        ColumnIndex = columnIndex;
        Focusable = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
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
