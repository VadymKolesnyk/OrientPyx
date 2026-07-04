using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class ClassicDrawView : UserControl
{
    private ClassicDrawViewModel? _vm;

    public ClassicDrawView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as ClassicDrawViewModel;
        if (_vm is null)
            return;

        // Headers are baked into the band model, so a language switch is handled by rebuilding.
        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        BuildBands();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    // Builds the columns for both tables: the editable group rows (checkbox + start/interval) and the
    // read-only drawn result rows.
    private void BuildBands()
    {
        if (_vm is null)
            return;

        var loc = _vm.Localization;

        GroupsSheet.Bands = new SheetColumnBuilder(loc)
            // "Take part" checkbox — a custom centred CheckBox bound on the row.
            .Custom("ClassicDraw.Col.Selected",
                    () => CheckBoxCell(nameof(ClassicDrawGroupRowViewModel.Selected)),
                    width: 60)
            .Text("Draw.Col.Group", nameof(ClassicDrawGroupRowViewModel.Name), minWidth: 160)
            .Text("ClassicDraw.Col.Control", nameof(ClassicDrawGroupRowViewModel.FirstControlLabel),
                  minWidth: 90, placeholder: string.Empty)
            .Text("ClassicDraw.Col.Count", nameof(ClassicDrawGroupRowViewModel.CountLabel), minWidth: 70)
            .Text("ClassicDraw.Col.Start", nameof(ClassicDrawGroupRowViewModel.Start),
                  editPath: nameof(ClassicDrawGroupRowViewModel.Start), minWidth: 110,
                  placeholder: "гг:хх:сс", mask: SheetColumnBuilder.NumericMask.Time)
            .Text("Draw.Interval", nameof(ClassicDrawGroupRowViewModel.Interval),
                  editPath: nameof(ClassicDrawGroupRowViewModel.Interval), minWidth: 110,
                  placeholder: "гг:хх:сс", mask: SheetColumnBuilder.NumericMask.Time)
            .Text("ClassicDraw.Col.FreeMinute", nameof(ClassicDrawGroupRowViewModel.FreeMinute),
                  minWidth: 120, placeholder: string.Empty)
            .Bands;

        ResultsSheet.Bands = new SheetColumnBuilder(loc)
            .Text("Draw.Col.StartTime", nameof(DrawResultRowViewModel.StartTimeLabel), minWidth: 100)
            .Text("Draw.Col.Number", nameof(DrawResultRowViewModel.Number), minWidth: 80)
            .Text("Draw.Col.Name", nameof(DrawResultRowViewModel.FullName), minWidth: 220)
            .Text("Draw.Col.Separation", nameof(DrawResultRowViewModel.SeparationValue),
                  minWidth: 150, placeholder: string.Empty)
            .Text("Draw.Col.Group", nameof(DrawResultRowViewModel.GroupName), minWidth: 120)
            .Bands;
    }

    // A centred CheckBox bound two-way on the row by the given property path.
    private static CheckBox CheckBoxCell(string path) => new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        [!ToggleButton.IsCheckedProperty] = new Binding(path) { Mode = BindingMode.TwoWay },
    };

    private void Unsubscribe()
    {
        if (_vm is not null)
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
        _vm = null;
    }
}
