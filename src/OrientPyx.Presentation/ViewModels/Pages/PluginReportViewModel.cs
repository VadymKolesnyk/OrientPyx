using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Disciplines.Plugins;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Presentation.ViewModels.Shared;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// A plugin report's page («Плагіни» → the plugin → the report, Р-63): the document the plugin builds from the
/// competition's computed results, shown in full on a page-wide sheet and exported to Word (the protocols' .docx
/// writer). A day report has a day picker (the session day by default; picking one never changes the session); the
/// report's own settings sit in a card above the document. The document is rebuilt on open, on «Оновити», after the
/// settings are applied and when another station changes the data. One singleton VM serves every report — the root
/// coordinator sets <see cref="ReportId"/> before loading, like the plugin-settings page.
/// </summary>
public sealed partial class PluginReportViewModel : PageViewModelBase, IProtocolPreviewHost
{
    /// <summary>The on-screen sheet shows at most this many rows; the Word export always has every row.</summary>
    public const int PreviewRowCap = 3000;

    private readonly IPluginCatalog _plugins;
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;
    private readonly IDialogService _dialogs;
    private readonly IResultProtocolWriter _writer;
    private readonly IActivityLog? _log;

    // Guards SelectedDay while LoadAsync fills the picker, so the setter does not rebuild twice.
    private bool _syncingDay;

    public PluginReportViewModel(
        ILocalizationService localization, IPluginCatalog plugins, ICompetitionEditorService editor,
        ISessionService session, IBusyService busy, IDialogService dialogs, IResultProtocolWriter writer,
        IExportFileSaver fileSaver, IDataChangeBus bus, IWorkspaceMode mode, IActivityLog? log = null)
        : base(localization)
    {
        _plugins = plugins;
        _editor = editor;
        _session = session;
        _busy = busy;
        _dialogs = dialogs;
        _writer = writer;
        _log = log;
        FileSaver = fileSaver;

        // Everything a report may read — see ReloadAsync.
        ObserveData(bus, session, mode,
            DataArea.Participants, DataArea.ParticipantDays, DataArea.Readouts, DataArea.Groups,
            DataArea.GroupDaySettings, DataArea.ControlPoints, DataArea.CourseVariants, DataArea.Relay, DataArea.Days,
            DataArea.Settings, DataArea.CustomFields, DataArea.Competition, DataArea.Clubs, DataArea.Regions,
            DataArea.Dusshes, DataArea.AppData);

        // Singleton VM: reload on a competition/day change (marshal to UI) — only while the page is shown; opening
        // it from the menu loads it anew anyway.
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (ReportId.Length > 0 && IsActive)
                _ = LoadAsync();
        });
        // The document's captions and the report's name follow the language.
        Localization.PropertyChanged += (_, _) =>
        {
            if (ReportId.Length > 0 && IsActive)
                Dispatcher.UIThread.Post(() => _ = LoadAsync());
        };
    }

    public override string NavKey => "Nav.PluginReport";
    public override string TitleKey => "Page.PluginReport.Title";
    public override string TextKey => "Page.PluginReport.Text";

    public override string HelpKeyPrefix => "Help.PluginReport";

    /// <summary>The report shown; set by the root coordinator before <see cref="LoadAsync"/>.</summary>
    public string ReportId { get; set; } = string.Empty;

    /// <summary>Writes the export to the file the user picked (handles a target locked by Word). Used by the view.</summary>
    public IExportFileSaver FileSaver { get; }

    /// <summary>The document as the page shows it — a new instance per build (the view re-binds its table to it).</summary>
    [ObservableProperty]
    private ProtocolPreviewViewModel _preview = new();

    [ObservableProperty]
    private string _reportName = string.Empty;

    /// <summary>The report's description, else the plugin's name.</summary>
    [ObservableProperty]
    private string _reportDetails = string.Empty;

    /// <summary>The plugin's exception when it could not build the document; blank otherwise.</summary>
    [ObservableProperty]
    private string _error = string.Empty;

    public bool HasError => Error.Length > 0;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    /// <summary>A document is shown (built, not failed).</summary>
    [ObservableProperty]
    private bool _hasDocument;

    /// <summary>«У звіті немає рядків» — a document with no rows and no line of its own saying why.</summary>
    [ObservableProperty]
    private bool _showEmptyHint;

    /// <summary>The sheet shows only the first <see cref="PreviewRowCap"/> rows.</summary>
    [ObservableProperty]
    private string _truncatedHint = string.Empty;

    public bool IsTruncated => TruncatedHint.Length > 0;

    partial void OnTruncatedHintChanged(string value) => OnPropertyChanged(nameof(IsTruncated));

    // ── Day picker (a day report only; does NOT touch the session)

    public ObservableCollection<DayOption> DayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _selectedDay;

    private ReportScope _scope = ReportScope.Day;

    public bool ShowDaySelector => _scope == ReportScope.Day && DayOptions.Count > 1;

    partial void OnSelectedDayChanged(DayOption? value)
    {
        if (_syncingDay)
            return;
        _ = LoadSettingsAndBuildAsync();
    }

    // ── The report's settings

    public ObservableCollection<SettingItemViewModel> SettingItems { get; } = [];

    public bool HasSettings => SettingItems.Count > 0;

    /// <inheritdoc />
    /// <remarks>Only once a report was opened; another station's write rebuilds the document (typed but not applied
    /// settings are kept — they are re-read only on open or a day change).</remarks>
    protected override Task ReloadAsync(CancellationToken cancellationToken = default)
        => ReportId.Length > 0 ? BuildAsync() : Task.CompletedTask;

    public async Task LoadAsync()
    {
        var report = PluginReports.Find(_plugins, ReportId);
        _scope = report?.Scope ?? ReportScope.Day;
        ReportName = report is null ? ReportId : Localization.Get(report.NameKey);
        ReportDetails = report is null
            ? string.Empty
            : report.DescriptionKey is { } description ? Localization.Get(description) : report.PluginName;

        var days = _session.CurrentEvent is null ? [] : await _busy.RunAsync(() => _editor.GetDaysAsync());
        _syncingDay = true;
        try
        {
            var keep = SelectedDay?.Number ?? _session.CurrentDay?.Number;
            DayOptions.Clear();
            foreach (var day in days)
                DayOptions.Add(new DayOption(day, Localization));
            SelectedDay = DayOptions.FirstOrDefault(o => o.Number == keep) ?? DayOptions.FirstOrDefault();
        }
        finally
        {
            _syncingDay = false;
        }
        OnPropertyChanged(nameof(ShowDaySelector));

        await LoadSettingsAndBuildAsync();
    }

    // The day the report is built for: the picked one for a day report, none for a competition report.
    private Guid? ReportDayId => _scope == ReportScope.Day ? SelectedDay?.Day?.Id : null;

    private async Task LoadSettingsAndBuildAsync()
    {
        var report = PluginReports.Find(_plugins, ReportId);
        var dayId = ReportDayId;
        var data = report is null || _session.CurrentEvent is null
            ? null
            : await _busy.RunAsync(() => _editor.GetPluginReportSettingsAsync(ReportId, dayId));

        SettingItems.Clear();
        if (report is not null && data is not null)
            // Shown with the report's own scopes: in a competition report nothing is marked «для всього змагання».
            foreach (var def in report.Settings)
                SettingItems.Add(new SettingItemViewModel(Localization, report.PluginId, def,
                    data.Values.GetValueOrDefault(def.Key), EditTableAsync));
        OnPropertyChanged(nameof(HasSettings));

        await BuildAsync();
    }

    private Task<TableValue?> EditTableAsync(string? pluginId, SettingDef def, TableValue value) =>
        _dialogs.ShowSettingTableAsync(new SettingTableViewModel(Localization, pluginId, def, value));

    /// <summary>Saves the settings and rebuilds the document with them.</summary>
    [RelayCommand]
    private async Task ApplySettingsAsync()
    {
        if (SettingItems.Count == 0)
            return;
        var values = SettingItems.ToDictionary(i => i.Key, i => i.Value);
        var dayId = ReportDayId;
        await _busy.RunAsync(() => _editor.SavePluginReportSettingsAsync(ReportId, dayId, values));
        await BuildAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => BuildAsync();

    private async Task BuildAsync()
    {
        var output = await BuildOutputAsync();
        Error = output.Error is { } error ? string.Format(Localization.Get("Page.PluginReport.Error"), error) : string.Empty;
        if (output.Error is not null)
            _log?.Info($"Plugin report '{ReportId}' failed: {output.Error}");
        ShowDocument(output.Document);
    }

    private Task<PluginReportOutput> BuildOutputAsync()
    {
        if (_session.CurrentEvent is null || ReportId.Length == 0)
            return Task.FromResult(PluginReportOutput.None);
        var dayId = ReportDayId;
        var labels = BuildLabels();
        return _busy.RunAsync(() => _editor.BuildPluginReportAsync(ReportId, dayId, labels));
    }

    private PluginReportLabels BuildLabels() => new(
        ChiefJudgeLabel: Localization.Get("Protocols.ChiefJudge"),
        ChiefSecretaryLabel: Localization.Get("Protocols.ChiefSecretary"),
        JuryLabel: Localization.Get("Protocols.Jury"),
        FooterSoftwareName: Localization.Get("Protocols.Footer.Software"),
        FooterGeneratedLabel: Localization.Get("Protocols.Footer.Generated"),
        FooterPageLabel: Localization.Get("Protocols.Footer.Page"),
        Text: Localization.Get);

    // Fills a new preview from the built document (one rebuild of the sheet's table, not one per column/section).
    private void ShowDocument(ResultProtocolDocument? document)
    {
        var preview = new ProtocolPreviewViewModel();
        TruncatedHint = string.Empty;
        HasDocument = document is not null;
        if (document is not null)
        {
            preview.IsLandscape = document.Orientation == ProtocolOrientation.Landscape;
            preview.ShowTableBorders = document.TableBorders;
            preview.Subtitle = document.Subtitle;
            preview.CompetitionName = document.CompetitionName;
            preview.Title = document.Title;
            preview.DateText = document.DateText;
            preview.Venue = document.Venue;
            preview.FilterSummary = document.FilterSummary;
            preview.HasFilterSummary = document.FilterSummary.Length > 0;

            for (var i = 0; i < document.ColumnHeaders.Count; i++)
                preview.Columns.Add(new ProtocolPreviewColumn(i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    document.ColumnHeaders[i], string.Empty,
                    i < document.ColumnBodyWrap.Count && document.ColumnBodyWrap[i],
                    i < document.ColumnShrinkPriority.Count ? document.ColumnShrinkPriority[i] : 1));

            var left = PreviewRowCap;
            foreach (var section in document.Sections)
            {
                var rows = section.Rows.Take(Math.Max(0, left))
                    .Select(r => new ProtocolPreviewRow(r.Cells, r.IsTeamHeader, r.BoldCells, r.IsSubRow))
                    .ToList();
                left -= rows.Count;
                preview.Sections.Add(new ProtocolPreviewSection(section.GroupName, section.DistanceText, rows,
                    rankCalculation: section.RankCalculationText));
            }
            var total = document.Sections.Sum(s => s.Rows.Count);
            if (total > PreviewRowCap)
                TruncatedHint = string.Format(Localization.Get("Page.PluginReport.Truncated"), PreviewRowCap, total);

            foreach (var official in document.Officials)
                preview.Officials.Add($"{official.Role}: {official.NameWithCategory}");
            preview.HasOfficials = preview.Officials.Count > 0;
        }
        preview.IsEmpty = preview.Columns.Count == 0 || preview.Sections.All(s => s.Rows.Count == 0);
        ShowEmptyHint = preview.IsEmpty && !preview.HasFilterSummary;
        Preview = preview;
    }

    /// <summary>
    /// Builds the report and returns the .docx bytes + a suggested file name, or null when there is nothing to export
    /// (no competition / day, or the plugin failed — the error is then shown). The View runs the save dialog.
    /// </summary>
    public async Task<ProtocolExportResult?> GenerateWordAsync()
    {
        var output = await BuildOutputAsync();
        if (output.Document is not { } document)
        {
            if (output.Error is { } error)
                Error = string.Format(Localization.Get("Page.PluginReport.Error"), error);
            return null;
        }
        var bytes = await _busy.RunAsync(() => Task.FromResult(_writer.Write(document)));
        var report = PluginReports.Find(_plugins, ReportId);
        var name = ExportFileName.Build(Localization, report?.NameKey ?? ReportId, _session.CurrentEvent?.Name, "docx",
            _scope == ReportScope.Day ? SelectedDay?.Day : null,
            date: _session.CurrentEvent?.StartDate);
        return new ProtocolExportResult(bytes, name);
    }

    public string ExportWordLabel => Localization.Get("Page.PluginReport.ExportWord");

    // The report's columns are the plugin's: the sheet does not reorder them.
    public void MoveColumnByKey(string draggedKey, string targetKey, bool insertAfter)
    {
    }

    bool IProtocolPreviewHost.CanReorderColumns => false;
}
