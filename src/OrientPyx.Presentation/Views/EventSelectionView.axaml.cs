using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels;

namespace OrientPyx.Presentation.Views;

public partial class EventSelectionView : UserControl
{
    private EventSelectionViewModel? _vm;

    public EventSelectionView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    // Double-clicking a row opens that competition.
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is EventSelectionViewModel vm && vm.OpenCommand.CanExecute(null))
            vm.OpenCommand.Execute(null);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as EventSelectionViewModel;
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        BuildBands();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    // Builds the table's columns. Headers are baked into the band model, so a language switch is
    // handled by rebuilding. The trailing column holds a per-row hide/unhide button.
    private void BuildBands()
    {
        if (_vm is null)
            return;

        Sheet.Bands = new SheetColumnBuilder(_vm.Localization)
            .Text("EventSelection.Col.Identifier", nameof(EventSummaryRowViewModel.Identifier), minWidth: 140)
            .Text("EventSelection.Col.Name", nameof(EventSummaryRowViewModel.Name), minWidth: 160)
            .Text("EventSelection.Col.Venue", nameof(EventSummaryRowViewModel.Venue), minWidth: 200)
            .Text("EventSelection.Col.Dates", nameof(EventSummaryRowViewModel.DateRange), minWidth: 160)
            .Text("EventSelection.Col.Days", nameof(EventSummaryRowViewModel.DayCount), minWidth: 80)
            .Custom("EventSelection.Col.Hidden", BuildHideCell, width: 120, minWidth: 120)
            .Bands;
    }

    // A per-row toggle: "Hide" for a visible competition, "Show" for a hidden one. Bound to the VM's
    // command with the row as parameter; the label/tooltip switch on the row's IsHidden flag.
    private Control BuildHideCell()
    {
        var button = new Button
        {
            Classes = { "ghost", "small" },
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0),
            Command = _vm!.ToggleHiddenCommand,
            [!Button.CommandParameterProperty] = new Binding(),
            [!Button.ContentProperty] = new Binding(nameof(EventSummaryRowViewModel.IsHidden))
            {
                Converter = new HideLabelConverter(_vm.Localization)
            }
        };
        return button;
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
        _vm = null;
    }

    // Maps IsHidden → the button caption ("Показати" / "Приховати"), resolved through localization.
    private sealed class HideLabelConverter : Avalonia.Data.Converters.IValueConverter
    {
        private readonly Localization.ILocalizationService _loc;
        public HideLabelConverter(Localization.ILocalizationService loc) => _loc = loc;

        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => _loc.Get(value is true ? "EventSelection.Show" : "EventSelection.Hide");

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }
}
