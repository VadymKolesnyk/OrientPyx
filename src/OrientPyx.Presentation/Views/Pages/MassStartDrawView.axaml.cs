using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class MassStartDrawView : UserControl
{
    private MassStartDrawViewModel? _vm;

    public MassStartDrawView()
    {
        InitializeComponent();
        SheetEditing.Bind(this, GroupsSheet);
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as MassStartDrawViewModel;
        if (_vm is null)
            return;

        // Headers are baked into the band model, so a language switch — and a change of the restart columns
        // (another day, a relay with more legs) — is handled by rebuilding.
        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        _vm.PropertyChanged += OnViewModelChanged;
        BuildBands();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MassStartDrawViewModel.RestartLegs) or nameof(MassStartDrawViewModel.HasRelay))
            BuildBands();
    }

    private void BuildBands()
    {
        if (_vm is null)
            return;

        var loc = _vm.Localization;
        var timePlaceholder = loc.Get("Common.TimePlaceholder");

        var builder = new SheetColumnBuilder(loc)
            .Custom("MassStartDraw.Col.Selected",
                    () => CheckBoxCell(nameof(MassStartDrawGroupRowViewModel.Selected)),
                    width: 80)
            .Text("Draw.Col.Group", nameof(MassStartDrawGroupRowViewModel.Name), minWidth: 160)
            .Text("ClassicDraw.Col.Count", nameof(MassStartDrawGroupRowViewModel.CountLabel), minWidth: 70);
        if (_vm.HasRelay)
            builder.Text("MassStartDraw.Col.Legs", nameof(MassStartDrawGroupRowViewModel.LegCountLabel),
                minWidth: 70, placeholder: string.Empty);
        builder
            .Text("MassStartDraw.Col.Current", nameof(MassStartDrawGroupRowViewModel.CurrentStartLabel),
                  width: 170, placeholder: string.Empty)
            .Text("MassStartDraw.Col.Start", nameof(MassStartDrawGroupRowViewModel.Start),
                  editPath: nameof(MassStartDrawGroupRowViewModel.Start), minWidth: 110,
                  placeholder: timePlaceholder, mask: SheetColumnBuilder.NumericMask.Time);

        // One restart column per leg 2…N of the day's longest relay; a cell the row's group has no leg for is disabled.
        var restartHeader = loc.Get("MassStartDraw.Col.Restart");
        for (var i = 0; i < _vm.RestartLegs.Count; i++)
        {
            var path = $"{nameof(MassStartDrawGroupRowViewModel.Restarts)}[{i}].{nameof(MassStartRestartCellViewModel.Text)}";
            builder
                .Text("MassStartDraw.Col.Restart", path, editPath: path, width: 210,
                      placeholder: timePlaceholder, mask: SheetColumnBuilder.NumericMask.Time,
                      enabledPath: $"{nameof(MassStartDrawGroupRowViewModel.Restarts)}[{i}].{nameof(MassStartRestartCellViewModel.IsEnabled)}")
                .HeaderText(string.Format(restartHeader, _vm.RestartLegs[i]));
        }

        GroupsSheet.Bands = builder.Bands;
    }

    // A centred CheckBox bound two-way on the row by the given property path.
    private static CheckBox CheckBoxCell(string path)
    {
        var box = new CheckBox
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            [!ToggleButton.IsCheckedProperty] = new Binding(path) { Mode = BindingMode.TwoWay },
        };
        SheetLock.DisableWhenLocked(box);
        return box;
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
        {
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
            _vm.PropertyChanged -= OnViewModelChanged;
        }
        _vm = null;
    }

    // The table refused to enter edit because its day is closed: explain why and how to reopen it.
    private void OnLockedEditAttempted(object? sender, SheetLockedEditEventArgs e)
    {
        if (DataContext is PageViewModelBase vm)
            _ = vm.ExplainDayLockAsync(e.DayNumber, e.Merged);
    }
}
