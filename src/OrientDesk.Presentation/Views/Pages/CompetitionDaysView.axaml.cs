using System.ComponentModel;
using Avalonia.Controls;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Views.Pages;

public partial class CompetitionDaysView : UserControl
{
    private CompetitionDaysViewModel? _vm;

    public CompetitionDaysView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    // DataGrid column headers live outside the visual tree, so they can't bind to
    // Localization[...] like cells do. We resolve them here and re-apply on language change.
    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as CompetitionDaysViewModel;
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        ApplyHeaders();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => ApplyHeaders();

    private void ApplyHeaders()
    {
        if (_vm is null)
            return;

        var loc = _vm.Localization;
        var columns = Sheet.Columns;
        columns[0].Header = loc.Get("CompetitionDays.Col.Day");
        columns[1].Header = loc.Get("CompetitionDays.Col.Date");
        columns[2].Header = loc.Get("CompetitionDays.Col.Venue");
        columns[3].Header = loc.Get("CompetitionDays.Col.Discipline");
        columns[4].Header = loc.Get("CompetitionDays.Col.Actions");
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
        _vm = null;
    }
}
