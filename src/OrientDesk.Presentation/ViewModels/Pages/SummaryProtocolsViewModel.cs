using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// «Протокол по сумі днів» (multi-day summary / «Підсумковий залік»): configures and exports a per-group
/// summary that totals each participant across the chosen days, then exports it to a Word (.docx). The user
/// picks the summing mode (by points / by time), which days to count (and their order), the tie-break priority
/// day, and — in points mode — whether only participants with a result on every counted day are ranked. The
/// template is stored <b>per competition</b> in the event database (the day set is competition-specific).
///
/// Shows a live preview (the actual two-tier banded table the .docx produces, filled with real participants).
/// Every change auto-saves to the competition; generating builds the document and hands the .docx bytes to the
/// View, which runs the save dialog.
/// </summary>
public sealed partial class SummaryProtocolsViewModel : PageViewModelBase
{
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly ISummaryProtocolBuilder _builder;
    private readonly ISummaryProtocolWriter _writer;
    private readonly IBusyService _busy;

    /// <summary>How many participant rows the preview shows per page mock-up.</summary>
    private const int PreviewRowCap = 40;

    // Suppresses auto-save + preview refreshes while a template is being applied during a load.
    private bool _applyingSettings;

    // The summary data, cached so a settings change can re-render the preview without a DB round-trip.
    private SummaryProtocolData? _data;
    private CompetitionInfo? _competitionInfo;

    public SummaryProtocolsViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        ISummaryProtocolBuilder builder,
        ISummaryProtocolWriter writer,
        IBusyService busy)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _builder = builder;
        _writer = writer;
        _busy = busy;

        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());

        ModeOptions =
        [
            new(SummaryMode.ByPoints, "SummaryProtocol.Mode.ByPoints", localization),
            new(SummaryMode.ByTime, "SummaryProtocol.Mode.ByTime", localization),
        ];
    }

    public override string NavKey => "Nav.SummaryProtocol";
    public override string TitleKey => "Page.SummaryProtocol.Title";
    public override string TextKey => "Page.SummaryProtocol.Text";

    public override string IconData =>
        "M4,4 h16 v16 h-16 z M4,9 h16 M9,9 v11 M4,14 h16";

    // ── The live preview document (built by RefreshPreview) ───────────────────────────────────────────

    /// <summary>The built summary document the preview table renders. Null until first built.</summary>
    [ObservableProperty]
    private SummaryProtocolDocument? _preview;

    /// <summary>True when there are no rows to show (placeholder hint instead of a table).</summary>
    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private bool _isLandscape = true;

    // ── Mode ─────────────────────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<SummaryModeOption> ModeOptions { get; }

    [ObservableProperty]
    private SummaryModeOption? _selectedMode;

    /// <summary>True in points mode — drives the visibility of the "require all days" checkbox.</summary>
    public bool IsByPoints => SelectedMode?.Mode == SummaryMode.ByPoints;

    [ObservableProperty]
    private bool _requireAllDays;

    // ── Leading columns (add / hide + order) ─────────────────────────────────────────────────────────
    // Only the leading identity columns are configurable; the per-day result bands and the trailing «Сума»
    // are always last.

    /// <summary>The configurable leading columns, in on-page order. Reordered with up/down; toggled visible.</summary>
    public ObservableCollection<SummaryColumnItemViewModel> LeadingColumns { get; } = [];

    // ── Days (which to count + order) ──────────────────────────────────────────────────────────────────

    public ObservableCollection<SummaryDayItemViewModel> Days { get; } = [];

    // ── Priority day ───────────────────────────────────────────────────────────────────────────────────

    public ObservableCollection<DayOption> PriorityDayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _priorityDay;

    public bool HasMultipleDays => Days.Count > 1;

    // ── Header text (mirrors the results protocol) ─────────────────────────────────────────────────────

    [ObservableProperty] private string _competitionName = string.Empty;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _subtitle = string.Empty;
    [ObservableProperty] private string _venue = string.Empty;
    [ObservableProperty] private string _competitionType = string.Empty;
    [ObservableProperty] private string _dateText = string.Empty;

    [ObservableProperty] private string _competitionNamePlaceholder = string.Empty;
    [ObservableProperty] private string _subtitlePlaceholder = string.Empty;
    [ObservableProperty] private string _venuePlaceholder = string.Empty;
    [ObservableProperty] private string _dateTextPlaceholder = string.Empty;
    [ObservableProperty] private string _competitionTypePlaceholder = string.Empty;

    [ObservableProperty]
    private bool _settingsSaved;

    public async Task LoadAsync()
    {
        var (data, info, settings) = await _busy.RunAsync(async () =>
        {
            var d = await _editor.GetSummaryProtocolDataAsync();
            var i = await _editor.GetInfoAsync();
            var s = await _editor.GetSummaryProtocolSettingsAsync();
            return (d, i, s);
        });

        _data = data;
        _competitionInfo = info;
        ApplySettings(settings ?? Default(data));
        ResolveHeaderPlaceholders(info);
        RefreshPreview();
    }

    // A sensible default when the competition has no saved template: by points, all days counted in order,
    // priority = first day.
    private static SummaryProtocolSettings Default(SummaryProtocolData data) => new()
    {
        Mode = SummaryMode.ByPoints,
        Orientation = ProtocolOrientation.Landscape,
        Days = data.Days.Select(d => new SummaryDaySetting { DayId = d.Id, DayNumber = d.Number, Counted = true }).ToList(),
        PriorityDayId = data.Days.Count > 0 ? data.Days[0].Id : null,
    };

    /// <summary>The localized caption key for a leading column.</summary>
    private static string CaptionKey(SummaryColumn column) => column switch
    {
        SummaryColumn.Sequence => "SummaryProtocol.Col.Sequence",
        SummaryColumn.Number => "SummaryProtocol.Col.Number",
        SummaryColumn.FullName => "SummaryProtocol.Col.FullName",
        SummaryColumn.BirthDate => "SummaryProtocol.Col.BirthDate",
        SummaryColumn.Region => "SummaryProtocol.Col.Region",
        SummaryColumn.Club => "SummaryProtocol.Col.Club",
        SummaryColumn.Dussh => "SummaryProtocol.Col.Dussh",
        SummaryColumn.Coach => "SummaryProtocol.Col.Coach",
        SummaryColumn.Rank => "SummaryProtocol.Col.Rank",
        _ => "SummaryProtocol.Col.FullName"
    };

    private void ApplySettings(SummaryProtocolSettings settings)
    {
        _applyingSettings = true;
        try
        {
            SelectedMode = ModeOptions.FirstOrDefault(o => o.Mode == settings.Mode) ?? ModeOptions[0];
            IsLandscape = settings.Orientation == ProtocolOrientation.Landscape;
            RequireAllDays = settings.RequireAllDays;
            CompetitionName = settings.CompetitionName;
            Title = settings.Title;
            Subtitle = settings.Subtitle;
            Venue = settings.Venue;
            CompetitionType = settings.CompetitionType;
            DateText = settings.DateText;

            // Reconcile the saved leading columns against the full set: a summary saved before this feature (or
            // before a column existed) is missing some, so append any absent column (hidden) in enum order, and
            // seed from the default layout when no list is stored at all.
            var savedColumns = settings.LeadingColumns is { Count: > 0 }
                ? settings.LeadingColumns
                : SummaryProtocolSettings.DefaultLeadingColumns();
            var present = savedColumns.Select(c => c.Column).ToHashSet();
            foreach (SummaryColumn column in Enum.GetValues<SummaryColumn>())
                if (present.Add(column))
                    savedColumns.Add(new SummaryColumnSetting { Column = column, Visible = false });

            LeadingColumns.Clear();
            foreach (var c in savedColumns)
            {
                var item = new SummaryColumnItemViewModel(c.Column, CaptionKey(c.Column), c.Visible, Localization);
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SummaryColumnItemViewModel.Visible))
                    {
                        RefreshPreview();
                        AutoSave();
                    }
                };
                LeadingColumns.Add(item);
            }

            // Reconcile the saved day selection against the live day list: keep the saved order for days that
            // still exist, drop vanished days, append any new day (counted) at the end.
            var data = _data ?? SummaryProtocolData.Empty;
            var savedByDay = settings.Days.ToDictionary(d => d.DayId);
            var ordered = new List<SummaryProtocolDay>();
            foreach (var sel in settings.Days)
            {
                var match = data.Days.FirstOrDefault(d => d.Id == sel.DayId);
                if (match is not null)
                    ordered.Add(match);
            }
            foreach (var day in data.Days.OrderBy(d => d.Number))
                if (!ordered.Any(o => o.Id == day.Id))
                    ordered.Add(day);

            Days.Clear();
            foreach (var day in ordered)
            {
                var counted = savedByDay.TryGetValue(day.Id, out var sel) ? sel.Counted : true;
                var item = new SummaryDayItemViewModel(day.Id, day.Number, counted, Localization);
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SummaryDayItemViewModel.Counted))
                    {
                        ResolveHeaderPlaceholders(_competitionInfo);
                        RefreshPreview();
                        AutoSave();
                    }
                };
                Days.Add(item);
            }
            OnPropertyChanged(nameof(HasMultipleDays));

            // Priority-day dropdown = the day list; select the saved one (else the first).
            PriorityDayOptions.Clear();
            foreach (var day in ordered)
                PriorityDayOptions.Add(new DayOption(new EventDay { Id = day.Id, Number = day.Number, Date = day.Date }, Localization));
            PriorityDay = PriorityDayOptions.FirstOrDefault(o => o.Day?.Id == settings.PriorityDayId)
                          ?? PriorityDayOptions.FirstOrDefault();
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private void ResolveHeaderPlaceholders(CompetitionInfo? info)
    {
        var name = !string.IsNullOrWhiteSpace(info?.Name) ? info!.Name : _session.CurrentEvent?.Name;
        CompetitionNamePlaceholder = name?.Trim() ?? string.Empty;
        SubtitlePlaceholder = info?.Organisation?.Trim() ?? string.Empty;
        VenuePlaceholder = info?.Venue?.Trim() ?? string.Empty;

        // Date placeholder: the span of the counted days ("30.05.2026 - 31.05.2026"), else the competition start.
        var dates = (_data?.Days ?? [])
            .Where(d => d.Date is not null)
            .Select(d => d.Date!.Value)
            .OrderBy(d => d)
            .ToList();
        if (dates.Count >= 2)
            DateTextPlaceholder = $"{dates[0]:dd.MM.yyyy} - {dates[^1]:dd.MM.yyyy}";
        else if (dates.Count == 1)
            DateTextPlaceholder = dates[0].ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        else
            DateTextPlaceholder = info?.StartDate is { } sd ? sd.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : string.Empty;

        var countedDays = Days.Count(d => d.Counted);
        CompetitionTypePlaceholder = countedDays > 0
            ? string.Format(Localization.Get("SummaryProtocol.CompetitionType.Default"), countedDays)
            : string.Empty;
    }

    partial void OnSelectedModeChanged(SummaryModeOption? value)
    {
        OnPropertyChanged(nameof(IsByPoints));
        if (_applyingSettings)
            return;
        RefreshPreview();
        AutoSave();
    }

    partial void OnRequireAllDaysChanged(bool value)
    {
        if (_applyingSettings)
            return;
        RefreshPreview();
        AutoSave();
    }

    partial void OnPriorityDayChanged(DayOption? value)
    {
        if (_applyingSettings)
            return;
        RefreshPreview();
        AutoSave();
    }

    partial void OnIsLandscapeChanged(bool value)
    {
        if (_applyingSettings)
            return;
        RefreshPreview();
        AutoSave();
    }

    /// <summary>
    /// Moves the leading column with key <paramref name="draggedKey"/> next to the one with key
    /// <paramref name="targetKey"/> — before it, or after it when <paramref name="insertAfter"/> is true. Called
    /// by the preview's header drag-reorder. Both keys are resolved against the FULL leading-column list (which
    /// includes hidden columns the preview never shows), so the move is correct regardless of visibility. Only
    /// the leading columns are reorderable; the per-day result bands and «Сума» are fixed at the end, so a key
    /// that isn't a leading column (a sub-column / total) is ignored.
    /// </summary>
    public void MoveLeadingColumnByKey(string draggedKey, string targetKey, bool insertAfter)
    {
        if (!Enum.TryParse<SummaryColumn>(draggedKey, out var dragged) ||
            !Enum.TryParse<SummaryColumn>(targetKey, out var target))
            return;
        var from = IndexOf(dragged);
        var targetIndex = IndexOf(target);
        if (from < 0 || targetIndex < 0 || from == targetIndex)
            return;

        // Insert before/after the target in the full order. Removing the dragged item first shifts every later
        // slot left by one, so when the source sits before the destination, drop the index by one.
        var insertIndex = insertAfter ? targetIndex + 1 : targetIndex;
        if (from < insertIndex)
            insertIndex--;
        insertIndex = Math.Clamp(insertIndex, 0, LeadingColumns.Count - 1);
        if (from == insertIndex)
            return;

        LeadingColumns.Move(from, insertIndex);
        RefreshPreview();
        AutoSave();
    }

    private int IndexOf(SummaryColumn column)
    {
        for (var i = 0; i < LeadingColumns.Count; i++)
            if (LeadingColumns[i].Column == column)
                return i;
        return -1;
    }

    // Moving a day reorders the column bands on the page.
    [RelayCommand]
    private void MoveDayUp(SummaryDayItemViewModel? item)
    {
        if (item is null) return;
        var i = Days.IndexOf(item);
        if (i > 0)
        {
            Days.Move(i, i - 1);
            RefreshPreview();
            AutoSave();
        }
    }

    [RelayCommand]
    private void MoveDayDown(SummaryDayItemViewModel? item)
    {
        if (item is null) return;
        var i = Days.IndexOf(item);
        if (i >= 0 && i < Days.Count - 1)
        {
            Days.Move(i, i + 1);
            RefreshPreview();
            AutoSave();
        }
    }

    private void OnHeaderEdited()
    {
        if (_applyingSettings)
            return;
        SettingsSaved = false;
        RefreshPreview();
        AutoSave();
    }

    partial void OnCompetitionNameChanged(string value) => OnHeaderEdited();
    partial void OnTitleChanged(string value) => OnHeaderEdited();
    partial void OnSubtitleChanged(string value) => OnHeaderEdited();
    partial void OnVenueChanged(string value) => OnHeaderEdited();
    partial void OnCompetitionTypeChanged(string value) => OnHeaderEdited();
    partial void OnDateTextChanged(string value) => OnHeaderEdited();

    private void RefreshPreview()
    {
        var settings = BuildDocumentSettings();
        var data = _data ?? SummaryProtocolData.Empty;
        var document = _builder.Build(data, settings, BuildLabels());

        // Cap the body rows across sections so the page mock-up stays cheap.
        var remaining = PreviewRowCap;
        var sections = new List<SummaryProtocolSection>();
        foreach (var s in document.Sections)
        {
            if (remaining <= 0) break;
            var rows = s.Rows.Take(remaining).ToList();
            remaining -= rows.Count;
            sections.Add(new SummaryProtocolSection { GroupName = s.GroupName, Rows = rows });
        }
        var capped = new SummaryProtocolDocument
        {
            Orientation = document.Orientation,
            CompetitionName = document.CompetitionName,
            Title = document.Title.Length > 0 ? document.Title : Localization.Get("SummaryProtocol.DefaultTitle"),
            Subtitle = document.Subtitle,
            Venue = document.Venue,
            DateText = document.DateText,
            CompetitionType = document.CompetitionType,
            LeadingColumns = document.LeadingColumns,
            NameColumnIndex = document.NameColumnIndex,
            DayBands = document.DayBands,
            TotalColumnHeader = document.TotalColumnHeader,
            ColumnBodyWrap = document.ColumnBodyWrap,
            ColumnShrinkPriority = document.ColumnShrinkPriority,
            Sections = sections,
        };

        Preview = capped;
        IsEmpty = sections.Count == 0 || sections.All(s => s.Rows.Count == 0);
    }

    // The persisted settings: the user's typed header values (blanks stay blank).
    private SummaryProtocolSettings BuildSettings() => new()
    {
        Mode = SelectedMode?.Mode ?? SummaryMode.ByPoints,
        Orientation = IsLandscape ? ProtocolOrientation.Landscape : ProtocolOrientation.Portrait,
        LeadingColumns = LeadingColumns.Select(c => c.ToSetting()).ToList(),
        RequireAllDays = RequireAllDays,
        Days = Days.Select(d => new SummaryDaySetting { DayId = d.DayId, DayNumber = d.DayNumber, Counted = d.Counted }).ToList(),
        PriorityDayId = PriorityDay?.Day?.Id,
        CompetitionName = CompetitionName?.Trim() ?? string.Empty,
        Title = Title?.Trim() ?? string.Empty,
        Subtitle = Subtitle?.Trim() ?? string.Empty,
        Venue = Venue?.Trim() ?? string.Empty,
        CompetitionType = CompetitionType?.Trim() ?? string.Empty,
        DateText = DateText?.Trim() ?? string.Empty,
    };

    // The settings used to build the document: blanks folded to their resolved placeholders.
    private SummaryProtocolSettings BuildDocumentSettings()
    {
        var s = BuildSettings();
        if (s.CompetitionName.Length == 0) s.CompetitionName = CompetitionNamePlaceholder;
        if (s.Subtitle.Length == 0) s.Subtitle = SubtitlePlaceholder;
        if (s.Venue.Length == 0) s.Venue = VenuePlaceholder;
        if (s.DateText.Length == 0) s.DateText = DateTextPlaceholder;
        if (s.CompetitionType.Length == 0) s.CompetitionType = CompetitionTypePlaceholder;
        return s;
    }

    private void AutoSave()
    {
        if (_applyingSettings)
            return;
        SettingsSaved = false;
        if (_session.CurrentEvent is null)
            return;
        var settings = BuildSettings();
        _ = Task.Run(async () =>
        {
            try { await _editor.SaveSummaryProtocolSettingsAsync(settings); }
            catch { /* best-effort auto-save */ }
        });
    }

    /// <summary>Builds the summary protocol and returns the .docx bytes + a suggested file name, or null when
    /// there is nothing to export. The View runs the save dialog.</summary>
    public async Task<ProtocolExportResult?> GenerateAsync()
    {
        if (_session.CurrentEvent is null)
            return null;

        var settings = BuildSettings();
        var documentSettings = BuildDocumentSettings();
        var labels = BuildLabels();

        var bytes = await _busy.RunAsync(async () =>
        {
            await _editor.SaveSummaryProtocolSettingsAsync(settings);
            var data = await _editor.GetSummaryProtocolDataAsync();
            var document = _builder.Build(data, documentSettings, labels);
            return _writer.Write(document);
        });
        SettingsSaved = true;

        return new ProtocolExportResult(bytes, SuggestedFileName());
    }

    private SummaryProtocolLabels BuildLabels() => new(
        DefaultTitle: Localization.Get("SummaryProtocol.DefaultTitle"),
        DayBand: Localization.Get("SummaryProtocol.DayBand"),
        ColSequence: Localization.Get("SummaryProtocol.Col.Sequence"),
        ColNumber: Localization.Get("SummaryProtocol.Col.Number"),
        ColFullName: Localization.Get("SummaryProtocol.Col.FullName"),
        ColBirthDate: Localization.Get("SummaryProtocol.Col.BirthDate"),
        ColRegion: Localization.Get("SummaryProtocol.Col.Region"),
        ColClub: Localization.Get("SummaryProtocol.Col.Club"),
        ColDussh: Localization.Get("SummaryProtocol.Col.Dussh"),
        ColCoach: Localization.Get("SummaryProtocol.Col.Coach"),
        ColRank: Localization.Get("SummaryProtocol.Col.Rank"),
        SubPlace: Localization.Get("SummaryProtocol.Sub.Place"),
        SubTime: Localization.Get("SummaryProtocol.Sub.Time"),
        SubPoints: Localization.Get("SummaryProtocol.Sub.Points"),
        Total: Localization.Get("SummaryProtocol.Col.Total"),
        ChiefJudge: Localization.Get("Protocols.ChiefJudge"),
        ChiefSecretary: Localization.Get("Protocols.ChiefSecretary"),
        Jury: Localization.Get("Protocols.Jury"),
        FooterSoftwareName: Localization.Get("Protocols.Footer.Software"),
        FooterGeneratedLabel: Localization.Get("Protocols.Footer.Generated"),
        FooterPageLabel: Localization.Get("Protocols.Footer.Page"));

    private string SuggestedFileName()
    {
        var competition = _session.CurrentEvent?.Name;
        if (string.IsNullOrWhiteSpace(competition))
            competition = Localization.Get("Protocols.DefaultName");
        var part = Localization.Get("SummaryProtocol.NamePart");
        var stamp = DateTime.Now.ToString("yyyy-MM-dd");
        var baseName = $"{competition} — {part} {stamp}";
        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(invalid, '_');
        return $"{baseName}.docx";
    }
}

/// <summary>A selectable summing-mode option for the mode dropdown.</summary>
public sealed partial class SummaryModeOption : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly string _captionKey;

    public SummaryModeOption(SummaryMode mode, string captionKey, ILocalizationService localization)
    {
        Mode = mode;
        _captionKey = captionKey;
        _localization = localization;
        _localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Caption));
    }

    public SummaryMode Mode { get; }

    public string Caption => _localization.Get(_captionKey);
}
