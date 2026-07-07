using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Dialogs;

public partial class QuickWithdrawalView : UserControl
{
    private QuickWithdrawalViewModel? _vm;

    public QuickWithdrawalView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    // Escape cancels, matching the other dialogs.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is QuickWithdrawalViewModel vm)
        {
            vm.CancelCommand.Execute(null);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    // The table raises this on a keyboard Delete; remove the row (never the trailing spare).
    private void OnDeleteRequested(object? sender, SheetDeleteEventArgs e)
        => _vm?.RemoveRow(e.Row);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as QuickWithdrawalViewModel;
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        BuildBands();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    // Builds the table's columns: editable number (digits), editable status (combo of FinishStatusOption),
    // read-only surname (auto-filled from the number), and a trailing delete action. Headers are baked into
    // the band model, so a language switch is handled by rebuilding.
    private void BuildBands()
    {
        if (_vm is null)
            return;

        Sheet.Bands = new SheetColumnBuilder(_vm.Localization)
            .Text("Participants.QuickWithdrawal.Number",
                  nameof(QuickWithdrawalRowViewModel.Number),
                  editPath: nameof(QuickWithdrawalRowViewModel.Number),
                  width: 110,
                  mask: SheetColumnBuilder.NumericMask.Digits)
            .Combo("Participants.QuickWithdrawal.Status",
                   itemsPath: nameof(QuickWithdrawalRowViewModel.Statuses),
                   selectedPath: nameof(QuickWithdrawalRowViewModel.SelectedStatus),
                   labelPath: nameof(FinishStatusOption.Label),
                   width: 170)
            // Read-only: the surname resolved from the number (no editPath).
            .Text("Participants.QuickWithdrawal.Surname",
                  nameof(QuickWithdrawalRowViewModel.FullName),
                  minWidth: 220)
            .DeleteAction(OnDeleteButton, "Common.Delete")
            .Bands;
    }

    private void OnDeleteButton(object row) => _vm?.RemoveRow(row);

    private void Unsubscribe()
    {
        if (_vm is not null)
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
        _vm = null;
    }
}
