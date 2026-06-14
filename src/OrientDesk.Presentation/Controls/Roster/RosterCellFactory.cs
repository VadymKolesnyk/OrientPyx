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
using OrientDesk.Presentation.Converters;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Controls;

/// <summary>
/// Builds the editor control for a roster cell from its <see cref="SheetColumn"/>. This is the
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

    public Control Build(SheetColumn column) => column.Kind switch
    {
        SheetCellKind.IdentityText => BuildIdentityText(column.IdentityPath),
        SheetCellKind.ChipText => BuildChipEditor(pathPrefix: string.Empty, chipPath: column.IdentityPath, highlight: true),
        SheetCellKind.StartTimeText => BuildIdentityText(column.IdentityPath),
        SheetCellKind.BirthDate => BuildBirthDate(),
        SheetCellKind.Group => BuildDayCell(column, isGroup: true),
        SheetCellKind.Chip => BuildDayCell(column, isGroup: false),
        SheetCellKind.StartTime => BuildDayStartTimeCell(column),
        SheetCellKind.OutOfCompetition => BuildDayOutOfCompetitionCell(column),
        SheetCellKind.RowGroup => BuildGroupCombo(pathPrefix: string.Empty),
        SheetCellKind.RowRegion => BuildRegionCombo(),
        SheetCellKind.RowClub => BuildClubCombo(),
        SheetCellKind.RowDussh => BuildDusshCombo(),
        SheetCellKind.RowRank => BuildRankCombo(),
        SheetCellKind.IdentityBool => BuildBoolCheckBox(column.IdentityPath),
        SheetCellKind.CollapsedGroup => BuildCollapsedGroup(),
        SheetCellKind.CollapsedChip => BuildCollapsedChip(),
        SheetCellKind.CollapsedStartTime => BuildCollapsedStartTime(),
        SheetCellKind.CollapsedOutOfCompetition => BuildCollapsedOutOfCompetition(),
        SheetCellKind.Actions => BuildDeleteButton(),
        SheetCellKind.Custom => column.CellBuilder?.Invoke() ?? new Control(),
        _ => new Control()
    };

    // ── Identity ────────────────────────────────────────────────────────────────────────────────
    private static Control BuildIdentityText(string path, SheetColumnBuilder.NumericMask mask = SheetColumnBuilder.NumericMask.None)
        => new LazyTextCell(path, path, new SheetTextOptions { Mask = mask });

    private Control BuildBirthDate()
        => new LazyDateCell(nameof(ParticipantRosterRowViewModel.BirthDate), _loc.Get("Common.DatePlaceholder"));

    // ── Expanded per-day cell ─────────────────────────────────────────────────────────────────────
    private Control BuildDayCell(SheetColumn column, bool isGroup)
    {
        var i = column.DayIndex;
        if (isGroup)
        {
            // The group combo stays interactive on every day — picking a group is how a non-member
            // joins that day — and only dims to signal a non-member row.
            var cell = BuildGroupCombo($"Days[{i}].");
            cell[!Visual.OpacityProperty] =
                new Binding($"Days[{i}].{nameof(RosterDayCellViewModel.IsMember)}") { Converter = DimWhenNotMember };
            return cell;
        }

        // Chip cell: numbers only, and non-editable (disabled, greyed) on days the participant does
        // not run. A grey backdrop fills the whole cell so the "disabled" state reads as a flat tint
        // rather than a faint floating textbox.
        var editor = BuildChipEditor($"Days[{i}].", highlight: true);
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

    // A per-day start-time cell: a HH:mm text box, disabled + greyed on days the participant doesn't run.
    private Control BuildDayStartTimeCell(SheetColumn column)
    {
        var i = column.DayIndex;
        var path = $"Days[{i}].{nameof(RosterDayCellViewModel.StartTimeText)}";
        var box = new LazyTextCell(path, path, new SheetTextOptions { CommitOnLostFocus = true });
        box[!InputElement.IsEnabledProperty] = new Binding($"Days[{i}].{nameof(RosterDayCellViewModel.IsMember)}");
        return WrapWithNonMemberBackdrop(box, i);
    }

    // A per-day out-of-competition cell: a centered CheckBox, disabled + greyed for non-members.
    private Control BuildDayOutOfCompetitionCell(SheetColumn column)
    {
        var i = column.DayIndex;
        var box = new CheckBox
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            [!ToggleButton.IsCheckedProperty] = new Binding($"Days[{i}].{nameof(RosterDayCellViewModel.OutOfCompetition)}")
                { Mode = BindingMode.TwoWay },
            [!InputElement.IsEnabledProperty] = new Binding($"Days[{i}].{nameof(RosterDayCellViewModel.IsMember)}"),
        };
        return WrapWithNonMemberBackdrop(box, i);
    }

    // Lays a grey backdrop behind an editor that is only visible on days the participant doesn't run,
    // so a disabled per-day cell reads as a flat tint (matches the chip cell).
    private static Control WrapWithNonMemberBackdrop(Control editor, int dayIndex)
    {
        var backdrop = new Border
        {
            [!Visual.IsVisibleProperty] =
                new Binding($"Days[{dayIndex}].{nameof(RosterDayCellViewModel.IsMember)}") { Converter = NotMember },
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

        // One real group used on some (not all) days: read-only "<group> (<n> днів)" summary.
        var single = BuildMutedLabel();
        single[!TextBlock.TextProperty] = new Binding(nameof(ParticipantRosterRowViewModel.GroupSingleSummary));
        single[!Visual.IsVisibleProperty] = new Binding(nameof(ParticipantRosterRowViewModel.GroupShowsSingle));
        panel.Children.Add(single);

        var different = BuildDifferentLabel();
        different[!Visual.IsVisibleProperty] = new Binding(nameof(ParticipantRosterRowViewModel.GroupShowsDifferent));
        panel.Children.Add(different);
        return panel;
    }

    private Control BuildCollapsedChip()
    {
        var panel = new Panel();
        var editor = BuildChipEditor(
            pathPrefix: string.Empty,
            chipPath: nameof(ParticipantRosterRowViewModel.CollapsedChipValue),
            highlight: true);
        editor[!Visual.IsVisibleProperty] = new Binding(nameof(ParticipantRosterRowViewModel.ChipShowsInput));
        panel.Children.Add(editor);

        var different = BuildDifferentLabel();
        different[!Visual.IsVisibleProperty] = new Binding(nameof(ParticipantRosterRowViewModel.ChipShowsDifferent));
        panel.Children.Add(different);
        // A participant who runs no day shows neither child.
        return panel;
    }

    private Control BuildCollapsedStartTime()
    {
        var panel = new Panel();
        var editor = new LazyTextCell(
            nameof(ParticipantRosterRowViewModel.CollapsedStartTimeText),
            nameof(ParticipantRosterRowViewModel.CollapsedStartTimeText),
            new SheetTextOptions { CommitOnLostFocus = true });
        editor[!Visual.IsVisibleProperty] = new Binding(nameof(ParticipantRosterRowViewModel.StartTimeShowsInput));
        panel.Children.Add(editor);

        var different = BuildDifferentLabel();
        different[!Visual.IsVisibleProperty] = new Binding(nameof(ParticipantRosterRowViewModel.StartTimeShowsDifferent));
        panel.Children.Add(different);
        return panel;
    }

    private Control BuildCollapsedOutOfCompetition()
    {
        var panel = new Panel();
        var box = new CheckBox
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            [!ToggleButton.IsCheckedProperty] = new Binding(nameof(ParticipantRosterRowViewModel.CollapsedOutOfCompetition))
                { Mode = BindingMode.TwoWay },
            [!Visual.IsVisibleProperty] = new Binding(nameof(ParticipantRosterRowViewModel.OutOfCompetitionShowsInput)),
        };
        panel.Children.Add(box);

        var different = BuildDifferentLabel();
        different[!Visual.IsVisibleProperty] = new Binding(nameof(ParticipantRosterRowViewModel.OutOfCompetitionShowsDifferent));
        panel.Children.Add(different);
        return panel;
    }

    // ── Shared editors ────────────────────────────────────────────────────────────────────────────
    // Each combo cell is a LazyComboCell: it shows the selected option's label and only builds the real
    // SearchableComboBox when the cell is entered (focus / click / keyboard). At 600 rows × N combo
    // columns this keeps the realised visual tree tiny — the combos were the dominant scroll/GC cost.

    private LazyComboCell BuildGroupCombo(
        string pathPrefix,
        string? groupOptionsPath = null,
        string? selectedPath = null)
    {
        var itemsPath = groupOptionsPath ?? $"{pathPrefix}{nameof(RosterDayCellViewModel.GroupOptions)}";
        var selected = selectedPath ?? $"{pathPrefix}{nameof(RosterDayCellViewModel.SelectedGroup)}";
        return new LazyComboCell(
            () => BuildComboCore<GroupOption>(itemsPath, selected, nameof(GroupOption.Label)),
            $"{selected}.{nameof(GroupOption.Label)}");
    }

    // A region combo cell bound on the row. Both ParticipantDayRowViewModel and
    // ParticipantRosterRowViewModel expose RegionOptions/SelectedRegion with these names.
    private LazyComboCell BuildRegionCombo() => new(
        () => BuildComboCore<RegionOption>(
            nameof(ParticipantRosterRowViewModel.RegionOptions),
            nameof(ParticipantRosterRowViewModel.SelectedRegion),
            nameof(RegionOption.Label)),
        $"{nameof(ParticipantRosterRowViewModel.SelectedRegion)}.{nameof(RegionOption.Label)}");

    // A club combo cell bound on the row. Both row VMs expose ClubOptions/SelectedClub with these names.
    private LazyComboCell BuildClubCombo() => new(
        () => BuildComboCore<ClubOption>(
            nameof(ParticipantRosterRowViewModel.ClubOptions),
            nameof(ParticipantRosterRowViewModel.SelectedClub),
            nameof(ClubOption.Label)),
        $"{nameof(ParticipantRosterRowViewModel.SelectedClub)}.{nameof(ClubOption.Label)}");

    // A ДЮСШ combo cell bound on the row. Both row VMs expose DusshOptions/SelectedDussh with these names.
    private LazyComboCell BuildDusshCombo() => new(
        () => BuildComboCore<DusshOption>(
            nameof(ParticipantRosterRowViewModel.DusshOptions),
            nameof(ParticipantRosterRowViewModel.SelectedDussh),
            nameof(DusshOption.Label)),
        $"{nameof(ParticipantRosterRowViewModel.SelectedDussh)}.{nameof(DusshOption.Label)}");

    // A rank combo cell bound on the row. Both row VMs expose RankOptions/SelectedRank with these names.
    // Rank stores text, so the dropdown has no "+ new" option (ranks are managed on the Ranks page).
    private LazyComboCell BuildRankCombo() => new(
        () => BuildComboCore<RankOption>(
            nameof(ParticipantRosterRowViewModel.RankOptions),
            nameof(ParticipantRosterRowViewModel.SelectedRank),
            nameof(RankOption.Label)),
        $"{nameof(ParticipantRosterRowViewModel.SelectedRank)}.{nameof(RankOption.Label)}");

    // Builds the real SearchableComboBox for a combo cell, bound to the given items/selected paths and
    // rendering each option's <paramref name="labelPath"/>. Created on demand by LazyComboCell.
    private SearchableComboBox BuildComboCore<TOption>(string itemsPath, string selectedPath, string labelPath)
    {
        var combo = new SearchableComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            SearchWatermark = _loc.Get("Common.Search"),
            ItemTemplate = new FuncDataTemplate<TOption>((_, _) =>
                new TextBlock { [!TextBlock.TextProperty] = new Binding(labelPath) })
        };
        combo[!ItemsControl.ItemsSourceProperty] = new Binding(itemsPath);
        combo[!SelectingItemsControl.SelectedItemProperty] =
            new Binding(selectedPath) { Mode = BindingMode.TwoWay };
        return combo;
    }

    // A boolean CheckBox bound on the row by the given property path (e.g. IsFsouMember). Centered so
    // it reads as a flag cell rather than a stretched control.
    private static CheckBox BuildBoolCheckBox(string path)
    {
        var box = new CheckBox
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            [!ToggleButton.IsCheckedProperty] = new Binding(path) { Mode = BindingMode.TwoWay }
        };
        return box;
    }

    // A chip cell as a LazyTextCell: digits only, commits on lost focus, and (when a rental registry is
    // supplied) bold-reds a non-rental number on both the resting label and the editor, with the
    // Ctrl+double-click / context-menu toggle. Callers may still bind IsEnabled/IsVisible/Opacity on
    // the returned cell (the resting label inherits those from the cell).
    private LazyTextCell BuildChipEditor(string pathPrefix, string? chipPath = null, bool highlight = false)
    {
        var path = chipPath ?? $"{pathPrefix}{nameof(RosterDayCellViewModel.Chip)}";
        return new LazyTextCell(path, path, new SheetTextOptions
        {
            // A chip is a SportIdent card number kept as a string — restrict input to digits only.
            Mask = SheetColumnBuilder.NumericMask.Digits,
            CommitOnLostFocus = true,
            RentalChips = highlight ? _rentalChips : null,
            ToggleRental = highlight ? _onToggleRental : null,
            Localization = highlight ? _loc : null,
        });
    }

    private TextBlock BuildDifferentLabel()
    {
        var label = BuildMutedLabel();
        label.Text = _loc.Get("Participants.Roster.Different");
        return label;
    }

    // A muted, read-only cell label (the "різні" / single-group-summary states). Caller sets Text or
    // binds TextBlock.TextProperty.
    private static TextBlock BuildMutedLabel() => new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        Padding = new Thickness(10, 0),
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
