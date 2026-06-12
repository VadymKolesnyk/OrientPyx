using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using OrientDesk.Localization;
using OrientDesk.Presentation.Behaviors;
using OrientDesk.Presentation.Converters;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Controls;

/// <summary>
/// Builds the editor control for a roster cell from its <see cref="RosterColumn"/>. This is the
/// logic that used to live in <c>ParticipantsView.axaml.cs</c> (BuildGroupCombo / BuildChipEditor /
/// BuildRosterDayCell / BuildRosterCollapsedCell / BuildDifferentLabel), relocated into the control
/// and re-pointed at the column model. The bound DataContext for every cell is a
/// <see cref="ParticipantRosterRowViewModel"/>.
/// </summary>
internal sealed class RosterCellFactory
{
    private static readonly BoolToOpacityConverter DimWhenNotMember = new();

    /// <summary>True when the bound <c>IsMember</c> is false — drives the disabled chip cell's grey backdrop.</summary>
    private static readonly FuncValueConverter<bool, bool> NotMember = new(member => !member);

    private readonly ILocalizationService _loc;
    private readonly Action<object>? _onDelete;
    private readonly RentalChipRegistry? _rentalChips;
    private readonly Action<string>? _onToggleRental;

    public RosterCellFactory(
        ILocalizationService localization,
        Action<object>? onDelete,
        RentalChipRegistry? rentalChips = null,
        Action<string>? onToggleRental = null)
    {
        _loc = localization;
        _onDelete = onDelete;
        _rentalChips = rentalChips;
        _onToggleRental = onToggleRental;
    }

    public Control Build(RosterColumn column) => column.Kind switch
    {
        RosterCellKind.IdentityText => BuildIdentityText(column.IdentityPath),
        RosterCellKind.ChipText => BuildChipEditor(pathPrefix: string.Empty, chipPath: column.IdentityPath, numericOnly: true, highlight: true),
        RosterCellKind.BirthDate => BuildBirthDate(),
        RosterCellKind.Group => BuildDayCell(column, isGroup: true),
        RosterCellKind.Chip => BuildDayCell(column, isGroup: false),
        RosterCellKind.RowGroup => BuildGroupCombo(pathPrefix: string.Empty),
        RosterCellKind.CollapsedGroup => BuildCollapsedGroup(),
        RosterCellKind.CollapsedChip => BuildCollapsedChip(),
        RosterCellKind.Actions => BuildDeleteButton(),
        _ => new Control()
    };

    // ── Identity ────────────────────────────────────────────────────────────────────────────────
    private static TextBox BuildIdentityText(string path, bool digitsOnly = false)
    {
        var box = new TextBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        box[!TextBox.TextProperty] = new Binding(path) { Mode = BindingMode.TwoWay };
        if (digitsOnly)
            NumericInput.SetDigits(box, true);
        return box;
    }

    private BirthDateCell BuildBirthDate()
    {
        // CalendarDatePicker bound to the row's BirthDate via the global DateTimeOffset↔DateTime converter.
        var picker = new CalendarDatePicker
        {
            SelectedDateFormat = CalendarDatePickerFormat.Custom,
            CustomDateFormatString = "dd.MM.yyyy",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };
        picker[!CalendarDatePicker.SelectedDateProperty] =
            new Binding(nameof(ParticipantRosterRowViewModel.BirthDate))
            {
                Mode = BindingMode.TwoWay,
                Converter = (Avalonia.Application.Current!.Resources["DateTimeOffsetToDateTime"] as IValueConverter)
            };
        picker[!CalendarDatePicker.PlaceholderTextProperty] = new Binding("Localization[Common.DatePlaceholder]");
        return new BirthDateCell(picker);
    }

    // ── Expanded per-day cell ─────────────────────────────────────────────────────────────────────
    private Control BuildDayCell(RosterColumn column, bool isGroup)
    {
        var i = column.DayIndex;
        if (isGroup)
        {
            // The group combo stays interactive on every day — picking a group is how a non-member
            // joins that day — and only dims to signal a non-member row.
            var combo = BuildGroupCombo($"Days[{i}].");
            combo[!Visual.OpacityProperty] =
                new Binding($"Days[{i}].{nameof(RosterDayCellViewModel.IsMember)}") { Converter = DimWhenNotMember };
            return combo;
        }

        // Chip cell: numbers only, and non-editable (disabled, greyed) on days the participant does
        // not run. A grey backdrop fills the whole cell so the "disabled" state reads as a flat tint
        // rather than a faint floating textbox.
        var editor = BuildChipEditor($"Days[{i}].", numericOnly: true, highlight: true);
        editor[!InputElement.IsEnabledProperty] =
            new Binding($"Days[{i}].{nameof(RosterDayCellViewModel.IsMember)}");

        var backdrop = new Border
        {
            [!Visual.IsVisibleProperty] =
                new Binding($"Days[{i}].{nameof(RosterDayCellViewModel.IsMember)}") { Converter = NotMember },
            [!Border.BackgroundProperty] = new DynamicResourceExtension("SurfaceSubtle"),
        };

        var panel = new Panel();
        panel.Children.Add(backdrop);
        panel.Children.Add(editor);
        return panel;
    }

    // ── Collapsed merged cells ────────────────────────────────────────────────────────────────────
    private Control BuildCollapsedGroup()
    {
        var panel = new Panel();
        var combo = BuildGroupCombo(
            pathPrefix: string.Empty,
            groupOptionsPath: $"Days[0].{nameof(RosterDayCellViewModel.GroupOptions)}",
            selectedPath: nameof(ParticipantRosterRowViewModel.CollapsedGroupValue));
        combo[!Visual.IsVisibleProperty] = new Binding(nameof(ParticipantRosterRowViewModel.GroupShowsInput));
        panel.Children.Add(combo);

        var different = BuildDifferentLabel();
        different[!Visual.IsVisibleProperty] = new Binding(nameof(ParticipantRosterRowViewModel.GroupValuesDiffer));
        panel.Children.Add(different);
        return panel;
    }

    private Control BuildCollapsedChip()
    {
        var panel = new Panel();
        var editor = BuildChipEditor(
            pathPrefix: string.Empty,
            chipPath: nameof(ParticipantRosterRowViewModel.CollapsedChipValue),
            numericOnly: true,
            highlight: true);
        editor[!Visual.IsVisibleProperty] = new Binding(nameof(ParticipantRosterRowViewModel.ChipShowsInput));
        panel.Children.Add(editor);

        var different = BuildDifferentLabel();
        different[!Visual.IsVisibleProperty] = new Binding(nameof(ParticipantRosterRowViewModel.ChipShowsDifferent));
        panel.Children.Add(different);
        // A participant who runs no day shows neither child.
        return panel;
    }

    // ── Shared editors ────────────────────────────────────────────────────────────────────────────
    private static ComboBox BuildGroupCombo(
        string pathPrefix,
        string? groupOptionsPath = null,
        string? selectedPath = null)
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<GroupOption>((_, _) =>
                new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(GroupOption.Label)) })
        };
        combo[!ItemsControl.ItemsSourceProperty] =
            new Binding(groupOptionsPath ?? $"{pathPrefix}{nameof(RosterDayCellViewModel.GroupOptions)}");
        combo[!SelectingItemsControl.SelectedItemProperty] =
            new Binding(selectedPath ?? $"{pathPrefix}{nameof(RosterDayCellViewModel.SelectedGroup)}")
            { Mode = BindingMode.TwoWay };
        return combo;
    }

    private TextBox BuildChipEditor(string pathPrefix, string? chipPath = null, bool numericOnly = false, bool highlight = false)
    {
        var box = new TextBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        box[!TextBox.TextProperty] =
            new Binding(chipPath ?? $"{pathPrefix}{nameof(RosterDayCellViewModel.Chip)}")
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus };
        if (numericOnly)
            // A chip is a SportIdent card number kept as a string — restrict input to digits only
            // (no sign or separator) so the field can never hold a non-digit character.
            NumericInput.SetDigits(box, true);
        if (highlight && _rentalChips is not null)
        {
            // Bold-red a number that isn't in the rental database, and let a double-click toggle it.
            ChipHighlight.SetRegistry(box, _rentalChips);
            if (_onToggleRental is not null)
                ChipHighlight.SetToggle(box, _onToggleRental);
        }
        return box;
    }

    private TextBlock BuildDifferentLabel() => new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        Padding = new Thickness(10, 0),
        Text = _loc.Get("Participants.Roster.Different"),
        [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextMuted")
    };

    private Button BuildDeleteButton()
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
            [ToolTip.TipProperty] = _loc.Get("Participants.Delete")
        };
        button.Click += (_, _) =>
        {
            if (button.DataContext is { } row)
                _onDelete?.Invoke(row);
        };
        return button;
    }
}

/// <summary>
/// Wraps a birth-date <see cref="CalendarDatePicker"/> so the cell host can tell date cells apart
/// (they are always interactive, never seeded by a keystroke).
/// </summary>
internal sealed class BirthDateCell : Decorator
{
    public BirthDateCell(CalendarDatePicker picker) => Child = picker;
}
