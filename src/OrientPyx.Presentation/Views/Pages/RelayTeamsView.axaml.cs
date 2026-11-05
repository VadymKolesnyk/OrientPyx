using Avalonia.Platform.Storage;
using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OrientPyx.Presentation.Controls;
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
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        // Ctrl+Click on a row's delete button skips the confirmation (same approach as the other grids).
        AddHandler(PointerPressedEvent, OnTunnelPointerPressed, RoutingStrategies.Tunnel);
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
        BuildBands();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    private void OnColumnsChanged(object? sender, EventArgs e) => BuildBands();

    // «Імпорт дистанцій етапів»: an IOF CourseData (TeamCourseAssignment) or a «100.2;B» table (Р-41).
    private async void OnImportCoursesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_vm is null || Avalonia.Controls.TopLevel.GetTopLevel(this) is not { } topLevel)
            return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = _vm.Localization.Get("RelayTeams.ImportCourses.Title"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType(_vm.Localization.Get("RelayTeams.ImportCourses.FileType"))
                {
                    Patterns = ["*.xml", "*.csv", "*.txt"]
                }
            ]
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;
        string content;
        try
        {
            content = await System.IO.File.ReadAllTextAsync(path);
        }
        catch (System.IO.IOException)
        {
            return;
        }
        await _vm.ImportLegCoursesAsync(content);
    }

    private void OnFocusGridRequested(object? sender, EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => Sheet.Focus());

    // Team fields are editable; status/time/place/legs are the computed result (read-only).
    private void BuildBands()
    {
        if (_vm is null)
            return;

        var builder = new SheetColumnBuilder(_vm.Localization)
            .Text("RelayTeams.Col.Number", nameof(RelayTeamRowViewModel.NumberText),
                  editPath: nameof(RelayTeamRowViewModel.NumberText), minWidth: 80,
                  mask: SheetColumnBuilder.NumericMask.Integer)
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
        }
        _vm = null;
    }
}
