using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

public sealed class ResultsViewModel : PageViewModelBase
{
    public ResultsViewModel(ILocalizationService localization) : base(localization)
    {
    }

    public override string NavKey => "Nav.Results";
    public override string TitleKey => "Page.Results.Title";
    public override string TextKey => "Page.Results.Text";

    // Podium / bar chart.
    public override string IconData =>
        "M4,20 v-6 h4 v6 z M10,20 v-12 h4 v12 z M16,20 v-9 h4 v9 z";
}
