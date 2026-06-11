using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;
using OrientDesk.Presentation.ViewModels.Dialogs;

namespace OrientDesk.Presentation.Services;

/// <summary>
/// Default <see cref="IXmlImportFlow"/>. Parses the file off the UI thread, shows the shared
/// two-toggle modal, then imports control points and groups for the current day under the busy
/// overlay. See <see cref="IXmlImportFlow"/> for the single-action rationale.
/// </summary>
public sealed class XmlImportFlow : IXmlImportFlow
{
    // Stable toggle keys shared with the modal result.
    private const string ReplaceControlPointsKey = "replaceControlPoints";
    private const string UpdateGroupsKey = "updateGroups";

    private readonly ILocalizationService _localization;
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IIofXmlParser _xmlParser;
    private readonly ICourseNameSplitter _splitter;
    private readonly IDialogService _dialogs;
    private readonly IBusyService _busy;

    public XmlImportFlow(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IIofXmlParser xmlParser,
        ICourseNameSplitter splitter,
        IDialogService dialogs,
        IBusyService busy)
    {
        _localization = localization;
        _editor = editor;
        _session = session;
        _xmlParser = xmlParser;
        _splitter = splitter;
        _dialogs = dialogs;
        _busy = busy;
    }

    public async Task<bool> RunAsync(string xml, string? fileName = null, byte[]? content = null)
    {
        if (_session.CurrentDay is null || string.IsNullOrWhiteSpace(xml))
            return false;

        // Parse up front (off the UI thread) so a malformed file is reported before the options modal.
        var outcome = await _busy.RunAsync(() => Task.FromResult(Parse(xml)));
        if (!outcome.Success)
        {
            await _dialogs.ShowImportOptionsAsync(new ImportOptionsViewModel(
                _localization,
                titleKey: "Import.Title",
                messageKey: "Import.Error",
                options: []));
            return false;
        }

        // Preprocessing: review/edit the groups each course splits into (e.g. "ЧЖ55" → "Ч55", "Ж55")
        // before importing. Returns the rewritten course data, or null if the user cancelled here.
        var split = await _dialogs.ShowSplitGroupsAsync(
            new SplitGroupsViewModel(_localization, _splitter, outcome.Data!));
        if (split is null)
            return false; // cancelled in the splitter

        var dialog = new ImportOptionsViewModel(
            _localization,
            titleKey: "Import.Title",
            messageKey: "Import.Message",
            options:
            [
                new ImportOption(ReplaceControlPointsKey, "Import.ReplaceControlPoints", isChecked: true),
                new ImportOption(UpdateGroupsKey, "Import.UpdateGroups", isChecked: true)
            ]);

        var result = await _dialogs.ShowImportOptionsAsync(dialog);
        if (result is null)
            return false; // cancelled

        var replaceControlPoints = result.Get(ReplaceControlPointsKey, fallback: true);
        var updateGroups = result.Get(UpdateGroupsKey, fallback: true);
        var data = split;

        // One file, one action: import control points first (so groups can compute distances from the
        // freshly saved points), then groups. Finally archive the original file into the day's folder
        // (skipped when no file was supplied, e.g. a non-file source).
        await _busy.RunAsync(async () =>
        {
            await _editor.ImportControlPointsAsync(data, replaceControlPoints);
            await _editor.ImportGroupsAsync(data, updateGroups);

            if (!string.IsNullOrEmpty(fileName) && content is not null)
                await _editor.SaveDayFileAsync(fileName, content);
        });
        return true;
    }

    // Runs the synchronous parser and wraps success/failure so it can be marshalled off the UI thread.
    private ParseOutcome Parse(string xml)
    {
        try
        {
            return ParseOutcome.Ok(_xmlParser.Parse(xml));
        }
        catch (IofXmlFormatException ex)
        {
            return ParseOutcome.Failed(ex.Message);
        }
    }

    private readonly struct ParseOutcome
    {
        private ParseOutcome(bool success, IofCourseData? data, string? error)
        {
            Success = success;
            Data = data;
            Error = error;
        }

        public bool Success { get; }
        public IofCourseData? Data { get; }
        public string? Error { get; }

        public static ParseOutcome Ok(IofCourseData data) => new(true, data, null);
        public static ParseOutcome Failed(string error) => new(false, null, error);
    }
}
