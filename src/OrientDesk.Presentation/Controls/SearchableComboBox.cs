using System;
using System.Collections;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace OrientDesk.Presentation.Controls;

/// <summary>
/// A drop-in <see cref="ComboBox"/> that shows a search box at the top of its dropdown once the list
/// is long enough (more than <see cref="SearchThreshold"/> items), filtering the visible options as
/// the user types. Below the threshold it behaves exactly like a stock combo (no search row).
///
/// It keeps the stock Fluent template rather than re-templating: on first open it locates the popup
/// (<c>PART_Popup</c>), wraps its content in a <see cref="DockPanel"/> and docks a search
/// <see cref="TextBox"/> above it. Filtering swaps the base <see cref="ItemsControl.ItemsSource"/> to a
/// filtered copy of the caller's items, so all existing bindings, label templates and the table's
/// combo-driving logic keep working unchanged (it is still a <see cref="ComboBox"/>).
///
/// Keyboard model while the search box has focus: typing filters; ↓/↑ move a *visual* highlight over
/// the options WITHOUT changing the selection; Enter (or a mouse click) commits the highlighted/clicked
/// option and closes; Escape clears the query. Because the highlight is purely visual (a tinted item
/// background, not <see cref="SelectingItemsControl.SelectedItem"/>), merely browsing with the arrows
/// never changes the bound value — only Enter/click does. Focus stays on the search box throughout.
///
/// Item text for matching comes from <see cref="TextSelector"/> when set (the call site knows the
/// label property), otherwise from the item's <c>Label</c> property or <c>ToString()</c>.
/// </summary>
public sealed class SearchableComboBox : ComboBox
{
    // The Fluent theme keys its ComboBox ControlTheme (template, popup, etc.) to typeof(ComboBox).
    // A subclass would otherwise get no template and render blank, so resolve styling as a ComboBox.
    protected override Type StyleKeyOverride => typeof(ComboBox);

    /// <summary>Show the search row only when the option count exceeds this.</summary>
    public const int SearchThreshold = 5;

    /// <summary>Resolves the text used for filtering each item; defaults to Label/ToString.</summary>
    public Func<object?, string>? TextSelector { get; set; }

    /// <summary>Localized placeholder for the search box. Bindable so XAML combos can localize it.</summary>
    public static readonly StyledProperty<string> SearchWatermarkProperty =
        AvaloniaProperty.Register<SearchableComboBox, string>(nameof(SearchWatermark), "Search");

    public string SearchWatermark
    {
        get => GetValue(SearchWatermarkProperty);
        set => SetValue(SearchWatermarkProperty, value);
    }

    // The caller's unfiltered items. We bind ItemsSource to a filtered view of this; when no filter
    // is active the view is the source itself, so reference equality (e.g. SelectedItem matching) is
    // preserved exactly as the caller set up.
    private IList? _source;
    private TextBox? _searchBox;
    private INotifyCollectionChanged? _sourceIncc;
    private bool _suppressItemsChanged;

    // The purely-visual highlight: index into the current (filtered) item list, and the container we
    // tinted so we can clear it when the highlight moves. -1 = nothing highlighted.
    private int _highlightIndex = -1;
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x7C, 0x3A, 0xED));

    public SearchableComboBox()
    {
        DropDownOpened += OnDropDownOpenedHandler;
        DropDownClosed += OnDropDownClosedHandler;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        var popup = e.NameScope.Find<Popup>("PART_Popup");
        if (popup is not null)
            InjectSearchBox(popup);
    }

    // Track the caller's ItemsSource. Avalonia raises ItemsSourceProperty changes; we capture the new
    // value as our source and (re)apply the current filter. We guard against our own re-assignment.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty && !_suppressItemsChanged)
        {
            DetachSourceWatch();
            _source = AsList(change.GetNewValue<IEnumerable?>());
            AttachSourceWatch();
            ApplyFilter();
        }
    }

    private static IList? AsList(IEnumerable? items) => items switch
    {
        null => null,
        IList list => list,
        _ => items.Cast<object?>().ToList()
    };

    private void AttachSourceWatch()
    {
        if (_source is INotifyCollectionChanged incc)
        {
            _sourceIncc = incc;
            incc.CollectionChanged += OnSourceCollectionChanged;
        }
    }

    private void DetachSourceWatch()
    {
        if (_sourceIncc is not null)
        {
            _sourceIncc.CollectionChanged -= OnSourceCollectionChanged;
            _sourceIncc = null;
        }
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => ApplyFilter();

    private void InjectSearchBox(Popup popup)
    {
        // The popup's child is the dropdown surface (a Border wrapping the scrolled item list). Wrap
        // it once in a DockPanel and dock a search TextBox on top.
        if (popup.Child is not Control surface || surface is DockPanel)
            return;

        popup.Child = null; // detach before reparenting

        _searchBox = new TextBox
        {
            Margin = new Thickness(6, 6, 6, 0),
            MinHeight = 32,
            [DockPanel.DockProperty] = Dock.Top
        };
        // Keep the watermark in sync with the (possibly bound) SearchWatermark property.
        _searchBox.Bind(TextBox.PlaceholderTextProperty, this.GetObservable(SearchWatermarkProperty));
        _searchBox.TextChanged += (_, _) => ApplyFilter();

        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(_searchBox);
        dock.Children.Add(surface);
        popup.Child = dock;

        // Handle navigation keys in TUNNEL on the popup so we beat both the TextBox's own handling and
        // the ComboBox's OnKeyDown (which would otherwise move/commit the real selection).
        popup.Child.AddHandler(KeyDownEvent, OnPopupKeyDown, RoutingStrategies.Tunnel);

        UpdateSearchVisibility();
    }

    private void OnPopupKeyDown(object? sender, KeyEventArgs e)
    {
        Program.ActivityLog?.Info($"[combo] popup keydown: {e.Key}, searchFocused={_searchBox?.IsFocused}, items={ItemCount}, hl={_highlightIndex}");
        switch (e.Key)
        {
            case Key.Down:
                MoveHighlight(+1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveHighlight(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                CommitHighlight();
                e.Handled = true;
                break;
            case Key.Escape:
                if (!string.IsNullOrEmpty(_searchBox?.Text))
                {
                    _searchBox!.Text = string.Empty; // first Esc clears the query
                    e.Handled = true;
                }
                // a second Esc (empty query) is left for the combo to close the dropdown
                break;
        }
    }

    // Move the visual highlight by one within the filtered list. Does NOT touch SelectedItem.
    private void MoveHighlight(int delta)
    {
        var count = ItemCount;
        if (count == 0)
            return;

        int next;
        if (_highlightIndex < 0)
            next = delta > 0 ? 0 : count - 1;
        else
            next = Math.Clamp(_highlightIndex + delta, 0, count - 1);

        SetHighlight(next);
        if (ContainerFromIndex(next) is { } c)
            c.BringIntoView();
        RefocusSearch();
    }

    private void SetHighlight(int index)
    {
        // Clear the old container's tint.
        if (_highlightIndex >= 0 && ContainerFromIndex(_highlightIndex) is { } prev)
            prev.ClearValue(BackgroundProperty);

        _highlightIndex = index;

        if (index >= 0 && ContainerFromIndex(index) is { } cur)
            cur.SetValue(BackgroundProperty, HighlightBrush);
    }

    // Commit the highlighted option (or, if none highlighted, the lone match) as the real selection.
    private void CommitHighlight()
    {
        var index = _highlightIndex;
        if (index < 0 && ItemCount == 1)
            index = 0; // a single filtered result: Enter picks it even without arrowing
        if (index < 0 || index >= ItemCount)
        {
            IsDropDownOpen = false;
            return;
        }

        // Resolve the item at the highlighted position in the *current* (filtered) list and select it.
        var item = ItemFromIndex(index);
        if (item is not null)
            SelectedItem = item;
        IsDropDownOpen = false;
    }

    private object? ItemFromIndex(int index)
    {
        var i = 0;
        foreach (var item in (IEnumerable?)ItemsSource ?? Array.Empty<object>())
        {
            if (i++ == index)
                return item;
        }
        return null;
    }

    private void RefocusSearch()
    {
        if (_searchBox is { IsVisible: true } box && !box.IsFocused)
            Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Background);
    }

    private void OnDropDownOpenedHandler(object? sender, EventArgs e)
    {
        UpdateSearchVisibility();
        // Start with no highlight; the first ↓ lands on the top match.
        SetHighlight(-1);
        if (_searchBox is { IsVisible: true } box)
            Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Background);
    }

    private void OnDropDownClosedHandler(object? sender, EventArgs e)
    {
        SetHighlight(-1);
        // Clear the filter so the next open starts fresh and the full list is intact.
        if (_searchBox is not null && !string.IsNullOrEmpty(_searchBox.Text))
            _searchBox.Text = string.Empty;
    }

    private void UpdateSearchVisibility()
    {
        if (_searchBox is not null)
            _searchBox.IsVisible = (_source?.Count ?? 0) > SearchThreshold;
    }

    private void ApplyFilter()
    {
        UpdateSearchVisibility();

        var query = _searchBox?.Text;

        IEnumerable view;
        if (_source is null || string.IsNullOrWhiteSpace(query) || _source.Count <= SearchThreshold)
        {
            view = _source ?? (IEnumerable)Array.Empty<object>();
        }
        else
        {
            var q = query.Trim();
            view = _source.Cast<object?>()
                .Where(item => TextOf(item).Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        _suppressItemsChanged = true;
        try
        {
            // Only swap when the reference actually changes, to avoid needless rebuilds.
            if (!ReferenceEquals(ItemsSource, view))
                SetCurrentValue(ItemsSourceProperty, view);
        }
        finally
        {
            _suppressItemsChanged = false;
        }

        // Re-place the highlight on the first match while a query is active (containers were rebuilt),
        // so Enter has an obvious target. Defer so the new containers exist.
        if (!string.IsNullOrWhiteSpace(query) && ItemCount > 0)
            Dispatcher.UIThread.Post(() => SetHighlight(0), DispatcherPriority.Background);
        else
            _highlightIndex = -1;

        // Rebuilding the items list can pull focus off the search box — always pull it back.
        RefocusSearch();
    }

    private string TextOf(object? item)
    {
        if (item is null)
            return string.Empty;
        if (TextSelector is not null)
            return TextSelector(item) ?? string.Empty;
        var label = item.GetType().GetProperty("Label")?.GetValue(item) as string;
        return label ?? item.ToString() ?? string.Empty;
    }
}
