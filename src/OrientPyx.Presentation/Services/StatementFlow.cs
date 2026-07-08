using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Default <see cref="IStatementFlow"/>. Reads the on-screen (filtered + sorted) rows from the table's
/// <see cref="SheetTable.VisibleItems"/> — turning each into its participant id — and builds a human-readable
/// summary of the active filters + search, then fetches the statement data and opens the modal. All DB reads run
/// under the busy overlay. The modal (a <see cref="StatementViewModel"/>) owns the preview + export/print.
/// </summary>
public sealed class StatementFlow : IStatementFlow
{
    private readonly ILocalizationService _localization;
    private readonly ISessionService _session;
    private readonly ICompetitionEditorService _editor;
    private readonly IAppSettingsService _appSettings;
    private readonly IStatementBuilder _builder;
    private readonly IResultProtocolWriter _writer;
    private readonly IStatementPrintService _printService;
    private readonly IDialogService _dialogs;
    private readonly IBusyService _busy;

    public StatementFlow(
        ILocalizationService localization,
        ISessionService session,
        ICompetitionEditorService editor,
        IAppSettingsService appSettings,
        IStatementBuilder builder,
        IResultProtocolWriter writer,
        IStatementPrintService printService,
        IDialogService dialogs,
        IBusyService busy)
    {
        _localization = localization;
        _session = session;
        _editor = editor;
        _appSettings = appSettings;
        _builder = builder;
        _writer = writer;
        _printService = printService;
        _dialogs = dialogs;
        _busy = busy;
    }

    public async Task OpenAsync(SheetTable table, Guid? dayId)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (_session.CurrentEvent is null)
            return;

        var participantIds = CaptureParticipantIds(table);
        if (participantIds.Count == 0)
            return;

        var filterSummary = BuildFilterSummary(table);

        var (data, info) = await _busy.RunAsync(async () =>
        {
            var d = await _editor.GetStatementDataAsync(dayId, participantIds);
            var i = await _editor.GetInfoAsync();
            return (d, i);
        });

        var headerDefaults = ResolveHeaderDefaults(info, dayId);

        var vm = new StatementViewModel(
            _localization, _editor, _appSettings, _builder, _writer, _printService, _dialogs, _busy, _session,
            data, filterSummary, headerDefaults);
        await vm.LoadAsync();
        await _dialogs.ShowStatementAsync(vm);
    }

    public async Task PrintDirectAsync(SheetTable table, Guid? dayId)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (_session.CurrentEvent is null || !_printService.IsSupported)
            return;

        var participantIds = CaptureParticipantIds(table);
        if (participantIds.Count == 0)
            return;

        // The A4 printer must be known before we can print silently. If none is configured (or the saved one is
        // gone), open the picker once — after that the print goes straight through with no further prompts.
        var a4 = await _appSettings.GetA4PrintSettingsAsync();
        if (!a4.HasPrinter || !_printService.GetInstalledPrinters().Contains(a4.PrinterName))
        {
            var chosen = await _dialogs.ShowA4PrintSettingsAsync(
                new A4PrintSettingsViewModel(_localization, _appSettings, _printService, a4));
            if (!chosen)
                return;
            a4 = await _appSettings.GetA4PrintSettingsAsync();
            if (!a4.HasPrinter)
                return;
        }

        var filterSummary = BuildFilterSummary(table);

        await _busy.RunAsync(async () =>
        {
            // The saved statement template (per competition, seeded from the app default) — the same one the
            // modal would show; no header block, so the resolved defaults don't matter for the document.
            var settings = await _editor.GetStatementSettingsAsync() ?? await _appSettings.GetStatementSettingsAsync();
            var data = await _editor.GetStatementDataAsync(dayId, participantIds);
            var document = _builder.Build(data, settings, BuildLabels(), filterSummary);
            await _printService.PrintAsync(document, a4);
        });
    }

    // The participant ids of the shown rows, in on-screen (filtered + sorted) order. Both day-grid and roster
    // row VMs expose ParticipantId.
    private static List<Guid> CaptureParticipantIds(SheetTable table)
    {
        var ids = new List<Guid>();
        foreach (var item in table.VisibleItems)
        {
            switch (item)
            {
                case ParticipantDayRowViewModel d:
                    ids.Add(d.ParticipantId);
                    break;
                case ParticipantRosterRowViewModel r:
                    ids.Add(r.ParticipantId);
                    break;
            }
        }
        return ids;
    }

    // The localized labels the builder needs. Mirrors StatementViewModel.BuildLabels — the silent-print path
    // builds the document itself (no modal), so it resolves the same captions here.
    private StatementLabels BuildLabels()
    {
        var headers = new Dictionary<StatementColumn, string>();
        var shortHeaders = new Dictionary<StatementColumn, string>();
        foreach (StatementColumn column in Enum.GetValues<StatementColumn>())
        {
            headers[column] = _localization.Get(CaptionKey(column));
            if (ShortCaptionKey(column) is { } key)
                shortHeaders[column] = _localization.Get(key);
        }

        return new StatementLabels(
            DefaultTitle: _localization.Get("Statement.DefaultTitle"),
            ColumnHeaders: headers,
            ColumnHeadersShort: shortHeaders,
            StartDayHeaderTemplate: _localization.Get("Statement.Col.StartDay"),
            FooterSoftwareName: _localization.Get("Protocols.Footer.Software"),
            FooterGeneratedLabel: _localization.Get("Protocols.Footer.Generated"),
            FooterPageLabel: _localization.Get("Protocols.Footer.Page"));
    }

    private static string CaptionKey(StatementColumn column) => column switch
    {
        StatementColumn.Sequence => "Statement.Col.Sequence",
        StatementColumn.Number => "Statement.Col.Number",
        StatementColumn.FullName => "Statement.Col.FullName",
        StatementColumn.BirthDate => "Statement.Col.BirthDate",
        StatementColumn.Group => "Statement.Col.Group",
        StatementColumn.Chip => "Statement.Col.Chip",
        StatementColumn.Start => "Statement.Col.Start",
        StatementColumn.Region => "Statement.Col.Region",
        StatementColumn.Club => "Statement.Col.Club",
        StatementColumn.Dussh => "Statement.Col.Dussh",
        StatementColumn.Coach => "Statement.Col.Coach",
        StatementColumn.Rank => "Statement.Col.Rank",
        StatementColumn.Team => "Statement.Col.Team",
        StatementColumn.Representative => "Statement.Col.Representative",
        StatementColumn.FsouCode => "Statement.Col.FsouCode",
        StatementColumn.Note => "Statement.Col.Note",
        _ => "Statement.Col.FullName"
    };

    private static string? ShortCaptionKey(StatementColumn column) => column switch
    {
        StatementColumn.FullName => "Statement.Col.Short.FullName",
        StatementColumn.BirthDate => "Statement.Col.Short.BirthDate",
        StatementColumn.Coach => "Statement.Col.Short.Coach",
        StatementColumn.Rank => "Statement.Col.Short.Rank",
        StatementColumn.Representative => "Statement.Col.Short.Representative",
        _ => null
    };

    // The competition-metadata watermarks/fallbacks for the statement header. The date uses the scoped day when
    // one is set (day mode), else the competition start date.
    private StatementHeaderDefaults ResolveHeaderDefaults(CompetitionInfo? info, Guid? dayId)
    {
        var name = !string.IsNullOrWhiteSpace(info?.Name) ? info!.Name.Trim() : _session.CurrentEvent?.Name?.Trim() ?? string.Empty;
        var organisation = info?.Organisation?.Trim() ?? string.Empty;

        // Venue + date: the scoped day's own, else the competition's.
        var day = dayId is { } id ? _session.CurrentDay is { } cd && cd.Id == id ? cd : null : null;
        var venue = FirstNonBlank(day?.Venue, info?.Venue);
        var date = day?.Date ?? info?.StartDate;
        var dateText = date is { } dt ? dt.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

        return new StatementHeaderDefaults(name, organisation, venue, dateText);
    }

    private static string FirstNonBlank(params string?[] candidates)
    {
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c))
                return c!.Trim();
        return string.Empty;
    }

    // A single line summarising the applied filters + global search, e.g.
    // "Представник: Колесник Вадим · Регіон: Івано-Франківська · Пошук: «іван»". Value filters list their kept
    // values; condition filters show the condition text; the search term is appended last.
    private string BuildFilterSummary(SheetTable table)
    {
        var parts = new List<string>();
        foreach (var filter in table.ActiveFilters)
        {
            if (!filter.IsActive)
                continue;
            parts.Add(DescribeFilter(filter));
        }

        var search = table.GlobalSearch?.Trim() ?? string.Empty;
        if (search.Length > 0)
            parts.Add($"{_localization.Get("Statement.FilterSummary.Search")}: «{search}»");

        if (parts.Count == 0)
            return string.Empty;

        // No literal "Фільтри:" prefix — the values are shown on their own as a heading line under the title.
        return string.Join(" · ", parts);
    }

    // One filter as "<Header>: <values or condition>". Value/status filters list the kept values (joined with
    // ", "); a condition filter uses the SheetFilter's own localized description.
    private string DescribeFilter(SheetFilter filter)
    {
        var head = filter.Header.Length > 0 ? filter.Header : _localization.Get("Sheet.Filter.Column");

        if (filter.Mode == SheetFilterMode.Values && filter.AllowedValues is { } values)
        {
            var shown = values.Where(v => v.Length > 0).OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase);
            var joined = string.Join(", ", shown);
            // A value filter may keep the empty value (blank cell): note it explicitly.
            if (values.Any(v => v.Length == 0))
                joined = joined.Length > 0
                    ? $"{joined}, {_localization.Get("Statement.FilterSummary.Blank")}"
                    : _localization.Get("Statement.FilterSummary.Blank");
            return $"{head}: {joined}";
        }

        // Condition / status: reuse the filter's own localized description (already "<Header>: <cond> «text»").
        return filter.Describe(_localization);
    }
}
