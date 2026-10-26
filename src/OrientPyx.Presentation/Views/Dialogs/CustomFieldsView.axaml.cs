using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Views.Dialogs;

public partial class CustomFieldsView : UserControl
{
    private CustomFieldsViewModel? _vm;

    public CustomFieldsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is CustomFieldsViewModel vm)
        {
            if (vm.HasPendingDelete)
                vm.CancelDeleteCommand.Execute(null);
            else
                vm.CloseCommand.Execute(null);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private void OnDeleteRequested(object? sender, SheetDeleteEventArgs e) => _vm?.RequestDelete(e.Row);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as CustomFieldsViewModel;
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        BuildBands();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    // Name (editable), type (combo), choice options with synonyms (editable for a choice field only), origin
    // (read-only) and the delete action. Headers are baked into the bands, so a language switch rebuilds them.
    private void BuildBands()
    {
        if (_vm is null)
            return;

        Sheet.Bands = new SheetColumnBuilder(_vm.Localization)
            .Text("CustomFields.Col.Label",
                  nameof(CustomFieldRowViewModel.Label),
                  editPath: nameof(CustomFieldRowViewModel.Label),
                  width: 200)
            .Combo("CustomFields.Col.Kind",
                   itemsPath: nameof(CustomFieldRowViewModel.KindOptions),
                   selectedPath: nameof(CustomFieldRowViewModel.SelectedKind),
                   labelPath: nameof(FieldKindOption.Label),
                   width: 130)
            .Text("CustomFields.Col.Options",
                  nameof(CustomFieldRowViewModel.OptionsText),
                  editPath: nameof(CustomFieldRowViewModel.OptionsText),
                  width: 280,
                  enabledPath: nameof(CustomFieldRowViewModel.IsChoice),
                  opacityPath: nameof(CustomFieldRowViewModel.IsChoice),
                  placeholder: _vm.Localization.Get("CustomFields.OptionsPlaceholder"))
            .Text("CustomFields.Col.Source",
                  nameof(CustomFieldRowViewModel.SourceText),
                  width: 150)
            .DeleteAction(row => _vm?.RequestDelete(row), "Common.Delete")
            .Bands;
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
        _vm = null;
    }
}
