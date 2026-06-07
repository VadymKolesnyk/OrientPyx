using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

public sealed class CoursesViewModel : PageViewModelBase
{
    public CoursesViewModel(ILocalizationService localization) : base(localization)
    {
    }

    public override string NavKey => "Nav.Courses";
    public override string TitleKey => "Page.Courses.Title";
    public override string TextKey => "Page.Courses.Text";

    // Map / route pin.
    public override string IconData =>
        "M12,2 a7,7 0 0 0 -7,7 c0,5 7,12 7,12 s7,-7 7,-12 a7,7 0 0 0 -7,-7 z M12,11.5 a2.5,2.5 0 1 0 0,-5 a2.5,2.5 0 0 0 0,5 z";
}
