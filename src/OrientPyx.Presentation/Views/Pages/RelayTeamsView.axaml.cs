using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.Services.Undo;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class RelayTeamsView : UserControl
{
    private RelayTeamsViewModel? _vm;
    private bool _deleteCtrlDown;

    public RelayTeamsView()
    {
        InitializeComponent();
        SheetEditing.Bind(this, Sheet);
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            EndLegCourseSession();
            Unsubscribe();
        };
        // Ctrl+Click on a row's delete button skips the confirmation (same approach as the other grids).
        AddHandler(PointerPressedEvent, OnTunnelPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnPageKeyDown, RoutingStrategies.Tunnel);
        // Undo steps name a team by its number and name («Назва — 101 Азимут»).
        Sheet.UndoRowLabel = row => row is RelayTeamRowViewModel team
            ? RelayTeamsViewModel.TeamUndoName(team.NumberText, team.Name)
            : null;
        // A leg's course box (legs panel) is one undo edit session while it has focus, like a table cell: the whole
        // typed course is one step.
        AddHandler(GotFocusEvent, OnLegCourseGotFocus, RoutingStrategies.Bubble);
        AddHandler(LostFocusEvent, OnLegCourseLostFocus, RoutingStrategies.Bubble);
    }

    private UndoGesture.Session? _legCourseSession;

    private void OnLegCourseGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (_vm is null || e.Source is not TextBox { DataContext: RelayLegRowViewModel leg })
            return;
        EndLegCourseSession();
        // Nothing to end on undo: the box writes as it is typed in (its own Ctrl+Z works while it has focus).
        _legCourseSession = UndoGesture.OpenSession(_vm.LegCourseUndoLabel(leg), static () => { }, isText: true);
    }

    private void OnLegCourseLostFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is TextBox { DataContext: RelayLegRowViewModel })
            EndLegCourseSession();
    }

    private void EndLegCourseSession()
    {
        UndoGesture.CloseSession(_legCourseSession);
        _legCourseSession = null;
    }

    // Ctrl+B = «Присвоєння номерів», the same key as on «Учасники».
    private void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.B && e.KeyModifiers == KeyModifiers.Control && _vm is { HasRelayGroups: true })
        {
            _ = AssignNumbersAsync();
            e.Handled = true;
        }
    }

    private void OnAssignNumbersClick(object? sender, RoutedEventArgs e) => _ = AssignNumbersAsync();

    // The teams as the table shows them (filtered, sorted) are numbered in that order.
    private async System.Threading.Tasks.Task AssignNumbersAsync()
    {
        if (_vm is not null)
            await _vm.AssignNumbersCommand.ExecuteAsync(Sheet.VisibleItems);
    }

    private void OnTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
        => _deleteCtrlDown = e.KeyModifiers.HasFlag(KeyModifiers.Control);

    private void OnDeleteRequested(object? sender, SheetDeleteEventArgs e)
    {
        if (_vm is not null && e.Row is RelayTeamRowViewModel row)
            Delete(row, e.SkipConfirm);
    }

    private void OnDeleteButton(object row)
    {
        if (_vm is null || row is not RelayTeamRowViewModel team)
            return;
        var skipConfirm = _deleteCtrlDown;
        _deleteCtrlDown = false;
        Delete(team, skipConfirm);
    }

    private void Delete(RelayTeamRowViewModel row, bool skipConfirm)
    {
        if (skipConfirm)
            _ = _vm!.DeleteTeamNoConfirmAsync(row);
        else
            _ = _vm!.DeleteTeamCommand.ExecuteAsync(row);
    }

    private void OnLockedEditAttempted(object? sender, SheetLockedEditEventArgs e)
    {
        if (DataContext is PageViewModelBase vm)
            _ = vm.ExplainDayLockAsync(e.DayNumber, e.Merged);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as RelayTeamsViewModel;
        if (_vm is null)
            return;
        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        _vm.FocusGridRequested += OnFocusGridRequested;
        _vm.ColumnsChanged += OnColumnsChanged;
        _vm.RevealRequested += OnRevealRequested;
        BuildBands();
        ApplyPendingReveal();
    }

    private void OnRevealRequested(object? sender, EventArgs e) => ApplyPendingReveal();

    // «Перейти» from the day readiness check: the team's row (its legs and «без етапу» list show under the table).
    private void ApplyPendingReveal()
    {
        if (_vm is { } vm)
            SheetReveal.Apply(vm, Sheet, id => vm.Teams.FirstOrDefault(t => t.Id == id));
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    private void OnColumnsChanged(object? sender, EventArgs e) => BuildBands();

    private void OnFocusGridRequested(object? sender, EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => Sheet.Focus());

    // Team fields are editable; status/time/place/legs are the computed result (read-only).
    private void BuildBands()
    {
        if (_vm is null)
            return;

        // The number lands once, when its edit ends: a typed «105» is never «1» or «10» on the way (each of which
        // another team may hold), and the change is one undo step «Номер команди 101 → 105».
        var builder = new SheetColumnBuilder(_vm.Localization)
            .Text("RelayTeams.Col.Number", nameof(RelayTeamRowViewModel.NumberText),
                  editPath: nameof(RelayTeamRowViewModel.NumberText), minWidth: 80,
                  mask: SheetColumnBuilder.NumericMask.Integer,
                  sortPath: nameof(RelayTeamRowViewModel.NumberSort),
                  commitOnLostFocus: true)
            .CellTint(nameof(RelayTeamRowViewModel.NumberError), Avalonia.Media.Brushes.MistyRose)
            .Text("RelayTeams.Col.Name", nameof(RelayTeamRowViewModel.Name),
                  editPath: nameof(RelayTeamRowViewModel.Name), minWidth: 200)
            .Combo("RelayTeams.Col.Group",
                   nameof(RelayTeamRowViewModel.GroupOptions),
                   nameof(RelayTeamRowViewModel.SelectedGroup),
                   nameof(RelayGroupOption.Label), minWidth: 110)
            .Combo("RelayTeams.Col.Club",
                   nameof(RelayTeamRowViewModel.ClubOptions),
                   nameof(RelayTeamRowViewModel.SelectedClub),
                   nameof(ClubOption.Label), minWidth: 150)
            .Combo("RelayTeams.Col.Region",
                   nameof(RelayTeamRowViewModel.RegionOptions),
                   nameof(RelayTeamRowViewModel.SelectedRegion),
                   nameof(RegionOption.Label), minWidth: 150)
            .Check("RelayTeams.Col.OutOfCompetition", nameof(RelayTeamRowViewModel.OutOfCompetition));
        // «Набір на команду» (Р-41): the team's own course set, when some group uses team sets.
        if (_vm.ShowCourseSetColumn)
            builder.Text("RelayTeams.Col.CourseSet", nameof(RelayTeamRowViewModel.CourseSetText),
                  editPath: nameof(RelayTeamRowViewModel.CourseSetText), minWidth: 110,
                  placeholderPath: nameof(RelayTeamRowViewModel.CourseSetPlaceholder));
        Sheet.Bands = builder
            .Combo("RelayTeams.Col.StatusOverride",
                   nameof(RelayTeamRowViewModel.StatusOptions),
                   nameof(RelayTeamRowViewModel.SelectedStatus),
                   nameof(FinishStatusOption.Label), minWidth: 130)
            .Text("RelayTeams.Col.Legs", nameof(RelayTeamRowViewModel.LegsText), minWidth: 70)
            .CellTint(nameof(RelayTeamRowViewModel.HasUnplaced), Avalonia.Media.Brushes.MistyRose)
            .Text("RelayTeams.Col.Status", nameof(RelayTeamRowViewModel.StatusText), minWidth: 110)
            .Text("RelayTeams.Col.Time", nameof(RelayTeamRowViewModel.TimeText), minWidth: 90)
            .Text("RelayTeams.Col.Place", nameof(RelayTeamRowViewModel.PlaceText), minWidth: 70,
                  sortPath: nameof(RelayTeamRowViewModel.PlaceSort))
            .DeleteAction(OnDeleteButton, "RelayTeams.Delete")
            .Bands;
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
        {
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
            _vm.FocusGridRequested -= OnFocusGridRequested;
            _vm.ColumnsChanged -= OnColumnsChanged;
            _vm.RevealRequested -= OnRevealRequested;
        }
        _vm = null;
    }
}
