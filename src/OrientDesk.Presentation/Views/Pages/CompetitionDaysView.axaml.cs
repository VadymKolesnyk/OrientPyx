using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Views.Pages;

public partial class CompetitionDaysView : UserControl
{
    private CompetitionDaysViewModel? _vm;

    public CompetitionDaysView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();

        // Capture the Ctrl modifier before the delete button consumes the press (it marks
        // PointerPressed handled), so Ctrl+Click on Delete can skip the confirmation prompt.
        AddHandler(PointerPressedEvent, OnTunnelPointerPressed, RoutingStrategies.Tunnel);
    }

    // Delete on the table deletes the selected day. Ctrl+Delete skips the confirmation. Ignored
    // while a cell editor (TextBox) has focus, so Delete still edits text inside a cell.
    private void OnSheetKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Key != Key.Delete)
            return;

        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
            return;

        var skipConfirm = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        e.Handled = true;
        _ = _vm.DeleteSelectedDayAsync(skipConfirm);
    }

    // The per-row delete button. Button.Click doesn't carry key modifiers, and the button marks its
    // own PointerPressed handled — so we capture the Ctrl state in the tunnel phase. A plain click
    // confirms first; Ctrl+Click deletes immediately.
    private bool _deleteCtrlDown;

    private void OnTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
        => _deleteCtrlDown = e.KeyModifiers.HasFlag(KeyModifiers.Control);

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Control { Tag: DayRowViewModel row })
            return;

        var skipConfirm = _deleteCtrlDown;
        _deleteCtrlDown = false;

        if (skipConfirm)
            _ = _vm.DeleteDayNoConfirmAsync(row);
        else
            _ = _vm.DeleteDayCommand.ExecuteAsync(row);
    }

    // DataGrid column headers live outside the visual tree, so they can't bind to
    // Localization[...] like cells do. We resolve them here and re-apply on language change.
    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as CompetitionDaysViewModel;
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        ApplyHeaders();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => ApplyHeaders();

    private void ApplyHeaders()
    {
        if (_vm is null)
            return;

        var loc = _vm.Localization;
        var columns = Sheet.Columns;
        columns[0].Header = loc.Get("CompetitionDays.Col.Day");
        columns[1].Header = loc.Get("CompetitionDays.Col.Date");
        columns[2].Header = loc.Get("CompetitionDays.Col.Venue");
        columns[3].Header = loc.Get("CompetitionDays.Col.Discipline");
        columns[4].Header = loc.Get("CompetitionDays.Col.Actions");
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
        _vm = null;
    }
}
