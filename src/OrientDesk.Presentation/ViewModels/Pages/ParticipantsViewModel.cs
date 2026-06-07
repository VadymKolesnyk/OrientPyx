using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

public sealed class ParticipantsViewModel : PageViewModelBase
{
    public ParticipantsViewModel(ILocalizationService localization) : base(localization)
    {
    }

    public override string NavKey => "Nav.Participants";
    public override string TitleKey => "Page.Participants.Title";
    public override string TextKey => "Page.Participants.Text";

    // Single person.
    public override string IconData =>
        "M12,12 a4,4 0 1 0 0,-8 a4,4 0 0 0 0,8 z M4,21 a8,8 0 0 1 16,0 z";
}
