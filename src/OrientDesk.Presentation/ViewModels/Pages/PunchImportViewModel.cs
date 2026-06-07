using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

public sealed class PunchImportViewModel : PageViewModelBase
{
    public PunchImportViewModel(ILocalizationService localization) : base(localization)
    {
    }

    public override string NavKey => "Nav.PunchImport";
    public override string TitleKey => "Page.PunchImport.Title";
    public override string TextKey => "Page.PunchImport.Text";

    // Download / import arrow into a tray.
    public override string IconData =>
        "M12,3 v9 M8,9 l4,4 l4,-4 M4,16 v3 a1,1 0 0 0 1,1 h14 a1,1 0 0 0 1,-1 v-3";
}
