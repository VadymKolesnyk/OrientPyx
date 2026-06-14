using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.Controls;

/// <summary>
/// A toolbar button that opens a checkbox list to show/hide a <see cref="SheetTable"/>'s columns.
/// Bind <see cref="Table"/> to the table and <see cref="Localization"/> to the page's service; the
/// list is rebuilt from the table's <see cref="SheetTable.ToggleableColumns"/> each time the flyout
/// opens (and when the table's column set changes), so it always reflects the live columns.
/// </summary>
public sealed class SheetColumnsButton : Button
{
    public static readonly StyledProperty<SheetTable?> TableProperty =
        AvaloniaProperty.Register<SheetColumnsButton, SheetTable?>(nameof(Table));

    public static readonly StyledProperty<ILocalizationService?> LocalizationProperty =
        AvaloniaProperty.Register<SheetColumnsButton, ILocalizationService?>(nameof(Localization));

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

    private readonly StackPanel _list;
    private readonly Flyout _flyout;

    public SheetColumnsButton()
    {
        Classes.Add("ghost");

        // Icon-only button: a "hamburger" (three horizontal lines) that opens the column picker.
        // PathIcon fills its geometry, so open line segments wouldn't render — use three filled bars.
        var icon = new PathIcon
        {
            Data = Geometry.Parse("M3,4 h18 v2.5 h-18 z M3,10.75 h18 v2.5 h-18 z M3,17.5 h18 v2.5 h-18 z"),
            Width = 16,
            Height = 16
        };
        Content = icon;

        _list = new StackPanel { Spacing = 2, Margin = new Thickness(4) };

        // The flyout opens in its own top-level popup, OUTSIDE the window's root layout transform, so
        // it would render at base size when the UI scale isn't 100%. Wrap the content in a
        // LayoutTransformControl whose ScaleTransform tracks the same UiScale (matching PopupScaling.axaml).
        var scaled = new LayoutTransformControl
        {
            LayoutTransform = BuildUiScaleTransform(),
            Child = new ScrollViewer { MaxHeight = 360, Content = _list }
        };
        _flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            Content = scaled
        };
        Flyout = _flyout;

        // Rebuild the checkbox list each time the flyout opens, so it reflects the current columns.
        _flyout.Opened += (_, _) => RebuildList();
        ApplyCaption();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TableProperty)
        {
            if (change.OldValue is SheetTable oldTable)
                oldTable.ColumnsChanged -= OnTableColumnsChanged;
            if (change.NewValue is SheetTable newTable)
                newTable.ColumnsChanged += OnTableColumnsChanged;
        }
        else if (change.Property == LocalizationProperty)
        {
            ApplyCaption();
        }
    }

    private void OnTableColumnsChanged(object? sender, EventArgs e)
    {
        // Keep the open list in sync (e.g. a header context-menu hide while the flyout is open).
        // Skip the rebuild we ourselves triggered by toggling a checkbox (handled in-place), so we
        // don't tear down the checkbox whose handler is still running.
        if (_flyout.IsOpen && !_togglingFromList)
            RebuildList();
    }

    // True while a checkbox toggle is driving the table rebuild, so the resulting ColumnsChanged does
    // not re-enter RebuildList and remove the live checkbox mid-handler.
    private bool _togglingFromList;

    private void ApplyCaption()
        => ToolTip.SetTip(this, Localization?.Get("Sheet.Columns.Button") ?? "Columns");

    // A ScaleTransform whose X/Y track the app-wide UiScale service (exposed as a StaticResource in
    // App.axaml). Live: the service raises PropertyChanged on Scale, so the flyout follows a scale change.
    internal static ScaleTransform BuildUiScaleTransform()
    {
        var transform = new ScaleTransform();
        if (Application.Current?.Resources["UiScale"] is { } uiScale)
        {
            transform[!ScaleTransform.ScaleXProperty] = new Binding("Scale") { Source = uiScale };
            transform[!ScaleTransform.ScaleYProperty] = new Binding("Scale") { Source = uiScale };
        }
        return transform;
    }

    // (Re)builds the checkbox list from the table's toggleable columns. Each checkbox is checked when
    // the column is visible; toggling it shows/hides via the table (which rebuilds and re-raises).
    private void RebuildList()
    {
        _list.Children.Clear();
        if (Table is not { } table)
            return;

        foreach (var column in table.ToggleableColumns())
        {
            var key = column.Key;
            var check = new CheckBox
            {
                Content = column.PickerLabel,
                IsChecked = !column.IsHidden,
                Padding = new Thickness(6, 2),
                MinHeight = 0
            };
            check.IsCheckedChanged += (_, _) =>
            {
                _togglingFromList = true;
                try { table.SetColumnHidden(key, hidden: check.IsChecked != true); }
                finally { _togglingFromList = false; }
            };
            _list.Children.Add(check);
        }
    }
}
