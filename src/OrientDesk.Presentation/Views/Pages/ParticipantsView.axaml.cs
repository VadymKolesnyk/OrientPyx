using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
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

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => ApplyHeaders();

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

    // Rebuilds the roster grid's per-day group columns to match the current day set. Each cell binds
    // to the row's Days[i] (cells are in day order); a non-member cell is dimmed and its ✕ removes
    // the day membership.
    private void RebuildRosterDayColumns()
    {
        if (_vm is null)
            return;

        var loc = _vm.Localization;
        var columns = RosterSheet.Columns;

        // Drop any previously built per-day columns, keeping only the fixed identity columns.
        while (columns.Count > RosterFixedColumnCount)
            columns.RemoveAt(columns.Count - 1);

        // Build one column per competition day, in order. Driven by the VM's day set (not the rows),
        // so the columns appear even when the roster is empty or before any participant is added. The
        // cells bind to each row's Days[i], which the roster query keeps aligned to the same day order.
        var days = _vm.RosterDays;
        for (var i = 0; i < days.Count; i++)
        {
            var index = i; // capture for the template closure
            var dayNumber = days[i].Number;

            var column = new DataGridTemplateColumn
            {
                Header = $"{loc.Get("Header.Day")} {dayNumber}",
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
                MinWidth = 28,
                CanUserSort = false,
                CellTemplate = new FuncDataTemplate<ParticipantRosterRowViewModel>((_, _) => BuildRosterDayCell(index))
            };
            columns.Add(column);
        }
    }

    // Builds the cell content for a roster per-day column at a given day index: a group ComboBox whose
    // first entry ("не участвує") leaves the day. The cell dims when the participant is not a member.
    private Control BuildRosterDayCell(int dayIndex)
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            ItemTemplate = new FuncDataTemplate<GroupOption>((_, _) =>
                new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(GroupOption.Label)) })
        };
        combo[!ItemsControl.ItemsSourceProperty] = new Binding($"Days[{dayIndex}].{nameof(RosterDayCellViewModel.GroupOptions)}");
        combo[!SelectingItemsControl.SelectedItemProperty] =
            new Binding($"Days[{dayIndex}].{nameof(RosterDayCellViewModel.SelectedGroup)}") { Mode = BindingMode.TwoWay };

        // Dim the cell when the participant does not run that day.
        combo[!Visual.OpacityProperty] = new Binding($"Days[{dayIndex}].{nameof(RosterDayCellViewModel.IsMember)}")
        {
            Converter = DimWhenNotMember
        };

        return combo;
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
