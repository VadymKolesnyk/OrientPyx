using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Controls;

/// <summary>
/// The per-column filter editor — a Google-Sheets-style dropdown with two modes: "by values"
/// (a searchable checkbox list of the column's distinct values) and "by condition" (a condition
/// dropdown + text box). Shown as a UiScale-wrapped <see cref="Flyout"/> (matching the header menu /
/// columns picker), so it renders correctly at any UI scale. Built imperatively, like the rest of
/// <c>Controls/Sheet/</c>; on Apply it writes back through <see cref="SheetTable.SetColumnFilter"/>.
/// </summary>
internal sealed class ColumnFilterPopup
{
    private readonly SheetTable _table;
    private readonly SheetColumn _column;
    private readonly ILocalizationService _loc;
    private readonly Flyout _flyout;

    // Working copy of the filter being edited (seeded from the existing one, if any).
    private readonly SheetFilter _draft;

    // Mode panels (only one shown at a time), all in one cell of _modeArea — see SetMode.
    private readonly Grid _modeArea = new();
    private readonly StackPanel _valuesPanel = new() { Spacing = 6 };
    private readonly StackPanel _conditionPanel = new() { Spacing = 6 };
    private readonly StackPanel _statusPanel = new() { Spacing = 6 };
    private readonly StackPanel _valuesList = new() { Spacing = 2 };
    private readonly List<CheckBox> _valueChecks = new();
    private readonly List<CheckBox> _statusChecks = new();
    // Every distinct value of the column, incl. those the other filters hide from the list.
    private HashSet<string> _allValues = new();
    private TextBox? _valuesSearch;
    private ComboBox? _conditionCombo;
    private TextBox? _conditionText;

    public ColumnFilterPopup(SheetTable table, SheetColumn column, ILocalizationService loc)
    {
        _table = table;
        _column = column;
        _loc = loc;

        var existing = table.GetColumnFilter(column.Key);
        _draft = new SheetFilter
        {
            ColumnKey = column.Key,
            Header = string.IsNullOrEmpty(column.PickerLabel) ? column.Header : column.PickerLabel,
            // A status column (payment) defaults to "by status" — its cell value is a status token, so
            // the generic "by values" list would show raw tokens. Other columns default to "by values".
            Mode = existing?.Mode ?? (column.StatusIsCellValue ? SheetFilterMode.Status : SheetFilterMode.Values),
            Condition = existing?.Condition ?? SheetFilterCondition.Contains,
            Text = existing?.Text ?? string.Empty,
            AllowedValues = existing?.AllowedValues is { } a ? new HashSet<string>(a) : null,
            AllowedStatuses = existing?.AllowedStatuses is { } s ? new HashSet<string>(s) : null
        };

        var root = BuildContent();
        _flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = new LayoutTransformControl
            {
                LayoutTransform = SheetColumnsButton.BuildUiScaleTransform(),
                Child = root
            }
        };
        // The popup never resizes while open (macOS shows a resized popup squashed — PopupSizeLock): the width
        // stays, and the mode area keeps its height while the search shortens the value list, so the buttons
        // under it don't move either.
        _flyout.Opened += (_, _) =>
        {
            PopupSizeLock.Pin(root, height: false);
            PopupSizeLock.Pin(_modeArea, width: false);
        };
    }

    public void Show(Control anchor) => _flyout.ShowAt(anchor);

    private Control BuildContent()
    {
        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(10), MinWidth = 240, MaxWidth = 320 };

        // Title: the column name, and «Інші дії ▾» on the right — the column menu (remove filter, bulk edit,
        // hide column) that a header right-click used to open before it opened this editor directly.
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var name = new TextBlock
        {
            Text = _draft.Header,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
        };
        var moreContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        moreContent.Children.Add(new TextBlock { Text = _loc.Get("Sheet.Filter.MoreActions"), VerticalAlignment = VerticalAlignment.Center });
        moreContent.Children.Add(new Icon { Kind = "ChevronDown", Size = 12, VerticalAlignment = VerticalAlignment.Center });
        var more = new Button
        {
            Classes = { "ghost", "small" },
            Content = moreContent,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        more.Click += (_, _) => { _flyout.Hide(); _table.ShowColumnMenu(_column); };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(more, 1);
        title.Children.Add(name);
        title.Children.Add(more);
        stack.Children.Add(title);

        // Mode radio toggles. A status column (payment) offers "by status" + "by condition"; the generic
        // "by values" list is hidden there because its cell value is a status token, not the visible text.
        var byCondition = new RadioButton { Content = _loc.Get("Sheet.Filter.Mode.Condition"), GroupName = "mode" };
        byCondition.IsChecked = _draft.Mode == SheetFilterMode.Condition;
        byCondition.IsCheckedChanged += (_, _) => { if (byCondition.IsChecked == true) SetMode(SheetFilterMode.Condition); };
        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };

        // "By values" is dropped only when the cell value IS the status token (payment); a column whose status
        // lives on its own path (the roster's «Номер» → «Мандатку пройдено») keeps all three modes.
        if (!_column.StatusIsCellValue)
        {
            var byValues = new RadioButton { Content = _loc.Get("Sheet.Filter.Mode.Values"), GroupName = "mode" };
            byValues.IsChecked = _draft.Mode == SheetFilterMode.Values;
            byValues.IsCheckedChanged += (_, _) => { if (byValues.IsChecked == true) SetMode(SheetFilterMode.Values); };
            modeRow.Children.Add(byValues);
        }
        modeRow.Children.Add(byCondition);
        if (_column.StatusFilter)
        {
            var byStatus = new RadioButton { Content = _loc.Get("Sheet.Filter.Mode.Status"), GroupName = "mode" };
            byStatus.IsChecked = _draft.Mode == SheetFilterMode.Status;
            byStatus.IsCheckedChanged += (_, _) => { if (byStatus.IsChecked == true) SetMode(SheetFilterMode.Status); };
            modeRow.Children.Add(byStatus);
        }
        stack.Children.Add(modeRow);

        BuildValuesPanel();
        BuildConditionPanel();
        if (_column.StatusFilter)
            BuildStatusPanel();
        _modeArea.Children.Add(_valuesPanel);
        _modeArea.Children.Add(_conditionPanel);
        _modeArea.Children.Add(_statusPanel);
        stack.Children.Add(_modeArea);

        var clear = new Button { Classes = { "ghost" }, Content = _loc.Get("Sheet.Filter.Clear") };
        var apply = new Button { Classes = { "accent" }, Content = _loc.Get("Sheet.Filter.Apply") };
        clear.Click += (_, _) => { _flyout.Hide(); _table.ClearColumnFilter(_column.Key); };
        apply.Click += (_, _) => { _flyout.Hide(); Apply(); };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        actions.Children.Add(clear);
        actions.Children.Add(apply);
        stack.Children.Add(actions);

        SetMode(_draft.Mode);
        return stack;
    }

    private void SetMode(SheetFilterMode mode)
    {
        _draft.Mode = mode;
        ShowPanel(_valuesPanel, mode == SheetFilterMode.Values);
        ShowPanel(_conditionPanel, mode == SheetFilterMode.Condition);
        ShowPanel(_statusPanel, mode == SheetFilterMode.Status);
    }

    // An inactive panel is see-through and inert but still laid out, so the mode area is as large as its largest
    // mode from the first layout on and a mode switch never resizes the popup.
    private static void ShowPanel(Control panel, bool active)
    {
        panel.Opacity = active ? 1 : 0;
        panel.IsHitTestVisible = active;
        panel.IsEnabled = active;
    }

    // ── Values mode
    private void BuildValuesPanel()
    {
        _valuesSearch = new TextBox { PlaceholderText = _loc.Get("Sheet.Filter.Search") };
        _valuesSearch.GetObservable(TextBox.TextProperty).Subscribe(new AnonymousObserver(FilterValueList));
        // Escape clears the search text (or, when already empty, closes the flyout) — and must be marked
        // handled either way, else the TextBox types it as the U+241B (␛) control glyph.
        _valuesSearch.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Escape)
                return;
            e.Handled = true;
            if (string.IsNullOrEmpty(_valuesSearch.Text))
                _flyout.Hide();
            else
                _valuesSearch.Text = string.Empty;
        };

        var selectAll = new Button { Classes = { "ghost", "small" }, Content = _loc.Get("Sheet.Filter.SelectAll") };
        var clearAll = new Button { Classes = { "ghost", "small" }, Content = _loc.Get("Sheet.Filter.ClearValues") };
        selectAll.Click += (_, _) => SetAllValueChecks(true);
        clearAll.Click += (_, _) => SetAllValueChecks(false);
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        toggles.Children.Add(selectAll);
        toggles.Children.Add(clearAll);

        // Build a checkbox per distinct value of the rows the OTHER filters (and the search) leave — filtered
        // by «Представник», the «Прізвище» list holds only that representative's runners; checked = kept.
        // A null AllowedValues (no filter yet) means everything is currently shown, so start all checked.
        _allValues = new HashSet<string>(_table.DistinctValues(_column));
        var distinct = _table.DistinctValues(_column, passingOtherFilters: true);
        var allowed = _draft.AllowedValues;
        foreach (var value in distinct)
        {
            var check = new CheckBox
            {
                Content = string.IsNullOrEmpty(value) ? _loc.Get("Sheet.Filter.Empty") : value,
                Tag = value,
                IsChecked = allowed is null || allowed.Contains(value),
                Padding = new Thickness(6, 2),
                MinHeight = 0
            };
            _valueChecks.Add(check);
            _valuesList.Children.Add(check);
        }

        _valuesPanel.Children.Add(_valuesSearch);
        _valuesPanel.Children.Add(toggles);
        _valuesPanel.Children.Add(new ScrollViewer { MaxHeight = 260, Content = _valuesList });
    }

    // Layout-tolerant like the table's own search: «Rjktc» finds «Колес» (wrong keyboard layout), s/i→ы and
    // Latin/Cyrillic look-alikes too — the term is expanded once into TextSearch variants.
    private void FilterValueList(string? query)
    {
        var variants = TextSearch.Variants(query);
        foreach (var check in _valueChecks)
        {
            var value = check.Tag as string ?? string.Empty;
            check.IsVisible = variants.Count == 0 || TextSearch.ContainsAny(value, variants);
        }
    }

    private void SetAllValueChecks(bool isChecked)
    {
        foreach (var check in _valueChecks)
            if (check.IsVisible) // only the values currently shown by the search
                check.IsChecked = isChecked;
    }

    // ── Condition mode
    private void BuildConditionPanel()
    {
        _conditionCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = SheetFilter.AllConditions,
            SelectedItem = _draft.Condition,
            ItemTemplate = new FuncDataTemplate<SheetFilterCondition>((c, _) =>
                new TextBlock { Text = _loc.Get(SheetFilter.ConditionKey(c)) })
        };
        _conditionText = new TextBox
        {
            PlaceholderText = _loc.Get("Sheet.Filter.Value"),
            Text = _draft.Text
        };
        // Escape closes the flyout — swallow it so the box never types the U+241B (␛) control glyph.
        _conditionText.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Escape)
                return;
            e.Handled = true;
            _flyout.Hide();
        };

        // The text box is irrelevant for the empty/not-empty conditions; hide it for them.
        void SyncTextVisibility()
        {
            var c = _conditionCombo!.SelectedItem as SheetFilterCondition?;
            _conditionText!.IsVisible = c is not (SheetFilterCondition.IsEmpty or SheetFilterCondition.IsNotEmpty);
        }
        _conditionCombo.SelectionChanged += (_, _) => SyncTextVisibility();

        _conditionPanel.Children.Add(_conditionCombo);
        _conditionPanel.Children.Add(_conditionText);
        SyncTextVisibility();
    }

    // ── Status mode (payment column, roster «Номер»)
    private void BuildStatusPanel()
    {
        // One checkbox per status category (the column's own list, else the payment statuses); checked =
        // kept. A null AllowedStatuses (no filter yet) means every status is currently shown, so start all checked.
        var allowed = _draft.AllowedStatuses;
        var options = _column.StatusOptions
            ?? PaymentStatusExtensions.All.Select(s => (Token: s.ToString(), LabelKey: PaymentStatusExtensions.LabelKey(s))).ToList();
        foreach (var (token, labelKey) in options)
        {
            var check = new CheckBox
            {
                Content = _loc.Get(labelKey),
                Tag = token,
                IsChecked = allowed is null || allowed.Contains(token),
                Padding = new Thickness(6, 2),
                MinHeight = 0
            };
            _statusChecks.Add(check);
            _statusPanel.Children.Add(check);
        }
    }

    private void Apply()
    {
        if (_draft.Mode == SheetFilterMode.Values)
        {
            // The checked values are kept; a value the list didn't show (another filter hides its rows) keeps
            // the state it had. All values kept ⇒ "no filter" (null).
            _draft.ApplyPickedValues(
                _valueChecks.Select(c => c.Tag as string ?? string.Empty),
                _valueChecks.Where(c => c.IsChecked == true).Select(c => c.Tag as string ?? string.Empty),
                _allValues);
        }
        else if (_draft.Mode == SheetFilterMode.Status)
        {
            // Keep only the checked statuses. All checked ⇒ "no filter" (null).
            var keep = new HashSet<string>(
                _statusChecks.Where(c => c.IsChecked == true).Select(c => c.Tag as string ?? string.Empty));
            _draft.AllowedStatuses = keep.Count == _statusChecks.Count ? null : keep;
        }
        else
        {
            _draft.Condition = _conditionCombo?.SelectedItem as SheetFilterCondition? ?? SheetFilterCondition.Contains;
            _draft.Text = _conditionText?.Text ?? string.Empty;
        }

        _table.SetColumnFilter(_column.Key, _draft);
    }

    // Minimal IObserver<string?> so we can react to the search box text without a binding.
    private sealed class AnonymousObserver(System.Action<string?> onNext) : System.IObserver<string?>
    {
        public void OnCompleted() { }
        public void OnError(System.Exception error) { }
        public void OnNext(string? value) => onNext(value);
    }
}
