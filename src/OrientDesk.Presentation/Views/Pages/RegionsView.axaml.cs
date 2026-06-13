using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OrientDesk.Presentation.Controls;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Views.Pages;

public partial class RegionsView : UserControl
{
    private RegionsViewModel? _vm;

    public RegionsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();

        // Capture the Ctrl modifier before the delete button consumes the press, so Ctrl+Click on
        // Delete can skip the confirmation prompt (see ChipsView for the same approach).
        AddHandler(PointerPressedEvent, OnTunnelPointerPressed, RoutingStrategies.Tunnel);
    }

    // The table raises this on a keyboard Delete (Ctrl+Delete ⇒ skip the prompt).
    private void OnDeleteRequested(object? sender, SheetDeleteEventArgs e)
    {
        if (_vm is null || e.Row is not RegionRowViewModel row)
            return;
        DeleteRow(row, e.SkipConfirm);
    }

    private bool _deleteCtrlDown;

    private void OnTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
        => _deleteCtrlDown = e.KeyModifiers.HasFlag(KeyModifiers.Control);

    private void OnDeleteButton(object row)
    {
        if (_vm is null || row is not RegionRowViewModel region)
            return;
        var skipConfirm = _deleteCtrlDown;
        _deleteCtrlDown = false;
        DeleteRow(region, skipConfirm);
    }

    private void DeleteRow(RegionRowViewModel row, bool skipConfirm)
    {
        if (skipConfirm)
            _ = _vm!.DeleteRegionNoConfirmAsync(row);
        else
            _ = _vm!.DeleteRegionCommand.ExecuteAsync(row);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as RegionsViewModel;
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        _vm.FocusGridRequested += OnFocusGridRequested;
        BuildBands();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    // After the delete-confirmation modal closes, return focus to the grid (see ChipsView).
    private void OnFocusGridRequested(object? sender, EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => Sheet.Focus());

    // Builds the table's columns. Headers are baked into the band model, so a language switch is
    // handled by rebuilding. Name is editable; the participant count is read-only (no editPath).
    private void BuildBands()
    {
        if (_vm is null)
            return;

        Sheet.Bands = new SheetColumnBuilder(_vm.Localization)
            .Text("Regions.Col.Name", nameof(RegionRowViewModel.Name),
                  editPath: nameof(RegionRowViewModel.Name), minWidth: 240)
            .Text("Regions.Col.Count", nameof(RegionRowViewModel.ParticipantCount), minWidth: 160)
            .DeleteAction(OnDeleteButton, "Regions.Delete")
            .Bands;
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
        {
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
            _vm.FocusGridRequested -= OnFocusGridRequested;
        }
        _vm = null;
    }
}
