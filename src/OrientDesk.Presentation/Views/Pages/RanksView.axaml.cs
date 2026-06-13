using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OrientDesk.Presentation.Controls;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Views.Pages;

public partial class RanksView : UserControl
{
    private RanksViewModel? _vm;

    public RanksView()
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
        if (_vm is null || e.Row is not RankRowViewModel row)
            return;
        DeleteRow(row, e.SkipConfirm);
    }

    private bool _deleteCtrlDown;

    private void OnTunnelPointerPressed(object? sender, PointerPressedEventArgs e)
        => _deleteCtrlDown = e.KeyModifiers.HasFlag(KeyModifiers.Control);

    private void OnDeleteButton(object row)
    {
        if (_vm is null || row is not RankRowViewModel rank)
            return;
        var skipConfirm = _deleteCtrlDown;
        _deleteCtrlDown = false;
        DeleteRow(rank, skipConfirm);
    }

    private void DeleteRow(RankRowViewModel row, bool skipConfirm)
    {
        if (skipConfirm)
            _ = _vm!.DeleteRankNoConfirmAsync(row);
        else
            _ = _vm!.DeleteRankCommand.ExecuteAsync(row);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as RanksViewModel;
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

    // Builds the table's columns: an editable rank name and its points (decimal). Headers are baked
    // into the band model, so a language switch is handled by rebuilding.
    private void BuildBands()
    {
        if (_vm is null)
            return;

        Sheet.Bands = new SheetColumnBuilder(_vm.Localization)
            .Text("Ranks.Col.Name", nameof(RankRowViewModel.Name),
                  editPath: nameof(RankRowViewModel.Name), minWidth: 200)
            .Text("Ranks.Col.Points", nameof(RankRowViewModel.PointsText),
                  editPath: nameof(RankRowViewModel.PointsText), minWidth: 120,
                  mask: SheetColumnBuilder.NumericMask.Decimal)
            .DeleteAction(OnDeleteButton, "Ranks.Delete")
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
