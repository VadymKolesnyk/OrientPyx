using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using OrientDesk.Presentation.Converters;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Views.Pages;

public partial class ParticipantsView : UserControl
{
    // Index of the first runtime-built per-day column in the roster grid (after the 6 identity
    // columns: surname, name, number, rank, coach, birth date).
    private const int RosterFixedColumnCount = 6;

    private static readonly BoolToOpacityConverter DimWhenNotMember = new();

    private ParticipantsViewModel? _vm;

    public ParticipantsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();

        // Capture the Ctrl modifier before the delete button consumes the press, so Ctrl+Click on
        // Delete can skip the confirmation prompt.
        AddHandler(PointerPressedEvent, OnTunnelPointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as ParticipantsViewModel;
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        _vm.ColumnsChanged += OnColumnsChanged;
        _vm.RosterColumnsChanged += OnRosterColumnsChanged;
        _vm.FocusGridRequested += OnFocusGridRequested;
        ApplyHeaders();
        ApplyColumnVisibility();
        // Build the roster's per-day columns from the VM's current day set, in case LoadAsync already
        // ran (and raised RosterColumnsChanged) before this view subscribed.
        RebuildRosterDayColumns();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        ApplyHeaders();
        // The roster's per-day/block headers and the "різні" label hold resolved strings; rebuild so
        // they re-localize.
        RebuildRosterDayColumns();
    }

    private void OnFocusGridRequested(object? sender, System.EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => DaySheet.Focus());

    private void OnColumnsChanged(object? sender, System.EventArgs e) => ApplyColumnVisibility();

    private void OnRosterColumnsChanged(object? sender, System.EventArgs e) => RebuildRosterDayColumns();

    // Delete on the day table deletes the selected participant. Ctrl+Delete skips the confirmation.
    private void OnDaySheetKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Key != Key.Delete)
            return;

        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
            return;

        var skipConfirm = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        e.Handled = true;
        _ = _vm.DeleteSelectedParticipantAsync(skipConfirm);
    }

    private bool _deleteCtrlDown;

    private void OnTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
        => _deleteCtrlDown = e.KeyModifiers.HasFlag(KeyModifiers.Control);

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Control { Tag: ParticipantDayRowViewModel row })
            return;

        var skipConfirm = _deleteCtrlDown;
        _deleteCtrlDown = false;

        if (skipConfirm)
            _ = _vm.DeleteParticipantNoConfirmAsync(row);
        else
            _ = _vm.DeleteParticipantCommand.ExecuteAsync(row);
    }

    // DataGrid column headers live outside the visual tree, so they can't bind. Resolve them here
    // and re-apply on language change.
    private void ApplyHeaders()
    {
        if (_vm is null)
            return;

        var loc = _vm.Localization;
        var day = DaySheet.Columns;
        day[0].Header = loc.Get("Participants.Col.Surname");
        day[1].Header = loc.Get("Participants.Col.Name");
        day[2].Header = loc.Get("Participants.Col.Number");
        day[3].Header = loc.Get("Participants.Col.Rank");
        day[4].Header = loc.Get("Participants.Col.Coach");
        day[5].Header = loc.Get("Participants.Col.BirthDate");
        day[6].Header = loc.Get("Participants.Col.Group");
        day[7].Header = loc.Get("Participants.Col.Chip");
        day[8].Header = loc.Get("Participants.Col.Team");
        day[9].Header = loc.Get("Participants.Col.Actions");

        var roster = RosterSheet.Columns;
        roster[0].Header = loc.Get("Participants.Col.Surname");
        roster[1].Header = loc.Get("Participants.Col.Name");
        roster[2].Header = loc.Get("Participants.Col.Number");
        roster[3].Header = loc.Get("Participants.Col.Rank");
        roster[4].Header = loc.Get("Participants.Col.Coach");
        roster[5].Header = loc.Get("Participants.Col.BirthDate");
        // Per-day columns set their own "Day N" headers when (re)built.
    }

    // Hide the team column unless the current day's discipline uses it (rogaine).
    private void ApplyColumnVisibility()
    {
        if (_vm is null)
            return;

        DaySheet.Columns[8].IsVisible = _vm.ShowTeamColumn;
    }

    // Rebuilds the roster grid's per-day field columns to match the current day set and the blocks'
    // collapse state. Each block (Groups, Chips, …) is either collapsed to one merged column or
    // expanded to one column per day. The block's leading column header hosts its collapse toggle.
    private void RebuildRosterDayColumns()
    {
        if (_vm is null)
            return;

        var loc = _vm.Localization;
        var columns = RosterSheet.Columns;

        // Drop any previously built per-day columns, keeping only the fixed identity columns.
        while (columns.Count > RosterFixedColumnCount)
            columns.RemoveAt(columns.Count - 1);

        var days = _vm.RosterDays;
        foreach (var block in _vm.Blocks)
        {
            if (block.IsCollapsed)
            {
                // One merged column for the whole block; its header is the toggle banner.
                columns.Add(NewDayColumn(
                    header: BuildBlockToggleHeader(block),
                    cell: () => BuildRosterCollapsedCell(block.Field)));
            }
            else
            {
                // One column per day, driven by the VM's day set (not the rows), so columns appear
                // even when the roster is empty. The leading column header is the toggle banner; the
                // rest are "День N". Cells bind to each row's Days[i] (kept in day order by the query).
                for (var i = 0; i < days.Count; i++)
                {
                    var index = i;     // capture for the template closure
                    var field = block.Field;
                    object header = i == 0
                        ? BuildBlockToggleHeader(block)
                        : $"{loc.Get("Header.Day")} {days[i].Number}";
                    columns.Add(NewDayColumn(header, () => BuildRosterDayCell(field, index)));
                }
            }
        }
    }

    // A per-day/field column with the shared sizing, and with sorting and reordering disabled —
    // per-day columns are never individually reorderable, so a block can't be split (block DnD TBD).
    private static DataGridTemplateColumn NewDayColumn(object header, Func<Control> cell) => new()
    {
        Header = header,
        Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
        MinWidth = 28,
        CanUserSort = false,
        CanUserReorder = false,
        CellTemplate = new FuncDataTemplate<ParticipantRosterRowViewModel>((_, _) => cell())
    };

    // Expanded per-day cell for a field at a given day index. Dims when the participant is not a
    // member of that day.
    private Control BuildRosterDayCell(RosterField field, int dayIndex)
    {
        Control content = field == RosterField.Groups
            ? BuildGroupCombo($"Days[{dayIndex}].")
            : BuildChipEditor($"Days[{dayIndex}].");

        // Dim the cell when the participant does not run that day.
        content[!Visual.OpacityProperty] = new Binding($"Days[{dayIndex}].{nameof(RosterDayCellViewModel.IsMember)}")
        {
            Converter = DimWhenNotMember
        };
        return content;
    }

    // Collapsed merged cell for a block: an editable input when the relevant days share one value,
    // a read-only "різні" label when they differ. Both states live in the cell, swapped by IsVisible,
    // so the cell stays live as values converge/diverge.
    private Control BuildRosterCollapsedCell(RosterField field)
    {
        var panel = new Panel();

        if (field == RosterField.Groups)
        {
            var combo = BuildGroupCombo(
                pathPrefix: string.Empty,
                groupOptionsPath: $"Days[0].{nameof(RosterDayCellViewModel.GroupOptions)}",
                selectedPath: nameof(ParticipantRosterRowViewModel.CollapsedGroupValue));
            combo[!Visual.IsVisibleProperty] = new Binding(nameof(ParticipantRosterRowViewModel.GroupShowsInput));
            panel.Children.Add(combo);

            var different = BuildDifferentLabel();
            different[!Visual.IsVisibleProperty] = new Binding(nameof(ParticipantRosterRowViewModel.GroupValuesDiffer));
            panel.Children.Add(different);
        }
        else
        {
            var editor = BuildChipEditor(
                pathPrefix: string.Empty,
                chipPath: nameof(ParticipantRosterRowViewModel.CollapsedChipValue));
            editor[!Visual.IsVisibleProperty] = new Binding(nameof(ParticipantRosterRowViewModel.ChipShowsInput));
            panel.Children.Add(editor);

            var different = BuildDifferentLabel();
            different[!Visual.IsVisibleProperty] = new Binding(nameof(ParticipantRosterRowViewModel.ChipShowsDifferent));
            panel.Children.Add(different);
            // A participant who runs no day has no chip at all: the cell stays empty (neither child shown).
        }

        return panel;
    }

    // A group ComboBox bound under the given binding-path prefix (e.g. "Days[2]." for an expanded
    // cell, or "" for the collapsed cell using row-level aggregate paths).
    private static ComboBox BuildGroupCombo(
        string pathPrefix,
        string? groupOptionsPath = null,
        string? selectedPath = null)
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
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

    // A chip text editor bound under the given binding path. The collapsed cell binds the row-level
    // aggregate (CollapsedChipValue); the expanded cell binds Days[i].Chip.
    private static TextBox BuildChipEditor(string pathPrefix, string? chipPath = null)
    {
        var box = new TextBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };
        box[!TextBox.TextProperty] =
            new Binding(chipPath ?? $"{pathPrefix}{nameof(RosterDayCellViewModel.Chip)}")
            { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus };
        return box;
    }

    // The greyed, read-only "різні" label shown in a collapsed cell whose relevant days differ.
    private TextBlock BuildDifferentLabel()
    {
        var label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 0),
            [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextMuted")
        };
        if (_vm is not null)
            label.Text = _vm.Localization.Get("Participants.Roster.Different");
        return label;
    }

    // The block's collapse/expand banner: a flat button (field name + chevron) placed in the block's
    // leading column header. Clicking flips the block's collapse state (the VM raises a rebuild).
    private Button BuildBlockToggleHeader(RosterFieldBlockViewModel block)
    {
        var loc = _vm!.Localization;
        var chevron = new PathIcon
        {
            Width = 10,
            Height = 10,
            // Right-pointing when collapsed, down-pointing when expanded.
            Data = Geometry.Parse(block.IsCollapsed ? "M6,4 L10,8 L6,12 Z" : "M4,6 L8,10 L12,6 Z")
        };
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = loc.Get(block.LabelKey), VerticalAlignment = VerticalAlignment.Center },
                chevron
            }
        };
        return new Button
        {
            Classes = { "ghost" },
            Padding = new Thickness(8, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = content,
            Command = _vm.ToggleBlockCommand,
            CommandParameter = block,
            [ToolTip.TipProperty] = loc.Get(block.IsCollapsed ? "Participants.Roster.Expand" : "Participants.Roster.Collapse")
        };
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
        {
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
            _vm.ColumnsChanged -= OnColumnsChanged;
            _vm.RosterColumnsChanged -= OnRosterColumnsChanged;
            _vm.FocusGridRequested -= OnFocusGridRequested;
        }
        _vm = null;
    }
}
