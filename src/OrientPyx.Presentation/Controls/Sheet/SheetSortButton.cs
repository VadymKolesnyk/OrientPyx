using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.Controls;

/// <summary>
/// A toolbar button that opens a small editor for a <see cref="SheetTable"/>'s multi-column sort:
/// an ordered list of "sort by … then by …" rows, each a column picker + ascending/descending toggle,
/// with add / remove / reorder. Bind <see cref="Table"/> and <see cref="Localization"/> like
/// <see cref="SheetColumnsButton"/>. The rows are rebuilt from the table's live sort each time the
/// flyout opens (and when the table's sort changes), and Apply pushes them back via
/// <see cref="SheetTable.SetSortLevels"/>.
/// </summary>
public sealed class SheetSortButton : Button
{
    public static readonly StyledProperty<SheetTable?> TableProperty =
        AvaloniaProperty.Register<SheetSortButton, SheetTable?>(nameof(Table));

    public static readonly StyledProperty<ILocalizationService?> LocalizationProperty =
        AvaloniaProperty.Register<SheetSortButton, ILocalizationService?>(nameof(Localization));

    public SheetTable? Table
    {
        get => GetValue(TableProperty);
        set => SetValue(TableProperty, value);
    }

    public ILocalizationService? Localization
    {
        get => GetValue(LocalizationProperty);
        set => SetValue(LocalizationProperty, value);
    }

    private readonly StackPanel _rows;
    private readonly Flyout _flyout;

    // The working set the editor mutates while open; committed to the table on Apply.
    private readonly List<(string Key, bool Descending)> _working = new();

    public SheetSortButton()
    {
        Classes.Add("ghost");

        // Icon-only button: an "A→Z with arrows" style sort glyph (two bars + a down arrow).
        var icon = new PathIcon
        {
            Data = Geometry.Parse(
                // three descending-length bars (a sort list) + a down arrow on the right
                "M3,4 h10 v2 h-10 z M3,9 h7 v2 h-7 z M3,14 h4 v2 h-4 z " +
                "M17,3 h2 v11 h3 l-4,5 l-4,-5 h3 z"),
            Width = 16,
            Height = 16
        };
        Content = icon;

        _rows = new StackPanel { Spacing = 6 };

        _footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        // The rows scroll vertically when there are many levels; the footer stays pinned below. Neither
        // ever scrolls horizontally — the whole panel is fixed-width to fit its widest row exactly.
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(8), Width = RowWidth + 16 };
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 360,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _rows
        });
        panel.Children.Add(_footer);

        // The flyout renders in its own top-level popup outside the window's root transform; wrap in the
        // shared UiScale transform so it matches the app scale (see SheetColumnsButton / PopupScaling).
        // A FlyoutPresenter caps content width by default; widen it so the fixed panel isn't clipped.
        var scaled = new LayoutTransformControl
        {
            LayoutTransform = SheetColumnsButton.BuildUiScaleTransform(),
            Child = panel
        };
        _flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            Content = scaled,
            FlyoutPresenterClasses = { "sortFlyout" }
        };
        Flyout = _flyout;

        _flyout.Opened += (_, _) => { SeedFromTable(); Rebuild(); };
        ApplyCaption();
    }

    // The exact width of a row grid (see BuildRow's column definitions), so the panel fits it with no
    // horizontal scroll. Kept in one place so the two stay in sync.
    private const double RowWidth = 78 + 174 + 120 + 30 + 30 + 30;

    private readonly StackPanel _footer;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TableProperty)
        {
            if (change.OldValue is SheetTable oldTable)
                oldTable.SortChanged -= OnTableSortChanged;
            if (change.NewValue is SheetTable newTable)
                newTable.SortChanged += OnTableSortChanged;
        }
        else if (change.Property == LocalizationProperty)
        {
            ApplyCaption();
        }
    }

    // Keep the open editor in sync if the sort changes elsewhere (e.g. a header click while it's open).
    private void OnTableSortChanged(object? sender, EventArgs e)
    {
        if (_flyout.IsOpen)
        {
            SeedFromTable();
            Rebuild();
        }
    }

    private void ApplyCaption()
        => ToolTip.SetTip(this, Localization?.Get("Sheet.Sort.Button") ?? "Sort");

    // Load the table's current sort into the working set.
    private void SeedFromTable()
    {
        _working.Clear();
        if (Table is not { } table)
            return;
        foreach (var level in table.SortLevels)
            _working.Add((level.Column.Key, level.Descending));
    }

    // (Re)builds the editor rows + footer from the working set and the table's sortable columns.
    private void Rebuild()
    {
        _rows.Children.Clear();
        _footer.Children.Clear();
        if (Table is not { } table || Localization is not { } loc)
            return;

        var columns = table.SortableColumns();
        if (columns.Count == 0)
            return;

        if (_working.Count == 0)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = loc.Get("Sheet.Sort.Empty"),
                Foreground = (IBrush?)this.FindResource("TextSecondary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 2, 2, 6)
            });
        }

        for (var i = 0; i < _working.Count; i++)
            _rows.Children.Add(BuildRow(i, columns, loc));

        // "+ Add level" — enabled while there is at least one column not yet used. Left-margin aligns it
        // under the column picker (past the fixed "Спочатку по/Потім по" prefix track).
        var canAdd = _working.Count < columns.Count;
        var add = new Button
        {
            Classes = { "ghost", "small" },
            Content = loc.Get("Sheet.Sort.AddLevel"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(78, 0, 0, 0),
            IsEnabled = canAdd
        };
        add.Click += (_, _) =>
        {
            var next = FirstUnused(columns);
            if (next is not null)
            {
                _working.Add((next.Key, false));
                Rebuild();
            }
        };
        _rows.Children.Add(add);

        // Footer: Clear (all) on the left of Apply.
        var clear = new Button { Classes = { "ghost" }, Content = loc.Get("Sheet.Sort.Clear") };
        clear.Click += (_, _) => { _working.Clear(); table.ClearSort(); _flyout.Hide(); };
        var apply = new Button { Classes = { "accent" }, Content = loc.Get("Sheet.Sort.Apply") };
        apply.Click += (_, _) => { table.SetSortLevels(new List<(string, bool)>(_working)); _flyout.Hide(); };
        _footer.Children.Add(clear);
        _footer.Children.Add(apply);
    }

    // One editor row: "[спочатку по/потім по] [column ▾] [↑ зростання / ↓ спадання] [↑] [↓] [✕]".
    // Laid out as a Grid with fixed column widths so every row's prefix / picker / direction / action
    // buttons line up vertically (a StackPanel would content-size each cell and leave them ragged).
    private Control BuildRow(int index, IReadOnlyList<SheetColumn> columns, ILocalizationService loc)
    {
        var (key, descending) = _working[index];

        var prefix = new TextBlock
        {
            Text = loc.Get(index == 0 ? "Sheet.Sort.FirstBy" : "Sheet.Sort.ThenBy"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        // Column picker — the same column can't sit on two levels, so exclude keys used by other rows.
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var col in columns)
        {
            var usedElsewhere = false;
            for (var j = 0; j < _working.Count; j++)
                if (j != index && _working[j].Key == col.Key) { usedElsewhere = true; break; }
            if (usedElsewhere)
                continue;
            combo.Items.Add(new ComboBoxItem { Content = col.PickerLabel, Tag = col.Key });
        }
        // Preselect the current column.
        foreach (ComboBoxItem item in combo.Items)
            if ((string?)item.Tag == key)
            {
                combo.SelectedItem = item;
                break;
            }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string newKey })
                _working[index] = (newKey, _working[index].Descending);
        };

        // Ascending / descending toggle button — fixed width so «↑ Зростання»/«↓ Спадання» don't shift.
        var dir = new Button
        {
            Classes = { "ghost", "small" },
            Width = 116,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Content = loc.Get(descending ? "Sheet.Sort.Desc" : "Sheet.Sort.Asc")
        };
        dir.Click += (_, _) =>
        {
            var d = !_working[index].Descending;
            _working[index] = (_working[index].Key, d);
            dir.Content = loc.Get(d ? "Sheet.Sort.Desc" : "Sheet.Sort.Asc");
        };

        Button IconButton(string glyphData, int delta, bool enabled)
        {
            var b = new Button
            {
                Classes = { "ghost" },
                Width = 26,
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                IsEnabled = enabled,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Content = new PathIcon { Width = 10, Height = 10, Data = Geometry.Parse(glyphData) }
            };
            b.Click += (_, _) =>
            {
                var target = index + delta;
                (_working[index], _working[target]) = (_working[target], _working[index]);
                Rebuild();
            };
            return b;
        }

        var up = IconButton("M5,1 L9,6 L1,6 Z", -1, index > 0);
        var down = IconButton("M1,3 L9,3 L5,8 Z", +1, index < _working.Count - 1);

        var remove = new Button
        {
            Classes = { "ghost" },
            Width = 26,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M0,0 L8,8 M8,0 L0,8"),
                Stroke = Brushes.Gray,
                StrokeThickness = 1.5,
                Width = 8,
                Height = 8
            }
        };
        remove.Click += (_, _) => { _working.RemoveAt(index); Rebuild(); };

        // Fixed grid track sizes keep all rows aligned: prefix | picker (fills) | direction | ↑ | ↓ | ✕.
        // Total (78+174+120+30+30+30 = 462) matches the fixed panel inner width so no horizontal scroll.
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("78,174,120,30,30,30"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1)
        };
        SetCell(prefix, 0);
        SetCell(combo, 1);
        SetCell(dir, 2);
        SetCell(up, 3);
        SetCell(down, 4);
        SetCell(remove, 5);
        row.Children.Add(prefix);
        row.Children.Add(combo);
        row.Children.Add(dir);
        row.Children.Add(up);
        row.Children.Add(down);
        row.Children.Add(remove);
        return row;

        static void SetCell(Control c, int col) => Grid.SetColumn(c, col);
    }

    // The first sortable column not already used on a level, for "+ Add level".
    private SheetColumn? FirstUnused(IReadOnlyList<SheetColumn> columns)
    {
        foreach (var col in columns)
        {
            var used = false;
            foreach (var (key, _) in _working)
                if (key == col.Key) { used = true; break; }
            if (!used)
                return col;
        }
        return null;
    }
}
