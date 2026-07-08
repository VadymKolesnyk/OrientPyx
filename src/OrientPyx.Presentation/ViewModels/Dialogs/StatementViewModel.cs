using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// The participant-statement («відомість») modal: configures and exports/prints a flat participant list — the
/// currently-shown rows of the participants table — to Word (.docx) or A4. The template (orientation, the
/// ordered/visible column set, the header text) is stored <b>per competition</b> in the event database via
/// <see cref="ICompetitionEditorService"/>; a competition with no template is seeded from the app-level default
/// (via <see cref="IAppSettingsService"/>) and saved per competition thereafter. «Зберегти для наступних
/// змагань» writes the current layout as the app-level default.
///
/// The modal shows a live <see cref="ProtocolPreviewViewModel"/> (the same shared preview the protocols use),
/// filled with the captured rows and sorted by chip (rental first, then own, then chip number; own chips bold),
/// with the applied-filters line under the header. Built from the same <see cref="IStatementBuilder"/> the
/// export/print uses, so the preview matches the output. Reusing <see cref="ResultProtocolDocument"/> means the
/// Word export uses the results-protocol writer and the A4 print uses the statement print service.
/// </summary>
public sealed partial class StatementViewModel : ObservableObject, IProtocolPreviewHost
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly ICompetitionEditorService _editor;
    private readonly IAppSettingsService _appSettings;
    private readonly IStatementBuilder _builder;
    private readonly IResultProtocolWriter _writer;
    private readonly IStatementPrintService _printService;
    private readonly IDialogService _dialogs;
    private readonly IBusyService _busy;
    private readonly ISessionService _session;

    // The captured statement data (the currently-shown rows), the filter summary, and the header defaults.
    private readonly StatementData _data;
    private readonly StatementHeaderDefaults _headerDefaults;

    // How many participant rows the preview shows — enough to fill an A4 page; the page clips the rest.
    private const int PreviewRowCap = 40;

    // Suppresses per-field preview refreshes + auto-save while a template is being applied during load.
    private bool _applyingSettings;

    public StatementViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        IAppSettingsService appSettings,
        IStatementBuilder builder,
        IResultProtocolWriter writer,
        IStatementPrintService printService,
        IDialogService dialogs,
        IBusyService busy,
        ISessionService session,
        StatementData data,
        string filterSummary,
        StatementHeaderDefaults headerDefaults)
    {
        Localization = localization;
        _editor = editor;
        _appSettings = appSettings;
        _builder = builder;
        _writer = writer;
        _printService = printService;
        _dialogs = dialogs;
        _busy = busy;
        _session = session;
        _data = data;
        _headerDefaults = headerDefaults;

        Preview.FilterSummary = filterSummary;
        Preview.HasFilterSummary = filterSummary.Length > 0;
        _filterSummaryText = filterSummary;

        Localization.PropertyChanged += (_, _) => RaiseLabels();
    }

    public ILocalizationService Localization { get; }

    /// <summary>The live document preview shown in the modal. Rebuilt by <see cref="RefreshPreview"/>.</summary>
    public ProtocolPreviewViewModel Preview { get; } = new();

    /// <summary>Completes when the modal is closed (the result is unused — export/print happen via commands).</summary>
    public Task<bool> Completion => _completion.Task;

    // ── Localized chrome ─────────────────────────────────────────────────────────────────────────────

    public string Title => Localization.Get("Statement.Modal.Title");
    public string LandscapeLabel => Localization.Get("Statement.Layout.Landscape");
    public string ColumnsLabel => Localization.Get("Statement.Columns.Title");
    public string ExportWordLabel => Localization.Get("Statement.ExportWord");
    public string PrintLabel => Localization.Get("Statement.Print");
    public string PrintSettingsLabel => Localization.Get("Statement.PrintSettings");
    public string SaveForNextLabel => Localization.Get("Statement.SaveForNext");
    public string CloseLabel => Localization.Get("Common.Close");
    public string EmptyHint => Localization.Get("Statement.Preview.Empty");
    public string SettingsSavedHint => Localization.Get("Statement.SettingsSavedHint");

    private void RaiseLabels()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(LandscapeLabel));
        OnPropertyChanged(nameof(ColumnsLabel));
        OnPropertyChanged(nameof(ExportWordLabel));
        OnPropertyChanged(nameof(PrintLabel));
        OnPropertyChanged(nameof(PrintSettingsLabel));
        OnPropertyChanged(nameof(SaveForNextLabel));
        OnPropertyChanged(nameof(CloseLabel));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(SettingsSavedHint));
    }

    // ── Settings ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The configurable columns, in on-page order (checkbox toggle + drag-reorder via the preview).</summary>
    public ObservableCollection<StatementColumnItemViewModel> Columns { get; } = [];

    [ObservableProperty]
    private bool _isLandscape;

    [ObservableProperty]
    private string _competitionName = string.Empty;

    [ObservableProperty]
    private string _title2 = string.Empty; // "Title" is the localized chrome above; this is the header title field

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _venue = string.Empty;

    [ObservableProperty]
    private string _dateText = string.Empty;

    // Placeholders (watermarks): the resolved competition default for each blank-able header field.
    [ObservableProperty]
    private string _competitionNamePlaceholder = string.Empty;

    [ObservableProperty]
    private string _titlePlaceholder = string.Empty;

    [ObservableProperty]
    private string _subtitlePlaceholder = string.Empty;

    [ObservableProperty]
    private string _venuePlaceholder = string.Empty;

    [ObservableProperty]
    private string _dateTextPlaceholder = string.Empty;

    [ObservableProperty]
    private bool _settingsSaved;

    private string _filterSummaryText;

    /// <summary>Loads the competition template (seeded from the app default) + resolves the header placeholders,
    /// then renders the preview. Called once when the modal opens.</summary>
    public async Task LoadAsync()
    {
        var settings = await _busy.RunAsync(async () =>
            await _editor.GetStatementSettingsAsync() ?? await _appSettings.GetStatementSettingsAsync());

        ApplySettings(settings);
        ResolveHeaderPlaceholders();
        RefreshPreview();
    }

    private void ApplySettings(StatementSettings settings)
    {
        _applyingSettings = true;
        try
        {
            IsLandscape = settings.Orientation == ProtocolOrientation.Landscape;
            CompetitionName = settings.CompetitionName;
            Title2 = settings.Title;
            Subtitle = settings.Subtitle;
            Venue = settings.Venue;
            DateText = settings.DateText;
        }
        finally
        {
            _applyingSettings = false;
        }

        // Reconcile against the full column set: a template saved before a column existed is missing it, so
        // append any absent column (visible) in enum order.
        var present = settings.Columns.Select(c => c.Column).ToHashSet();
        foreach (StatementColumn column in Enum.GetValues<StatementColumn>())
            if (present.Add(column))
                settings.Columns.Add(new StatementColumnSetting { Column = column, Visible = true });

        Columns.Clear();
        foreach (var c in settings.Columns)
        {
            var item = new StatementColumnItemViewModel(c.Column, CaptionKey(c.Column), c.Visible, Localization);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(StatementColumnItemViewModel.Visible))
                {
                    RefreshPreview();
                    AutoSave();
                }
            };
            Columns.Add(item);
        }
    }

    private void ResolveHeaderPlaceholders()
    {
        CompetitionNamePlaceholder = _headerDefaults.CompetitionName;
        TitlePlaceholder = Localization.Get("Statement.DefaultTitle");
        // The statement deliberately omits the organisation/subtitle line — no fallback, so it stays blank.
        SubtitlePlaceholder = string.Empty;
        VenuePlaceholder = _headerDefaults.Venue;
        DateTextPlaceholder = _headerDefaults.DateText;
    }

    // ── Column reorder (drag from the preview header) ──────────────────────────────────────────────────

    public void MoveColumnByKey(string draggedKey, string targetKey, bool insertAfter)
    {
        if (!Enum.TryParse<StatementColumn>(draggedKey, out var dragged) ||
            !Enum.TryParse<StatementColumn>(targetKey, out var target))
            return;
        var from = IndexOf(dragged);
        var targetIndex = IndexOf(target);
        if (from < 0 || targetIndex < 0 || from == targetIndex)
            return;

        var insertIndex = insertAfter ? targetIndex + 1 : targetIndex;
        if (from < insertIndex)
            insertIndex--;
        insertIndex = Math.Clamp(insertIndex, 0, Columns.Count - 1);
        if (from == insertIndex)
            return;

        Columns.Move(from, insertIndex);
        RefreshPreview();
        AutoSave();
    }

    private int IndexOf(StatementColumn column)
    {
        for (var i = 0; i < Columns.Count; i++)
            if (Columns[i].Column == column)
                return i;
        return -1;
    }

    // ── Persistence ──────────────────────────────────────────────────────────────────────────────────

    // Auto-saves the current template to the competition on every change. Fire-and-forget off the UI thread.
    private void AutoSave()
    {
        if (_applyingSettings)
            return;
        SettingsSaved = false;
        var settings = BuildSettings();
        _ = Task.Run(async () =>
        {
            try
            {
                await _editor.SaveStatementSettingsAsync(settings);
            }
            catch
            {
                // Best-effort auto-save; the next edit (or an export) will persist again.
            }
        });
    }

    // "Save for next competitions": stores the current layout as the app-level default.
    [RelayCommand]
    private async Task SaveSettings()
    {
        var settings = BuildSettings();
        await _busy.RunAsync(() => _appSettings.SaveStatementSettingsAsync(settings));
        SettingsSaved = true;
    }

    // ── Preview ──────────────────────────────────────────────────────────────────────────────────────

    private void RefreshPreview()
    {
        var settings = BuildDocumentSettings();

        Preview.IsLandscape = settings.Orientation == ProtocolOrientation.Landscape;
        Preview.CompetitionName = settings.CompetitionName;
        Preview.Title = settings.Title.Length > 0 ? settings.Title : Localization.Get("Statement.DefaultTitle");
        Preview.Subtitle = settings.Subtitle;
        Preview.DateText = settings.DateText;
        Preview.Venue = settings.Venue;

        var document = _builder.Build(_data, settings, BuildLabels(), _filterSummaryText);

        Preview.Columns.Clear();
        // The PHYSICAL column keys, mirroring the builder's plan: each visible logical column is one physical
        // column except «Старт», which expands to one column per day (all keyed "Start" so a drag reorders the
        // whole block). Kept parallel to the document's expanded header/wrap/shrink lists.
        var keys = new List<string>();
        foreach (var c in settings.Columns.Where(c => c.Visible).Select(c => c.Column))
        {
            if (c == StatementColumn.Start)
                for (var d = 0; d < _data.DayLabels.Count; d++)
                    keys.Add(c.ToString());
            else
                keys.Add(c.ToString());
        }
        if (keys.Count == 0)
            keys.Add(StatementColumn.FullName.ToString());
        for (var i = 0; i < keys.Count && i < document.ColumnHeaders.Count; i++)
            Preview.Columns.Add(new ProtocolPreviewColumn(keys[i], document.ColumnHeaders[i],
                i < document.ColumnHeadersShort.Count ? document.ColumnHeadersShort[i] : string.Empty,
                i < document.ColumnBodyWrap.Count && document.ColumnBodyWrap[i],
                i < document.ColumnShrinkPriority.Count ? document.ColumnShrinkPriority[i] : 1));

        Preview.Sections.Clear();
        var section = document.Sections.Count > 0 ? document.Sections[0] : null;
        if (section is not null)
        {
            var rows = section.Rows.Take(PreviewRowCap)
                .Select(r => new ProtocolPreviewRow(r.Cells, r.IsTeamHeader, r.BoldCells))
                .ToList();
            Preview.Sections.Add(new ProtocolPreviewSection(string.Empty, string.Empty, rows));
        }
        Preview.IsEmpty = Preview.Sections.Count == 0 || Preview.Sections.All(s => s.Rows.Count == 0);

        Preview.Officials.Clear();
        Preview.HasOfficials = false;
    }

    // ── Build settings ───────────────────────────────────────────────────────────────────────────────

    // Persisted: the user's typed values (blanks stay blank; the watermark is a hint, never stored).
    private StatementSettings BuildSettings() => new()
    {
        Orientation = IsLandscape ? ProtocolOrientation.Landscape : ProtocolOrientation.Portrait,
        CompetitionName = CompetitionName?.Trim() ?? string.Empty,
        Title = Title2?.Trim() ?? string.Empty,
        Subtitle = Subtitle?.Trim() ?? string.Empty,
        Venue = Venue?.Trim() ?? string.Empty,
        DateText = DateText?.Trim() ?? string.Empty,
        Columns = Columns.Select(c => c.ToSetting()).ToList()
    };

    // Built (preview + export): blank header fields folded down to the resolved competition/day defaults.
    private StatementSettings BuildDocumentSettings()
    {
        var s = BuildSettings();
        if (s.CompetitionName.Length == 0) s.CompetitionName = CompetitionNamePlaceholder;
        if (s.Subtitle.Length == 0) s.Subtitle = SubtitlePlaceholder;
        if (s.Venue.Length == 0) s.Venue = VenuePlaceholder;
        if (s.DateText.Length == 0) s.DateText = DateTextPlaceholder;
        return s;
    }

    private StatementLabels BuildLabels()
    {
        var headers = new Dictionary<StatementColumn, string>();
        var shortHeaders = new Dictionary<StatementColumn, string>();
        foreach (StatementColumn column in Enum.GetValues<StatementColumn>())
        {
            headers[column] = Localization.Get(CaptionKey(column));
            if (ShortCaptionKey(column) is { } key)
                shortHeaders[column] = Localization.Get(key);
        }

        return new StatementLabels(
            DefaultTitle: Localization.Get("Statement.DefaultTitle"),
            ColumnHeaders: headers,
            ColumnHeadersShort: shortHeaders,
            StartDayHeaderTemplate: Localization.Get("Statement.Col.StartDay"),
            FooterSoftwareName: Localization.Get("Protocols.Footer.Software"),
            FooterGeneratedLabel: Localization.Get("Protocols.Footer.Generated"),
            FooterPageLabel: Localization.Get("Protocols.Footer.Page"));
    }

    // ── Export / print ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Builds the .docx bytes + a suggested file name for the current view (the View runs the save
    /// dialog). Also persists the current settings. Returns null when there is nothing to export.</summary>
    public async Task<StatementExportResult?> GenerateWordAsync()
    {
        var settings = BuildSettings();
        var documentSettings = BuildDocumentSettings();
        var labels = BuildLabels();

        var bytes = await _busy.RunAsync(async () =>
        {
            await _editor.SaveStatementSettingsAsync(settings);
            var document = _builder.Build(_data, documentSettings, labels, _filterSummaryText);
            return _writer.Write(document);
        });
        SettingsSaved = false;

        return new StatementExportResult(bytes, SuggestedFileName("docx"));
    }

    /// <summary>Prints the current view to the configured A4 printer. Opens the A4 print-settings modal first
    /// when none is configured (or the saved one is missing); a no-op if the user then cancels or printing is
    /// unsupported.</summary>
    [RelayCommand]
    private async Task Print()
    {
        if (!_printService.IsSupported)
            return;

        var a4 = await _appSettings.GetA4PrintSettingsAsync();
        if (!a4.HasPrinter || !_printService.GetInstalledPrinters().Contains(a4.PrinterName))
        {
            var chosen = await _dialogs.ShowA4PrintSettingsAsync(
                new A4PrintSettingsViewModel(Localization, _appSettings, _printService, a4));
            if (!chosen)
                return;
            a4 = await _appSettings.GetA4PrintSettingsAsync();
            if (!a4.HasPrinter)
                return;
        }

        var documentSettings = BuildDocumentSettings();
        var labels = BuildLabels();
        await _busy.RunAsync(async () =>
        {
            await _editor.SaveStatementSettingsAsync(BuildSettings());
            var document = _builder.Build(_data, documentSettings, labels, _filterSummaryText);
            await _printService.PrintAsync(document, a4);
        });
    }

    /// <summary>Opens the A4 print-settings modal from the «Параметри друку A4» affordance.</summary>
    [RelayCommand]
    private async Task EditPrintSettings()
    {
        var a4 = await _appSettings.GetA4PrintSettingsAsync();
        await _dialogs.ShowA4PrintSettingsAsync(
            new A4PrintSettingsViewModel(Localization, _appSettings, _printService, a4));
    }

    [RelayCommand]
    private void Close() => _completion.TrySetResult(true);

    // ── Header-edit hooks (mirror the protocols page) ──────────────────────────────────────────────────

    private void OnHeaderEdited()
    {
        if (_applyingSettings)
            return;
        AutoSave();
    }

    partial void OnIsLandscapeChanged(bool value)
    {
        if (_applyingSettings)
            return;
        Preview.IsLandscape = value;
        AutoSave();
    }

    partial void OnCompetitionNameChanged(string value) => OnHeaderEdited();
    partial void OnTitle2Changed(string value) => OnHeaderEdited();
    partial void OnSubtitleChanged(string value) => OnHeaderEdited();
    partial void OnVenueChanged(string value) => OnHeaderEdited();
    partial void OnDateTextChanged(string value) => OnHeaderEdited();

    // "<competition> — відомість <date>.docx", sanitised for the save dialog.
    private string SuggestedFileName(string extension)
    {
        var competition = _session.CurrentEvent?.Name;
        if (string.IsNullOrWhiteSpace(competition))
            competition = Localization.Get("Statement.DefaultName");
        var part = Localization.Get("Statement.NamePart");
        var stamp = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var baseName = $"{competition} — {part} {stamp}";
        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(invalid, '_');
        return $"{baseName}.{extension}";
    }

    // ── Column captions ────────────────────────────────────────────────────────────────────────────────

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
}

/// <summary>The resolved competition/day defaults used as the statement header watermarks + blank fallbacks.</summary>
public sealed record StatementHeaderDefaults(
    string CompetitionName, string Organisation, string Venue, string DateText);

/// <summary>The result of building a statement: the .docx bytes and a suggested save file name.</summary>
public sealed record StatementExportResult(byte[] Bytes, string SuggestedFileName);
