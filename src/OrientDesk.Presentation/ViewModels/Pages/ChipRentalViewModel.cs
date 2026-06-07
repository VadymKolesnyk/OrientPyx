using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

public sealed class ChipRentalViewModel : PageViewModelBase
{
    public ChipRentalViewModel(ILocalizationService localization) : base(localization)
    {
    }

    public override string NavKey => "Nav.ChipRental";
    public override string TitleKey => "Page.ChipRental.Title";
    public override string TextKey => "Page.ChipRental.Text";

    // Tag / chip.
    public override string IconData =>
        "M3,12 l8,-8 h7 a1,1 0 0 1 1,1 v7 l-8,8 z M16.5,7.5 a1,1 0 1 0 0,0.01 z";
}
