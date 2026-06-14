using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.Controls;

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

    // Mode panels (only one visible at a time).
    private readonly StackPanel _valuesPanel = new() { Spacing = 6 };
    private readonly StackPanel _conditionPanel = new() { Spacing = 6 };
    private readonly StackPanel _valuesList = new() { Spacing = 2 };
    private readonly List<CheckBox> _valueChecks = new();
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
            Mode = existing?.Mode ?? SheetFilterMode.Values,
            Condition = existing?.Condition ?? SheetFilterCondition.Contains,
            Text = existing?.Text ?? string.Empty,
            AllowedValues = existing?.AllowedValues is { } a ? new HashSet<string>(a) : null
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
    }

    public void Show(Control anchor) => _flyout.ShowAt(anchor);

    private Control BuildContent()
    {
        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(10), MinWidth = 240, MaxWidth = 320 };

        // Title: the column name.
        stack.Children.Add(new TextBlock
        {
            Text = _draft.Header,
            FontWeight = Avalonia.Media.FontWeight.SemiBold
        });

        // Mode radio toggles.
        var byValues = new RadioButton { Content = _loc.Get("Sheet.Filter.Mode.Values"), GroupName = "mode" };
        var byCondition = new RadioButton { Content = _loc.Get("Sheet.Filter.Mode.Condition"), GroupName = "mode" };
        byValues.IsChecked = _draft.Mode == SheetFilterMode.Values;
        byCondition.IsChecked = _draft.Mode == SheetFilterMode.Condition;
        byValues.IsCheckedChanged += (_, _) => { if (byValues.IsChecked == true) SetMode(SheetFilterMode.Values); };
        byCondition.IsCheckedChanged += (_, _) => { if (byCondition.IsChecked == true) SetMode(SheetFilterMode.Condition); };
        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        modeRow.Children.Add(byValues);
        modeRow.Children.Add(byCondition);
        stack.Children.Add(modeRow);

        BuildValuesPanel();
        BuildConditionPanel();
        stack.Children.Add(_valuesPanel);
        stack.Children.Add(_conditionPanel);

        // Action buttons.
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
        _valuesPanel.IsVisible = mode == SheetFilterMode.Values;
        _conditionPanel.IsVisible = mode == SheetFilterMode.Condition;
    }

    // ── Values mode ──
    private void BuildValuesPanel()
    {
        _valuesSearch = new TextBox { Watermark = _loc.Get("Sheet.Filter.Search") };
        _valuesSearch.GetObservable(TextBox.TextProperty).Subscribe(new AnonymousObserver(FilterValueList));

        var selectAll = new Button { Classes = { "ghost", "small" }, Content = _loc.Get("Sheet.Filter.SelectAll") };
        var clearAll = new Button { Classes = { "ghost", "small" }, Content = _loc.Get("Sheet.Filter.ClearValues") };
        selectAll.Click += (_, _) => SetAllValueChecks(true);
        clearAll.Click += (_, _) => SetAllValueChecks(false);
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        toggles.Children.Add(selectAll);
        toggles.Children.Add(clearAll);

        // Build a checkbox per distinct value; checked = kept. A null AllowedValues (no filter yet) means
        // everything is currently shown, so start all checked.
        var distinct = _table.DistinctValues(_column);
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

    private void FilterValueList(string? query)
    {
        var q = query?.Trim() ?? string.Empty;
        foreach (var check in _valueChecks)
        {
            var value = check.Tag as string ?? string.Empty;
            check.IsVisible = q.Length == 0
                || value.Contains(q, System.StringComparison.CurrentCultureIgnoreCase);
        }
    }

    private void SetAllValueChecks(bool isChecked)
    {
        foreach (var check in _valueChecks)
            if (check.IsVisible) // only the values currently shown by the search
                check.IsChecked = isChecked;
    }

    // ── Condition mode ──
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
            Watermark = _loc.Get("Sheet.Filter.Value"),
            Text = _draft.Text
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

    private void Apply()
    {
        if (_draft.Mode == SheetFilterMode.Values)
        {
            // Keep only the checked values. If every value is checked, treat it as "no filter" (null).
            var keep = new HashSet<string>(
                _valueChecks.Where(c => c.IsChecked == true).Select(c => c.Tag as string ?? string.Empty));
            _draft.AllowedValues = keep.Count == _valueChecks.Count ? null : keep;
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
