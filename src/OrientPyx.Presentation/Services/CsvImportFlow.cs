using System.Globalization;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Default <see cref="ICsvImportFlow"/>. Reads the file's header off the UI thread, shows the
/// column-mapping modal (field → source column, with auto-guess + a clear-first toggle), turns the
/// mapped rows into the same <see cref="UofParticipantData"/> the XML import produces, then runs the
/// shared import under the busy overlay. A tabular file has no per-day info, so every imported athlete
/// is put on all existing days. Mirrors <see cref="ParticipantImportFlow"/>, adding the mapping step.
/// Handles both CSV text and .xlsx workbooks — only the parse step differs.
/// </summary>
public sealed class CsvImportFlow : ICsvImportFlow
{
    private readonly ILocalizationService _localization;
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly ICsvParser _parser;
    private readonly ISpreadsheetParser _spreadsheetParser;
    private readonly IDialogService _dialogs;
    private readonly IBusyService _busy;

    public CsvImportFlow(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        ICsvParser parser,
        ISpreadsheetParser spreadsheetParser,
        IDialogService dialogs,
        IBusyService busy)
    {
        _localization = localization;
        _editor = editor;
        _session = session;
        _parser = parser;
        _spreadsheetParser = spreadsheetParser;
        _dialogs = dialogs;
        _busy = busy;
    }

    public async Task<bool> RunAsync(string csv)
    {
        if (_session.CurrentEvent is null || string.IsNullOrWhiteSpace(csv))
            return false;

        // Parse up front (off the UI thread) so a malformed file is reported before the mapping modal.
        var outcome = await _busy.RunAsync(() => Task.FromResult(ParseCsv(csv)));
        return await ImportParsedAsync(outcome);
    }

    public async Task<bool> RunXlsxAsync(byte[] bytes)
    {
        if (_session.CurrentEvent is null || bytes is null || bytes.Length == 0)
            return false;

        var outcome = await _busy.RunAsync(() => Task.FromResult(ParseXlsx(bytes)));
        return await ImportParsedAsync(outcome);
    }

    // Shared tail for both entry points: validate the parse, show the mapping modal, build the data and
    // run the import. Every imported athlete is added to all existing days.
    private async Task<bool> ImportParsedAsync(ParseOutcome outcome)
    {
        if (!outcome.Success || outcome.Data!.Rows.Count == 0)
        {
            await ShowErrorAsync("CsvImport.Error");
            return false;
        }

        // A tabular file carries no day info; participants are added to all existing days, so at least one must exist.
        var days = await _busy.RunAsync(() => _editor.GetDaysAsync());
        if (days.Count == 0)
        {
            await ShowErrorAsync("CsvImport.NoDays");
            return false;
        }

        var file = outcome.Data!;
        var mapping = await _dialogs.ShowCsvMappingAsync(
            new CsvMappingViewModel(_localization, file.Header, file.Rows.Count));
        if (mapping is null)
            return false; // cancelled

        // A name is required to create a participant; without that column there is nothing to import.
        if (!mapping.Map.ContainsKey(CsvParticipantField.FullName))
        {
            await ShowErrorAsync("CsvImport.NoName");
            return false;
        }

        var data = BuildData(file, mapping.Map, days);
        if (data.Participants.Count == 0)
        {
            await ShowErrorAsync("CsvImport.NoRows");
            return false;
        }

        await _busy.RunAsync(reporter =>
            _editor.ImportParticipantsAsync(data, mapping.ClearFirst, new ProgressRelay(this, reporter)));
        return true;
    }

    // Turns the mapped CSV rows into participant data. Every athlete runs on all existing days.
    private static UofParticipantData BuildData(
        CsvParticipantData file,
        IReadOnlyDictionary<CsvParticipantField, int> map,
        IReadOnlyList<EventDay> days)
    {
        var dayNumbers = days.Select(d => d.Number).OrderBy(n => n).ToList();
        var participants = new List<UofParticipant>(file.Rows.Count);

        foreach (var row in file.Rows)
        {
            var name = Cell(row, map, CsvParticipantField.FullName);
            // Skip rows with no name — they can't become a participant.
            if (name.Length == 0)
                continue;

            var chip = Cell(row, map, CsvParticipantField.Chip);
            if (chip is "0")
                chip = string.Empty;

            participants.Add(new UofParticipant
            {
                FullName = name,
                Number = Cell(row, map, CsvParticipantField.Number),
                Team = Cell(row, map, CsvParticipantField.Team),
                BirthDate = ParseDate(Cell(row, map, CsvParticipantField.BirthDate)),
                Region = Cell(row, map, CsvParticipantField.Region),
                Club = Cell(row, map, CsvParticipantField.Club),
                Dussh = Cell(row, map, CsvParticipantField.Dussh),
                Group = Cell(row, map, CsvParticipantField.Group),
                Chip = chip,
                Rank = Cell(row, map, CsvParticipantField.Rank),
                Representative = Cell(row, map, CsvParticipantField.Representative),
                FsouCode = Cell(row, map, CsvParticipantField.FsouCode),
                IsFsouMember = ParseBool(Cell(row, map, CsvParticipantField.IsFsouMember)),
                Payment = Cell(row, map, CsvParticipantField.Payment),
                Coach = Cell(row, map, CsvParticipantField.Coach),
                DayNumbers = dayNumbers
            });
        }

        return new UofParticipantData { Participants = participants };
    }

    private static string Cell(
        IReadOnlyList<string> row,
        IReadOnlyDictionary<CsvParticipantField, int> map,
        CsvParticipantField field)
    {
        if (map.TryGetValue(field, out var i) && i >= 0 && i < row.Count)
            return row[i].Trim();
        return string.Empty;
    }

    // Treats common truthy spellings as a "yes"; everything else (incl. blank) is false.
    private static readonly string[] TruthyValues = ["1", "+", "так", "yes", "y", "true"];

    private static bool ParseBool(string value) =>
        TruthyValues.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    // Accepts the common date spellings (dd.MM.yyyy / yyyy-MM-dd / dd/MM/yyyy) and a bare year.
    private static DateTimeOffset? ParseDate(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return null;

        string[] formats = ["dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy"];
        if (DateTime.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return new DateTimeOffset(date, TimeSpan.Zero);

        // A bare 4-digit year → 1 January of that year (registration files sometimes carry only the year).
        if (trimmed.Length == 4 && int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            && year is >= 1900 and <= 2200)
            return new DateTimeOffset(new DateTime(year, 1, 1), TimeSpan.Zero);

        return null;
    }

    // Reuses the import-options modal as a plain "title + message + OK" error box (no toggles).
    private Task ShowErrorAsync(string messageKey) =>
        _dialogs.ShowImportOptionsAsync(new ImportOptionsViewModel(
            _localization,
            titleKey: "CsvImport.Title",
            messageKey: messageKey,
            options: []));

    private string Format(string key, params object[] args)
        => string.Format(_localization.Get(key), args);

    // Runs the synchronous CSV parser and wraps success/failure so it can be marshalled off the UI thread.
    private ParseOutcome ParseCsv(string csv)
    {
        try
        {
            return ParseOutcome.Ok(_parser.Parse(csv));
        }
        catch (CsvFormatException)
        {
            return ParseOutcome.Failed();
        }
    }

    // Same, for an .xlsx workbook's bytes.
    private ParseOutcome ParseXlsx(byte[] bytes)
    {
        try
        {
            return ParseOutcome.Ok(_spreadsheetParser.Parse(bytes));
        }
        catch (SpreadsheetFormatException)
        {
            return ParseOutcome.Failed();
        }
    }

    private readonly struct ParseOutcome
    {
        private ParseOutcome(bool success, CsvParticipantData? data)
        {
            Success = success;
            Data = data;
        }

        public bool Success { get; }
        public CsvParticipantData? Data { get; }

        public static ParseOutcome Ok(CsvParticipantData data) => new(true, data);
        public static ParseOutcome Failed() => new(false, null);
    }

    /// <summary>
    /// Turns the import's layer-neutral steps into localized overlay lines, reusing the participant
    /// import's progress keys. Identical in spirit to <see cref="ParticipantImportFlow"/>'s relay.
    /// </summary>
    private sealed class ProgressRelay : IProgress<ImportProgress>
    {
        private readonly CsvImportFlow _flow;
        private readonly IProgressReporter _reporter;
        private bool _counterStarted;

        public ProgressRelay(CsvImportFlow flow, IProgressReporter reporter)
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
}
