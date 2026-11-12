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
        var scope = _session.CurrentDay is { } day ? new ImportScopeChoice(day.Number) : null;
        // The additional fields the import can fill (each a mapping target — a switched-on plugin's field the
        // competition lacks included) and the mapping last confirmed for a file with the same headers (the
        // competition's own, else the app default).
        var headerHash = ImportMapping.HeaderSetHash(file.Header);
        var (fields, templateJson, relay) = await _busy.RunAsync(async () =>
            (await _editor.GetImportFieldsAsync(), await _editor.GetImportMappingAsync(headerHash),
             await _editor.CurrentDayHasRelayAsync()));
        var template = templateJson is null ? null : ImportMapping.Parse(templateJson);
        var mapping = await _dialogs.ShowCsvMappingAsync(
            new CsvMappingViewModel(_localization, file.Header, file.Rows.Count, scope, fields, template, relay));
        if (mapping is null)
            return false; // cancelled

        // «+ Нове додаткове поле…»: create each new field (named after its column) and map it under its key.
        var map = new Dictionary<ImportTarget, int>(mapping.Map);
        foreach (var newField in mapping.NewFields)
        {
            var created = await _busy.RunAsync(() => _editor.AddCustomFieldAsync(newField.Label, CustomFieldKind.Text, []));
            if (created is not null)
                map[ImportTarget.Field(created.Key)] = newField.ColumnIndex;
        }

        // Remember this mapping for the next file with the same headers.
        var remembered = map
            .Where(kv => kv.Value >= 0 && kv.Value < file.Header.Count)
            .ToDictionary(kv => kv.Key.Key, kv => file.Header[kv.Value]);
        await _busy.RunAsync(() => _editor.SaveImportMappingAsync(headerHash, ImportMapping.Serialize(remembered)));

        // A name is required to create a participant; without that column there is nothing to import.
        if (!map.ContainsKey(ImportTarget.FullName))
        {
            await ShowErrorAsync("CsvImport.NoName");
            return false;
        }

        var data = BuildData(file, map, days, relay);
        if (data.Participants.Count == 0)
        {
            await ShowErrorAsync("CsvImport.NoRows");
            return false;
        }

        var importScope = await ImportDuplicatesStep.ResolveAsync(
            data, mapping.ClearFirst, mapping.Scope ?? ParticipantImportScope.AllDays,
            _editor, _dialogs, _busy, _localization);
        if (importScope is null)
            return false; // cancelled in the duplicates modal

        // All-days: confirm what the file adds / updates and which absent athletes to delete.
        var previewed = await ImportPreviewStep.ResolveAsync(
            data, mapping.ClearFirst, importScope, _editor, _dialogs, _busy, _localization);
        if (previewed is null)
            return false; // cancelled in the preview modal
        var (finalScope, deleteIds) = previewed.Value;

        var imported = await _busy.RunAsync(reporter =>
            _editor.ImportParticipantsAsync(data, mapping.ClearFirst, finalScope, new ProgressRelay(this, reporter)));
        await ImportPreviewStep.DeleteAsync(deleteIds, _editor, _busy);
        await RelayImportReport.ShowAsync(imported, data, _dialogs, _localization);
        return true;
    }

    // Turns the mapped CSV rows into participant data. Every athlete runs on all existing days. On a relay day the
    // team comes from «Номер команди» (+ «Етап»), or from a leg number «100.2» in the number column, which then
    // is not written as the participant's own number (Р-23).
    internal static UofParticipantData BuildData(
        CsvParticipantData file,
        IReadOnlyDictionary<ImportTarget, int> map,
        IReadOnlyList<EventDay> days,
        bool relay = false)
    {
        var dayNumbers = days.Select(d => d.Number).OrderBy(n => n).ToList();
        var participants = new List<UofParticipant>(file.Rows.Count);

        foreach (var row in file.Rows)
        {
            var name = Cell(row, map, ImportTarget.FullName);
            // Skip rows with no name — they can't become a participant.
            if (name.Length == 0)
                continue;

            var chip = Cell(row, map, ImportTarget.Chip);
            if (chip is "0")
                chip = string.Empty;

            var number = Cell(row, map, ImportTarget.Number);
            int? teamNumber = null;
            int? leg = int.TryParse(Cell(row, map, ImportTarget.RelayLeg), out var l) && l > 0 ? l : null;
            if (RelayBib.TryParse(Cell(row, map, ImportTarget.RelayTeamNumber), out var t, out var tl) && t > 0)
            {
                teamNumber = t;
                leg ??= tl;
            }
            else if (relay && RelayBib.TryParse(number, out t, out tl) && tl is not null && t > 0)
            {
                teamNumber = t;
                leg ??= tl;
                number = string.Empty;
            }

            participants.Add(new UofParticipant
            {
                FullName = name,
                Number = number,
                Team = Cell(row, map, ImportTarget.Team),
                BirthDate = ParseDate(Cell(row, map, ImportTarget.BirthDate)),
                Region = Cell(row, map, ImportTarget.Region),
                Club = Cell(row, map, ImportTarget.Club),
                Dussh = Cell(row, map, ImportTarget.Dussh),
                Group = Cell(row, map, ImportTarget.Group),
                Chip = chip,
                Rank = Cell(row, map, ImportTarget.Rank),
                Representative = Cell(row, map, ImportTarget.Representative),
                FsouCode = Cell(row, map, ImportTarget.FsouCode),
                IsFsouMember = ParseBool(Cell(row, map, ImportTarget.IsFsouMember)),
                Payment = Cell(row, map, ImportTarget.Payment),
                Coach = Cell(row, map, ImportTarget.Coach),
                DayNumbers = dayNumbers,
                Fields = FieldCells(row, map),
                RelayTeamNumber = teamNumber,
                RelayTeamName = Cell(row, map, ImportTarget.RelayTeamName),
                RelayLeg = teamNumber is null ? null : leg
            });
        }

        return new UofParticipantData { Participants = participants };
    }

    // The additional-field cells of a row, by field key (normalised by the import, Р-18/Р-19).
    private static IReadOnlyDictionary<string, string> FieldCells(
        IReadOnlyList<string> row,
        IReadOnlyDictionary<ImportTarget, int> map)
    {
        var fields = new Dictionary<string, string>();
        foreach (var (target, i) in map)
            if (target.FieldKey is { Length: > 0 } key && i >= 0 && i < row.Count)
                fields[key] = row[i].Trim();
        return fields;
    }

    private static string Cell(
        IReadOnlyList<string> row,
        IReadOnlyDictionary<ImportTarget, int> map,
        ImportTarget field)
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
