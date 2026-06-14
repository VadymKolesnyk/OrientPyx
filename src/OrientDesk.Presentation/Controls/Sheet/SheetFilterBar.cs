using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.Controls;

/// <summary>
/// The active-filter chips strip shown at the left of a <see cref="SheetTable"/>'s built-in toolbar.
/// One removable chip per active filter (label from <see cref="SheetFilter.Describe"/>, an ✕ that
/// clears it), plus a "clear all" when more than one is set. Collapses to nothing when no filters are
/// active. Bind <see cref="Table"/> to the table and <see cref="Localization"/> to the page's service
/// (same pattern as <see cref="SheetColumnsButton"/>); it refreshes on the table's
/// <see cref="SheetTable.FiltersChanged"/> event.
/// </summary>
public sealed class SheetFilterBar : ContentControl
{
    public static readonly StyledProperty<SheetTable?> TableProperty =
        AvaloniaProperty.Register<SheetFilterBar, SheetTable?>(nameof(Table));

    public static readonly StyledProperty<ILocalizationService?> LocalizationProperty =
        AvaloniaProperty.Register<SheetFilterBar, ILocalizationService?>(nameof(Localization));

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

    private readonly WrapPanel _chips;

    public SheetFilterBar()
    {
        VerticalAlignment = VerticalAlignment.Center;
        _chips = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Content = _chips;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TableProperty)
        {
            if (change.OldValue is SheetTable oldTable)
                oldTable.FiltersChanged -= OnFiltersChanged;
            if (change.NewValue is SheetTable newTable)
                newTable.FiltersChanged += OnFiltersChanged;
            Rebuild();
        }
        else if (change.Property == LocalizationProperty)
        {
            Rebuild();
        }
    }

    private void OnFiltersChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        _chips.Children.Clear();
        if (Table is not { } table || Localization is not { } loc)
        {
            IsVisible = false;
            return;
        }

        var any = false;
        foreach (var filter in table.ActiveFilters)
        {
            _chips.Children.Add(BuildChip(filter.Describe(loc), () => table.ClearColumnFilter(filter.ColumnKey)));
            any = true;
        }

        // A "clear all" pill when there is more than one filter.
        if (table.ActiveFilters.Count > 1)
            _chips.Children.Add(BuildClearAll(loc.Get("Sheet.Filter.ClearAll"), table.ClearAllFilters));

        IsVisible = any;
    }

    private static Border BuildChip(string text, Action onRemove)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };

        var close = new Button
        {
            Classes = { "ghost" },
            Padding = new Thickness(0),
            Width = 18,
            Height = 18,
            MinWidth = 0,
            MinHeight = 0,
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M0,0 L8,8 M8,0 L0,8"),
                Stroke = Brushes.Gray,
                StrokeThickness = 1.5,
                Width = 8,
                Height = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        close.Click += (_, _) => onRemove();

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(label);
        content.Children.Add(close);

        return new Border
        {
            Classes = { "filterChip" },
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x3B, 0x82, 0xF6)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 1, 4, 1),
            Margin = new Thickness(0, 0, 6, 0),
            Child = content
        };
    }

    private static Button BuildClearAll(string text, Action onClear)
    {
        var button = new Button
        {
            Classes = { "ghost", "small" },
            Content = text,
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += (_, _) => onClear();
        return button;
    }
}
