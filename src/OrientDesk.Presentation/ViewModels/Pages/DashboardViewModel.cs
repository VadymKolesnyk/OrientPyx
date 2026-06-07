using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

public sealed partial class DashboardViewModel : PageViewModelBase
{
    private readonly ICompetitionService _competitionService;

    [ObservableProperty]
    private DashboardInfo? _info;

    public DashboardViewModel(ILocalizationService localization, ICompetitionService competitionService)
        : base(localization)
    {
        _competitionService = competitionService;
        _ = LoadAsync();
    }

    public override string NavKey => "Nav.Dashboard";
    public override string TitleKey => "Page.Dashboard.Title";
    public override string TextKey => "Page.Dashboard.Text";

    // Dashboard tiles.
    public override string IconData =>
        "M4,4 h7 v7 h-7 z M13,4 h7 v4 h-7 z M13,10 h7 v10 h-7 z M4,13 h7 v7 h-7 z";

    private async Task LoadAsync()
    {
        Info = await _competitionService.GetDashboardInfoAsync();
    }
}
