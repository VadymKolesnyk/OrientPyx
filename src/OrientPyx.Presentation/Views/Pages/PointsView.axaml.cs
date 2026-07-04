using System;
using System.ComponentModel;
using Avalonia.Controls;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class PointsView : UserControl
{
    private PointsViewModel? _vm;
    private TextBox? _formulaBox;

    public PointsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as PointsViewModel;
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;

        WireFormulaBox();
        BuildTableBands();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildTableBands();

    // When the page programmatically moves the formula caret (after a variable insert), push it onto the box.
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PointsViewModel.FormulaCaret) && _formulaBox is not null && _vm is not null)
        {
            var caret = Math.Clamp(_vm.FormulaCaret, 0, (_formulaBox.Text ?? string.Empty).Length);
            if (_formulaBox.CaretIndex != caret)
                _formulaBox.CaretIndex = caret;
        }
    }

    private void WireFormulaBox()
    {
        _formulaBox = this.FindControl<TextBox>("FormulaBox");
        if (_formulaBox is null)
            return;

        // Keep the VM's caret in sync so a palette insert lands at the cursor, not the end.
        _formulaBox.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.CaretIndexProperty && _vm is not null)
                _vm.FormulaCaret = _formulaBox.CaretIndex;
        };
    }

    // Place→points columns: place is read-only, points are editable (decimal). Headers are baked into
    // the band model, so a language switch is handled by rebuilding.
    private void BuildTableBands()
    {
        if (_vm is null)
            return;

        TableSheet.Bands = new SheetColumnBuilder(_vm.Localization)
            .Text("Points.Table.Col.Place", nameof(PointsTableRowViewModel.Place), minWidth: 100)
            .Text("Points.Table.Col.Points", nameof(PointsTableRowViewModel.PointsText),
                  editPath: nameof(PointsTableRowViewModel.PointsText), minWidth: 140,
                  mask: SheetColumnBuilder.NumericMask.Decimal)
            .Bands;
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
        {
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = null;
        _formulaBox = null;
    }
}
