namespace OrientPyx.BusinessLogic.Models;

/// <summary>A plugin report's settings for the page's auto-form: the definitions and their effective values.</summary>
public sealed record PluginReportSettingsData(
    string ReportId,
    string PluginId,
    IReadOnlyList<SettingDef> Definitions,
    IReadOnlyDictionary<string, object?> Values);

/// <summary>
/// The localized texts a plugin report's document needs from the program: the officials' captions and the page
/// footer (as the protocols print them), and <see cref="Text"/> — the localization lookup of an application key
/// (a plugin's string resolves as <c>"&lt;pluginId&gt;.&lt;key&gt;"</c>, falling back to the key itself). Supplied by
/// the Presentation layer so the build stays localization-free.
/// </summary>
public sealed record PluginReportLabels(
    string ChiefJudgeLabel,
    string ChiefSecretaryLabel,
    string JuryLabel,
    string FooterSoftwareName,
    string FooterGeneratedLabel,
    string FooterPageLabel,
    Func<string, string> Text);

/// <summary>A built plugin report: the document (shown on the page, written to Word), or why there is none.</summary>
/// <param name="Document">The document; null without a competition / day, or when the plugin failed.</param>
/// <param name="Error">The plugin's exception message when <see cref="Report.Build"/> failed; null otherwise.</param>
public sealed record PluginReportOutput(ResultProtocolDocument? Document, string? Error)
{
    public static PluginReportOutput None { get; } = new(null, null);
}
