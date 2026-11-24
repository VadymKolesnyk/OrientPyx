using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class DayReadinessView : UserControl
{
    // The level cell: red for an error, amber for a warning (the tints the cells of the other pages use).
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xDC, 0x26, 0x26));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xF5, 0x9E, 0x0B));

    private static readonly IValueConverter SeverityBrush =
        new FuncValueConverter<bool, IBrush?>(isError => isError ? ErrorBrush : WarningBrush);

    private DayReadinessViewModel? _vm;

    public DayReadinessView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        // F8 / the cell menu: «Перейти» for the selected row.
        Sheet.RowActionLabel = rows => _vm?.GoToActionLabel(rows);
        Sheet.RowActionRequested += (_, rows) => _vm?.GoTo(rows);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as DayReadinessViewModel;
        if (_vm is null)
            return;
        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        BuildBands();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    private void BuildBands()
    {
        if (_vm is null)
            return;

        Sheet.Bands = new SheetColumnBuilder(_vm.Localization)
            .Text("DayReadiness.Col.Severity", nameof(ReadinessRowViewModel.SeverityText), width: 130,
                  sortPath: nameof(ReadinessRowViewModel.SeveritySort))
            .CellTintByBrush(nameof(ReadinessRowViewModel.IsError), SeverityBrush)
            // Near the start, so the button stays on screen however long the problem text is.
            .Custom("DayReadiness.Col.GoTo", BuildGoToCell, width: 110)
            .Text("DayReadiness.Col.Number", nameof(ReadinessRowViewModel.Number), width: 90)
            .Text("DayReadiness.Col.Subject", nameof(ReadinessRowViewModel.Subject), minWidth: 180)
            .Text("DayReadiness.Col.Group", nameof(ReadinessRowViewModel.GroupName), minWidth: 90)
            // The text may be long: a fixed width, the whole of it on hover.
            .Text("DayReadiness.Col.Problem", nameof(ReadinessRowViewModel.Problem), width: 560,
                  toolTipPath: nameof(ReadinessRowViewModel.Problem))
            .Text("DayReadiness.Col.Where", nameof(ReadinessRowViewModel.Where), minWidth: 140)
            .Bands;
    }

    // «Перейти»: the page's command with the row as parameter (as the days grid's action buttons).
    private Control BuildGoToCell() => new Button
    {
        Classes = { "ghost", "small" },
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Command = _vm!.GoToCommand,
        Content = _vm.Localization.Get("DayReadiness.GoTo"),
        [!Button.CommandParameterProperty] = new Binding(),
        [ToolTip.TipProperty] = _vm.Localization.Get("DayReadiness.GoTo.Hint")
    };

    private void Unsubscribe()
    {
        if (_vm is not null)
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
        _vm = null;
    }
}
