using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using OrientDesk.Localization;
using OrientDesk.Presentation.Behaviors;
using OrientDesk.Presentation.Converters;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Controls;

/// <summary>
/// A small fluent builder that turns a page's flat column list into the <see cref="SheetBand"/>
/// model <see cref="SheetTable"/> renders. Every column becomes a single-column
/// <see cref="SheetBand.BandKind.Identity"/> band (a plain one-tier header), with its cell built by
/// a <see cref="SheetCellKind.Custom"/> <see cref="SheetColumn.CellBuilder"/>.
///
/// This is the reusable replacement for the old per-page <c>SheetDataGrid</c> column XAML: pages used
/// to declare DataGridTemplateColumns; now they call <c>Text(...)</c>/<c>Combo(...)</c>/<c>Date(...)</c>
/// etc. in code-behind and hand the resulting bands to the table via its <c>Bands</c> property.
/// Headers are resolved (localized) here, so a language change is handled by rebuilding.
/// </summary>
public sealed class SheetColumnBuilder
{
    private static readonly BoolToOpacityConverter DimConverter = new();

    private readonly ILocalizationService _loc;
    private readonly List<SheetBand> _bands = [];

    public SheetColumnBuilder(ILocalizationService localization)
    {
        _loc = localization;
    }

    /// <summary>The numeric input mask a text cell enforces while typing.</summary>
    public enum NumericMask { None, Digits, Integer, Decimal, Time }

    /// <summary>The accumulated bands, ready to assign to <see cref="SheetTable.Bands"/>.</summary>
    public IReadOnlyList<SheetBand> Bands => _bands;

    // ── Display / editable text ───────────────────────────────────────────────────────────────────
    /// <summary>
    /// A text column: a read-only <see cref="TextBlock"/> in normal state, a <see cref="TextBox"/>
    /// once the cell enters edit. Pass <paramref name="editPath"/> = null for a read-only column.
    /// </summary>
    public SheetColumnBuilder Text(
        string headerKey,
        string displayPath,
        string? editPath = null,
        double? width = null,
        double minWidth = 90,
        string? sortPath = null,
        NumericMask mask = NumericMask.None,
        string? enabledPath = null,
        string? opacityPath = null,
        string? placeholder = "—",
        RentalChipRegistry? rentalChips = null,
        Action<string>? toggleRental = null)
    {
        var column = NewColumn(headerKey, width, minWidth, sortPath ?? displayPath);
        column.CellBuilder = () =>
        {
            // Editable: the table swaps display↔edit by focusing the inner editor, so a single
            // control that's read-only-looking until focused fits the model best — but to match the
            // old grid's "TextBlock then TextBox" feel we use one TextBox skinned flat (the roster
            // table already styles in-cell TextBoxes borderless). A read-only column gets a TextBlock.
            if (editPath is null)
                return ReadOnlyText(displayPath, opacityPath);

            var box = new TextBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                [!TextBox.TextProperty] = new Binding(editPath)
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                }
            };
            if (placeholder is not null)
                box.PlaceholderText = placeholder;
            ApplyMask(box, mask);
            if (enabledPath is not null)
                box[!InputElement.IsEnabledProperty] = new Binding(enabledPath);
            if (opacityPath is not null)
                box[!Visual.OpacityProperty] = new Binding(opacityPath) { Converter = DimConverter };
            if (rentalChips is not null)
            {
                ChipHighlight.SetRegistry(box, rentalChips);
                if (toggleRental is not null)
                    ChipHighlight.SetToggle(box, toggleRental);
            }
            return box;
        };
        return Add(column);
    }

    // ── ComboBox ──────────────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// A combo column bound to <paramref name="itemsPath"/>/<paramref name="selectedPath"/> on the
    /// row, rendering each item's <paramref name="labelPath"/>.
    /// </summary>
    public SheetColumnBuilder Combo(
        string headerKey,
        string itemsPath,
        string selectedPath,
        string labelPath,
        double? width = null,
        double minWidth = 120,
        string? sortPath = null)
    {
        var column = NewColumn(headerKey, width, minWidth, sortPath ?? string.Empty);
        column.CellBuilder = () =>
        {
            var combo = new SearchableComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                SearchWatermark = _loc.Get("Common.Search"),
                TextSelector = item => item?.GetType().GetProperty(labelPath)?.GetValue(item) as string
                    ?? item?.ToString() ?? string.Empty,
                ItemTemplate = new FuncDataTemplate<object>((_, _) =>
                    new TextBlock { [!TextBlock.TextProperty] = new Binding(labelPath) }),
                [!ItemsControl.ItemsSourceProperty] = new Binding(itemsPath),
                [!SelectingItemsControl.SelectedItemProperty] =
                    new Binding(selectedPath) { Mode = BindingMode.TwoWay }
            };
            return combo;
        };
        return Add(column);
    }

    // ── CalendarDatePicker (DateTimeOffset) ───────────────────────────────────────────────────────
    /// <summary>
    /// A date column bound to a <c>DateTimeOffset?</c> property via the app's DateTimeOffset↔DateTime
    /// converter, formatted dd.MM.yyyy.
    /// </summary>
    public SheetColumnBuilder Date(
        string headerKey,
        string path,
        double? width = 160,
        double minWidth = 140,
        string? placeholderKey = "Common.DatePlaceholder")
    {
        var column = NewColumn(headerKey, width, minWidth, path);
        column.CellBuilder = () =>
        {
            var picker = new CalendarDatePicker
            {
                SelectedDateFormat = CalendarDatePickerFormat.Custom,
                CustomDateFormatString = "dd.MM.yyyy",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                [!CalendarDatePicker.SelectedDateProperty] = new Binding(path)
                {
                    Mode = BindingMode.TwoWay,
                    Converter = Application.Current!.Resources["DateTimeOffsetToDateTime"] as IValueConverter
                }
            };
            if (placeholderKey is not null)
                picker.PlaceholderText = _loc.Get(placeholderKey);
            NumericInput.SetDate(picker, true);
            return picker;
        };
        return Add(column);
    }

    // ── Fully custom cell ─────────────────────────────────────────────────────────────────────────
    /// <summary>A column whose cell is built entirely by the caller (e.g. a multi-button action cell).</summary>
    public SheetColumnBuilder Custom(
        string headerKey,
        Func<Control> cellBuilder,
        double? width = null,
        double minWidth = 90,
        string? sortPath = null)
    {
        var column = NewColumn(headerKey, width, minWidth, sortPath ?? string.Empty);
        column.CellBuilder = cellBuilder;
        return Add(column);
    }

    // ── Trailing delete action ────────────────────────────────────────────────────────────────────
    /// <summary>
    /// The trailing single-icon delete column. Clicking invokes <paramref name="onDelete"/> with the
    /// row; the table's keyboard Delete is wired separately via <c>DeleteCommand</c>/<c>DeleteRequested</c>.
    /// </summary>
    public SheetColumnBuilder DeleteAction(Action<object> onDelete, string tooltipKey)
    {
        var column = new SheetColumn(SheetCellKind.Custom)
        {
            Header = string.Empty,
            Width = 48,
            WidthCapped = true,
            MinWidth = 48,
        };
        column.CellBuilder = () => DeleteButton(onDelete, tooltipKey);
        _bands.Add(new SheetBand(SheetBand.BandKind.Identity, [column]) { Header = string.Empty });
        return this;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────
    private SheetColumn NewColumn(string headerKey, double? width, double minWidth, string sortPath)
    {
        var column = new SheetColumn(SheetCellKind.Custom)
        {
            Header = _loc.Get(headerKey),
            SortPath = sortPath,
            MinWidth = minWidth,
        };
        if (width is { } w)
        {
            column.Width = w;
            column.WidthCapped = true;
        }
        return column;
    }

    private SheetColumnBuilder Add(SheetColumn column)
    {
        _bands.Add(new SheetBand(SheetBand.BandKind.Identity, [column]) { Header = column.Header });
        return this;
    }

    private static Control ReadOnlyText(string displayPath, string? opacityPath)
    {
        var block = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            [!TextBlock.TextProperty] = new Binding(displayPath)
        };
        if (opacityPath is not null)
            block[!Visual.OpacityProperty] = new Binding(opacityPath) { Converter = DimConverter };
        return block;
    }

    private static void ApplyMask(TextBox box, NumericMask mask)
    {
        switch (mask)
        {
            case NumericMask.Digits: NumericInput.SetDigits(box, true); break;
            case NumericMask.Integer: NumericInput.SetInteger(box, true); break;
            case NumericMask.Decimal: NumericInput.SetDecimal(box, true); break;
            case NumericMask.Time: NumericInput.SetTime(box, true); break;
        }
    }

    private Button DeleteButton(Action<object> onDelete, string tooltipKey)
    {
        var button = new Button
        {
            Classes = { "danger", "small" },
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Content = new PathIcon
            {
                Data = Geometry.Parse("M6,7 h12 M9,7 v-2 h6 v2 M8,7 l1,13 h6 l1,-13"),
                Width = 14,
                Height = 14
            },
            [ToolTip.TipProperty] = _loc.Get(tooltipKey)
        };
        button.Click += (_, _) =>
        {
            if (button.DataContext is { } row)
                onDelete(row);
        };
        return button;
    }
}
