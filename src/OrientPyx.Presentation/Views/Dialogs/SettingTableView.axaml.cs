using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Views.Dialogs;

public partial class SettingTableView : UserControl
{
    private SettingTableViewModel? _vm;

    public SettingTableView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is SettingTableViewModel vm)
        {
            vm.CancelCommand.Execute(null);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private void OnDeleteRequested(object? sender, SheetDeleteEventArgs e) => _vm?.DeleteRow(e.Row);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as SettingTableViewModel;
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        BuildBands();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    // One editable text cell per table column (a number column takes a decimal mask), then the delete action.
    private void BuildBands()
    {
        if (_vm is null)
            return;

        var builder = new SheetColumnBuilder(_vm.Localization);
        for (var i = 0; i < _vm.Columns.Count; i++)
        {
            var column = _vm.Columns[i];
            var path = $"{nameof(SettingTableRowViewModel.Cells)}[{i}].{nameof(SettingTableCellViewModel.Text)}";
            builder.Text(column.LabelKey, path, editPath: path, width: column.IsNumber ? 110 : 140,
                mask: column.IsNumber ? SheetColumnBuilder.NumericMask.Decimal : SheetColumnBuilder.NumericMask.None);
        }
        Sheet.Bands = builder.DeleteAction(row => _vm?.DeleteRow(row), "Common.Delete").Bands;
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
        _vm = null;
    }
}
