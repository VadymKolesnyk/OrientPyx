using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

public sealed class GroupsViewModel : PageViewModelBase
{
    public GroupsViewModel(ILocalizationService localization) : base(localization)
    {
    }

    public override string NavKey => "Nav.Groups";
    public override string TitleKey => "Page.Groups.Title";
    public override string TextKey => "Page.Groups.Text";

    // Group of people.
    public override string IconData =>
        "M8,11 a3,3 0 1 0 0,-6 a3,3 0 0 0 0,6 z M16,11 a3,3 0 1 0 0,-6 a3,3 0 0 0 0,6 z M2,20 a6,6 0 0 1 12,0 z M13,20 a6,6 0 0 1 9,-5.2 v5.2 z";
}
