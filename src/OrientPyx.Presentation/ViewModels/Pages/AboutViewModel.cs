using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// «Про програму» — static informational screen shown in a global overlay (like Settings, and
/// available with no competition open). Holds only display strings resolved from localization plus
/// the runtime app version and the external links; the View opens the links in the default browser.
/// </summary>
public sealed class AboutViewModel : PageViewModelBase
{
    public AboutViewModel(ILocalizationService localization)
        : base(localization)
    {
    }

    public override string NavKey => "Menu.Help.About";
    public override string TitleKey => "About.Title";
    public override string TextKey => "About.Subtitle";

    /// <summary>Assembly version (e.g. "0.1.1"); shown next to the app name.</summary>
    public string AppVersion =>
        typeof(AboutViewModel).Assembly.GetName().Version?.ToString(3) ?? "—";   // this assembly, not the host (a test runner)

    /// <summary>Public source repository.</summary>
    public string GitHubUrl => "https://github.com/VadymKolesnyk/OrientPyx";

    /// <summary>The user documentation site (GitHub Pages).</summary>
    public string DocsUrl => Services.DocsLinks.Site;

    /// <summary>Sergiy Sukharev's original «Orientir» program that inspired this one.</summary>
    public string OrientirUrl => "https://events.orienteering.org.ua/po.htm";

    /// <summary>Memorial page for the «Orientir» author (linked from his name in the tribute text).</summary>
    public string AuthorMemoryUrl => "https://dou.ua/lenta/news/serhiy-sukharev-died-in-sumy/";
}
