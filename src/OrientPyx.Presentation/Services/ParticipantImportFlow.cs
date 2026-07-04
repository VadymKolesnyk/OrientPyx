using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Default <see cref="IParticipantImportFlow"/>. Parses the UOF file off the UI thread, shows the
/// shared options modal with a single "clear existing participants first" toggle, then imports under
/// the busy overlay. Mirrors <see cref="XmlImportFlow"/>.
/// </summary>
public sealed class ParticipantImportFlow : IParticipantImportFlow
{
    // Stable toggle key shared with the modal result.
    private const string ClearParticipantsKey = "clearParticipants";

    private readonly ILocalizationService _localization;
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IUofXmlParser _parser;
    private readonly IDialogService _dialogs;
    private readonly IBusyService _busy;

    public ParticipantImportFlow(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IUofXmlParser parser,
        IDialogService dialogs,
        IBusyService busy)
    {
        _localization = localization;
        _editor = editor;
        _session = session;
        _parser = parser;
        _dialogs = dialogs;
        _busy = busy;
    }

    public async Task<bool> RunAsync(string xml)
    {
        if (_session.CurrentEvent is null || string.IsNullOrWhiteSpace(xml))
            return false;

        // Parse up front (off the UI thread) so a malformed file is reported before the options modal.
        var outcome = await _busy.RunAsync(() => Task.FromResult(Parse(xml)));
        if (!outcome.Success)
        {
            await _dialogs.ShowImportOptionsAsync(new ImportOptionsViewModel(
                _localization,
                titleKey: "ParticipantsImport.Title",
                messageKey: "ParticipantsImport.Error",
                options: []));
            return false;
        }

        // Ask whether to wipe the participant database first (default off — keep + FOU-code merge).
        var dialog = new ImportOptionsViewModel(
            _localization,
            titleKey: "ParticipantsImport.Title",
            messageKey: "ParticipantsImport.Message",
            options:
            [
                new ImportOption(ClearParticipantsKey, "ParticipantsImport.ClearFirst", isChecked: false)
            ]);

        var result = await _dialogs.ShowImportOptionsAsync(dialog);
        if (result is null)
            return false; // cancelled

        var clearFirst = result.Get(ClearParticipantsKey, fallback: false);
        var data = outcome.Data!;

        await _busy.RunAsync(reporter =>
            _editor.ImportParticipantsAsync(data, clearFirst, new ProgressRelay(this, reporter)));
        return true;
    }

    private string Format(string key, params object[] args)
        => string.Format(_localization.Get(key), args);

    /// <summary>
    /// Turns the import's layer-neutral steps into localized overlay lines. We invoke the reporter
    /// synchronously (not via <see cref="Progress{T}"/>) so ordering is preserved — the import calls
    /// us on its single worker thread and the reporter hops to the UI thread itself. The first
    /// per-row tick opens its own line; later ticks replace it in place so the counter doesn't spam
    /// a line per participant.
    /// </summary>
    private sealed class ProgressRelay : IProgress<ImportProgress>
    {
        private readonly ParticipantImportFlow _flow;
        private readonly IProgressReporter _reporter;
        private bool _counterStarted;

        public ProgressRelay(ParticipantImportFlow flow, IProgressReporter reporter)
        {
            _flow = flow;
            _reporter = reporter;
        }

        public void Report(ImportProgress p)
        {
            switch (p.Stage)
            {
                case ImportStage.Parsed:
                    _reporter.Report(_flow.Format("ParticipantsImport.Progress.Found", p.Total));
                    break;
                case ImportStage.DaysCreated:
                    _reporter.Report(_flow.Format("ParticipantsImport.Progress.DaysCreated", p.Current));
                    break;
                case ImportStage.Cleared:
                    _reporter.Report(_flow._localization.Get("ParticipantsImport.Progress.Cleared"));
                    break;
                case ImportStage.ResolvingLookups:
                    _reporter.Report(_flow._localization.Get("ParticipantsImport.Progress.Lookups"));
                    break;
                case ImportStage.Participants:
                    var line = _flow.Format("ParticipantsImport.Progress.Importing", p.Current, p.Total);
                    if (_counterStarted)
                    {
                        _reporter.ReportReplace(line);
                    }
                    else
                    {
                        _reporter.Report(line);
                        _counterStarted = true;
                    }
                    break;
                case ImportStage.Done:
                    _reporter.Report(_flow._localization.Get("ParticipantsImport.Progress.Done"));
                    break;
            }
        }
    }

    // Runs the synchronous parser and wraps success/failure so it can be marshalled off the UI thread.
    private ParseOutcome Parse(string xml)
    {
        try
        {
            return ParseOutcome.Ok(_parser.Parse(xml));
        }
        catch (UofXmlFormatException ex)
        {
            return ParseOutcome.Failed(ex.Message);
        }
    }

    private readonly struct ParseOutcome
    {
        private ParseOutcome(bool success, UofParticipantData? data, string? error)
        {
            Success = success;
            Data = data;
            Error = error;
        }

        public bool Success { get; }
        public UofParticipantData? Data { get; }
        public string? Error { get; }

        public static ParseOutcome Ok(UofParticipantData data) => new(true, data, null);
        public static ParseOutcome Failed(string error) => new(false, null, error);
    }
}
