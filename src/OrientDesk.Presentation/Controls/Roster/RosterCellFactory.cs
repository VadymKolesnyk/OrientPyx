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

    public RosterCellFactory(
        ILocalizationService localization,
        Action<object>? onDelete,
        RentalChipRegistry? rentalChips = null)
    {
        _loc = localization;
        _onDelete = onDelete;
        _rentalChips = rentalChips;
    }

    public Control Build(SheetColumn column) => column.Kind switch
    {
        SheetCellKind.IdentityText => BuildIdentityText(column.IdentityPath),
        // Payment is a plain editable text cell; the status tint is painted on the whole SheetCell
        // container by the table (see SheetTable.BuildRow / PaymentHighlight), not on this content.
        SheetCellKind.PaymentText => BuildIdentityText(column.IdentityPath),
        SheetCellKind.ChipText => BuildChipEditor(pathPrefix: string.Empty, chipPath: column.IdentityPath, highlight: true),
        // The day-grid start-time column: an hh:mm:ss text box (digits only, auto-':') like the roster's
        // per-day cell.
        SheetCellKind.StartTimeText => BuildStartTimeEditor(column.IdentityPath),
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
        SheetCellKind.RaisedFeeFlag => BuildBoolCheckBox(nameof(ParticipantRosterRowViewModel.PaysRaisedFee)),
        SheetCellKind.TotalFee => BuildTotalFee(),
        SheetCellKind.CollapsedGroup => BuildCollapsedGroup(),
        SheetCellKind.CollapsedChip => BuildCollapsedChip(),
        SheetCellKind.CollapsedStartTime => BuildCollapsedStartTime(),
        SheetCellKind.CollapsedOutOfCompetition => BuildCollapsedOutOfCompetition(),
        SheetCellKind.RowResultText => BuildResultLabel(column.IdentityPath, column.ToolTipPath),
        SheetCellKind.RowStatus => BuildRowStatusCell(),
        SheetCellKind.ResultText => BuildDayResultLabel(column),
        SheetCellKind.Status => BuildDayStatusCell(column),
        // Collapsed result blocks are read-only: a single muted label bound to the row's merged value,
        // which already yields the shared value or the localized "різні" when member days disagree.
        SheetCellKind.CollapsedResultText or SheetCellKind.CollapsedStatus => BuildCollapsedResultLabel(column.IdentityPath),
        SheetCellKind.Actions => BuildDeleteButton(),
        SheetCellKind.Custom => column.CellBuilder?.Invoke() ?? new Control(),
        _ => new Control()
    };

    // The day-grid status cell: an always-editable combo (a participant shown in the day grid is a member),
    // letting a judge override the computed status or mark one when there's no read-out. Non-OK shows red.
    private Control BuildRowStatusCell()
    {
        var combo = BuildStatusCombo(
            nameof(ParticipantDayRowViewModel.StatusOptions),
            nameof(ParticipantDayRowViewModel.SelectedStatus),
            nameof(ParticipantDayRowViewModel.ResultStatusText),
            nameof(ParticipantDayRowViewModel.StatusIsProblem));
        combo[!InputElement.IsEnabledProperty] = new Binding(nameof(ParticipantDayRowViewModel.CanEditStatus));
        return combo;
    }

    // A collapsed (merged) read-only result label bound to a row property that yields the shared value or
    // the localized "різні". Muted like the other collapsed read-only summaries.
    private Control BuildCollapsedResultLabel(string mergedPath)
    {
        var label = BuildMutedLabel();
        label[!TextBlock.TextProperty] = new Binding(mergedPath);
        return label;
    }

    // A per-day read-only result label bound to Days[i].{path}, greyed on non-member days.
    private Control BuildDayResultLabel(SheetColumn column)
    {
        var i = column.DayIndex;
        var tooltip = string.IsNullOrEmpty(column.ToolTipPath) ? string.Empty : $"Days[{i}].{column.ToolTipPath}";
        var label = BuildResultLabel($"Days[{i}].{column.IdentityPath}", tooltip);
        label[!Visual.OpacityProperty] =
            new Binding($"Days[{i}].{nameof(RosterDayCellViewModel.IsMember)}") { Converter = DimWhenNotMember };
        return label;
    }

    // A per-day finish-status combo bound to Days[i], editable on any day the participant runs (a judge can
    // override the computed status or mark one with no read-out); disabled + greyed for non-members. Non-OK
    // shows red.
    private Control BuildDayStatusCell(SheetColumn column)
    {
        var i = column.DayIndex;
        var combo = BuildStatusCombo(
            $"Days[{i}].{nameof(RosterDayCellViewModel.StatusOptions)}",
            $"Days[{i}].{nameof(RosterDayCellViewModel.SelectedStatus)}",
            $"Days[{i}].{nameof(RosterDayCellViewModel.ResultStatusText)}",
            $"Days[{i}].{nameof(RosterDayCellViewModel.StatusIsProblem)}");
        combo[!InputElement.IsEnabledProperty] =
            new Binding($"Days[{i}].{nameof(RosterDayCellViewModel.CanEditStatus)}");
        return WrapWithNonMemberBackdrop(combo, i);
    }

    // ── Identity ────────────────────────────────────────────────────────────────────────────────
    private static Control BuildIdentityText(string path, SheetColumnBuilder.NumericMask mask = SheetColumnBuilder.NumericMask.None)
        => new LazyTextCell(path, path, new SheetTextOptions { Mask = mask });

    private Control BuildBirthDate()
        => new LazyDateCell(nameof(ParticipantRosterRowViewModel.BirthDate), _loc.Get("Common.DatePlaceholder"));

    // The day-grid start-time editor: an hh:mm:ss text box (digits only, auto-':') with a placeholder,
    // bound directly on the row by its identity path. Mirrors the roster's per-day start-time cell.
    private Control BuildStartTimeEditor(string path)
        => new LazyTextCell(path, path, new SheetTextOptions
        {
            Mask = SheetColumnBuilder.NumericMask.Time,
            Placeholder = _loc.Get("Common.TimePlaceholder"),
        });

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

    // A per-day start-time cell: an hh:mm:ss text box (digits only, auto-':'), disabled + greyed on days
    // the participant doesn't run.
    private Control BuildDayStartTimeCell(SheetColumn column)
    {
        var i = column.DayIndex;
        var path = $"Days[{i}].{nameof(RosterDayCellViewModel.StartTimeText)}";
        var box = new LazyTextCell(path, path, new SheetTextOptions
        {
            Mask = SheetColumnBuilder.NumericMask.Time,
            Placeholder = _loc.Get("Common.TimePlaceholder"),
            CommitOnLostFocus = true,
        });
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

    // The collapsed start-time cell is always read-only — even when every member day shares one value
    // (start time is edited per day, never on the merged roster cell). It shows the shared "hh:mm:ss"
    // value as a muted label, or the "різні" label when the days disagree.
    private Control BuildCollapsedStartTime()
    {
        var panel = new Panel();
        var shared = BuildMutedLabel();
        shared[!TextBlock.TextProperty] = new Binding(nameof(ParticipantRosterRowViewModel.CollapsedStartTimeText));
        shared[!Visual.IsVisibleProperty] = new Binding(nameof(ParticipantRosterRowViewModel.StartTimeShowsInput));
        panel.Children.Add(shared);

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

    /// <summary>
    /// Builds a per-discount CheckBox for a participant row. The discount set is dynamic, so the column
    /// builders create one of these via a <see cref="SheetCellKind.Custom"/> <c>CellBuilder</c>,
    /// capturing the discount's index. Each row exposes a parallel <c>DiscountFlags</c> collection, so
    /// the box binds <c>DiscountFlags[index].IsSelected</c> — mirroring how per-day <c>Days[i]</c> cells
    /// are bound. The FSOU-member discount column passes <paramref name="enabled"/> = false so its box
    /// reads as auto-applied (it follows «Член ФСОУ» rather than being clicked).
    /// </summary>
    public static CheckBox BuildDiscountFlag(int index, bool enabled)
    {
        var box = new CheckBox
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = enabled,
            [!ToggleButton.IsCheckedProperty] =
                new Binding($"DiscountFlags[{index}].{nameof(DiscountFlagViewModel.IsSelected)}") { Mode = BindingMode.TwoWay },
        };
        return box;
    }

    // A chip cell as a LazyTextCell: digits only, commits on lost focus, and (when a rental registry is
    // supplied) bold-reds a non-rental number on both the resting label and the editor. Toggling rental
    // status is the table's right-click menu (the chip column is RentalChipColumn), not the cell.
    // Callers may still bind IsEnabled/IsVisible/Opacity on the returned cell.
    private LazyTextCell BuildChipEditor(string pathPrefix, string? chipPath = null, bool highlight = false)
    {
        var path = chipPath ?? $"{pathPrefix}{nameof(RosterDayCellViewModel.Chip)}";
        return new LazyTextCell(path, path, new SheetTextOptions
        {
            // A chip is a SportIdent card number kept as a string — restrict input to digits only.
            Mask = SheetColumnBuilder.NumericMask.Digits,
            CommitOnLostFocus = true,
            RentalChips = highlight ? _rentalChips : null,
        });
    }

    // A read-only money label bound to the row's computed total entry fee. Both row VMs expose
    // FormattedTotalFee with this name. Uses the standard foreground (not muted) so the total reads as
    // real data rather than a placeholder. Stretches to fill the cell with right-aligned text so the
    // hover tooltip — a localized breakdown of where the sum came from (FeeBreakdown) — fires anywhere
    // in the cell, not only over the glyphs.
    private static TextBlock BuildTotalFee() => new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        TextAlignment = TextAlignment.Right,
        Padding = new Thickness(10, 0),
        [!TextBlock.TextProperty] = new Binding(nameof(ParticipantRosterRowViewModel.FormattedTotalFee)),
        [!ToolTip.TipProperty] = new Binding(nameof(ParticipantRosterRowViewModel.FeeBreakdown)),
    };

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

    // ── Result columns (read-only text + status combo) ──────────────────────────────────────────
    // Built as Custom cells by the column builders. The day grid binds directly on the row (empty prefix);
    // the roster binds on Days[i] (prefix "Days[i].") and dims/disables on non-member days.

    /// <summary>A read-only result label bound to <paramref name="path"/> (e.g. "FinishText" on the row,
    /// or "Days[2].FinishText" on a roster cell). Centered, muted-free so it reads as real data. When
    /// <paramref name="tooltipPath"/> is non-empty the cell shows that bound string as a hover tooltip
    /// (the «Бали» column uses it for the per-control score breakdown).</summary>
    public static TextBlock BuildResultLabel(string path, string tooltipPath = "")
    {
        var label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextAlignment = TextAlignment.Center,
            Padding = new Thickness(8, 0),
            [!TextBlock.TextProperty] = new Binding(path),
        };
        if (!string.IsNullOrEmpty(tooltipPath))
            label[!ToolTip.TipProperty] = new Binding(tooltipPath);
        return label;
    }

    /// <summary>
    /// A finish-status combo bound to the given options/selection paths. The resting cell shows the
    /// effective status code via <paramref name="restingTextPath"/> (e.g. "OK"/"MP", blank when no
    /// result) — NOT the selected option — so an override-less row reads as its computed status rather
    /// than "(автоматично)". The dropdown (built on click) lists the descriptive auto sentinel + the
    /// settable statuses. Editing routes through the VM's status-change handler. Used by both tables.
    /// </summary>
    public LazyComboCell BuildStatusCombo(string optionsPath, string selectedPath, string restingTextPath, string restingDangerPath) => new(
        () => BuildComboCore<FinishStatusOption>(optionsPath, selectedPath, nameof(FinishStatusOption.Label)),
        restingTextPath,
        restingDangerPath);

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
