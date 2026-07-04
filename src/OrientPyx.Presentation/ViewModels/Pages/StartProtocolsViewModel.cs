using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// «Стартові протоколи»: configures and exports a start protocol to a Word (.docx) document. Two kinds share
/// this VM (set via <see cref="Kind"/> on open): the <b>regular</b> start protocol — one section per group,
/// members ordered by start time within the group — and the <b>judges'</b> protocol — one section per start
/// minute, members of that minute (across all groups) under it. The template (orientation, ordered/visible
/// columns, header text) is stored <b>per competition day and per kind</b> in the event database via
/// <see cref="ICompetitionEditorService"/>; a (day, kind) with no saved template is seeded from the kind's
/// built-in default. Header fields fall back to the competition metadata when blank.
///
/// The page shows the same live <see cref="ProtocolPreviewViewModel"/> the results protocol uses — the actual
/// document mock-up (header + one section as a real table, filled with the day's real participants) built
/// from the same <see cref="IStartProtocolBuilder"/> the export uses, so the preview matches the .docx.
/// Reordering a column (drag its header) or toggling visibility rebuilds the preview immediately. Choosing a
/// day here never changes the active session day.
/// </summary>
public sealed partial class StartProtocolsViewModel : PageViewModelBase, IProtocolPreviewHost
{
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IAppSettingsService _appSettings;
    private readonly IStartProtocolBuilder _builder;
    private readonly IResultProtocolWriter _writer;
    private readonly IBusyService _busy;

    /// <summary>How many participant rows the preview shows — enough to fill an A4 page; the page clips the rest.</summary>
    private const int PreviewRowCap = 40;

    private bool _syncingDay;
    private bool _applyingSettings;
    private StartProtocolData? _previewData;
    private CompetitionInfo? _competitionInfo;

    public StartProtocolsViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IAppSettingsService appSettings,
        IStartProtocolBuilder builder,
        IResultProtocolWriter writer,
        IBusyService busy)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _appSettings = appSettings;
        _builder = builder;
        _writer = writer;
        _busy = busy;

        // Singleton VM: reload the day list + template on a competition/day change (marshal to UI).
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());

        // The base re-raises Title/Text on a language switch but not PageTitle (a VM-only property,
        // needed because Title is shadowed by the editable document title). Refresh it ourselves.
        localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(PageTitle));
    }

    /// <summary>Which start protocol this page is currently configuring. Set by the open command before LoadAsync.</summary>
    public StartProtocolKind Kind { get; set; } = StartProtocolKind.Regular;

    public ProtocolPreviewViewModel Preview { get; } = new();

    // The nav/title keys switch with the kind so the shell tab + heading read correctly for each protocol.
    public override string NavKey => Kind == StartProtocolKind.Judges ? "Nav.StartProtocolJudges" : "Nav.StartProtocol";
    public override string TitleKey => Kind == StartProtocolKind.Judges ? "Page.StartProtocolJudges.Title" : "Page.StartProtocol.Title";
    public override string TextKey => Kind == StartProtocolKind.Judges ? "Page.StartProtocolJudges.Text" : "Page.StartProtocol.Text";

    /// <summary>
    /// Static localized page heading (from <see cref="TitleKey"/>). Kept separate from the base
    /// <c>Title</c> because that name is shadowed here by the editable document-title observable property;
    /// the page's <c>h1</c> binds to this so it stays a fixed page title like every other page.
    /// </summary>
    public string PageTitle => Localization.Get(TitleKey);

    public override string IconData =>
        "M12,2 a10,10 0 1 0 0.001,0 z M12,7 v5 l4,2 M12,2 v3 M22,12 h-3 M12,22 v-3 M2,12 h3";

    // ── Day picker (does NOT touch the session) ──────────────────────────────────────────────────────

    public ObservableCollection<DayOption> DayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _selectedDay;

    public bool ShowDaySelector => DayOptions.Count > 1;

    // ── Settings ─────────────────────────────────────────────────────────────────────────────────────

    public ObservableCollection<StartProtocolColumnItemViewModel> Columns { get; } = [];

    [ObservableProperty]
    private bool _isLandscape;

    [ObservableProperty]
    private string _competitionName = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _venue = string.Empty;

    [ObservableProperty]
    private string _competitionType = string.Empty;

    [ObservableProperty]
    private string _dateText = string.Empty;

    // ── Header placeholders (watermarks) ───────────────────────────────────────────────────────────────
    // The resolved competition/day default for each header field, shown as the TextBox watermark when the
    // user typed nothing and used as the build-time fallback for a blank field; an empty placeholder leaves
    // the field blank everywhere. See ResolveHeaderPlaceholders / BuildDocumentSettings.

    [ObservableProperty]
    private string _competitionNamePlaceholder = string.Empty;

    [ObservableProperty]
    private string _subtitlePlaceholder = string.Empty;

    [ObservableProperty]
    private string _venuePlaceholder = string.Empty;

    [ObservableProperty]
    private string _dateTextPlaceholder = string.Empty;

    [ObservableProperty]
    private string _competitionTypePlaceholder = string.Empty;

    /// <summary>The kind's localized default title, shown as the Title watermark and used by the builder when
    /// the title is left blank (mirrors how the results protocol shows its default title).</summary>
    public string TitlePlaceholder => Localization.Get(DefaultTitleKey);

    [ObservableProperty]
    private bool _settingsSaved;

    // The localized caption for a start-protocol column.
    private static string CaptionKey(StartProtocolColumn column) => column switch
    {
        StartProtocolColumn.StartTime => "StartProtocols.Col.StartTime",
        StartProtocolColumn.Sequence => "StartProtocols.Col.Sequence",
        StartProtocolColumn.Number => "StartProtocols.Col.Number",
        StartProtocolColumn.FullName => "StartProtocols.Col.FullName",
        StartProtocolColumn.BirthDate => "StartProtocols.Col.BirthDate",
        StartProtocolColumn.Club => "StartProtocols.Col.Club",
        StartProtocolColumn.Region => "StartProtocols.Col.Region",
        StartProtocolColumn.Dussh => "StartProtocols.Col.Dussh",
        StartProtocolColumn.Coach => "StartProtocols.Col.Coach",
        StartProtocolColumn.Rank => "StartProtocols.Col.Rank",
        StartProtocolColumn.Chip => "StartProtocols.Col.Chip",
        StartProtocolColumn.Group => "StartProtocols.Col.Group",
        StartProtocolColumn.Team => "StartProtocols.Col.Team",
        StartProtocolColumn.Note => "StartProtocols.Col.Note",
        _ => "StartProtocols.Col.FullName"
    };

    /// <summary>Re-raises the nav/title/text keys after the kind is switched (the shell binds to them).</summary>
    public void RaiseKindLabels()
    {
        OnPropertyChanged(nameof(NavKey));
        OnPropertyChanged(nameof(TitleKey));
        OnPropertyChanged(nameof(TextKey));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(TitlePlaceholder));
    }

    public async Task LoadAsync()
    {
        var (days, info) = await _busy.RunAsync(async () =>
        {
            var d = await _editor.GetDaysAsync();
            var i = await _editor.GetInfoAsync();
            return (d, i);
        });

        _syncingDay = true;
        try
        {
            DayOptions.Clear();
            foreach (var day in days)
                DayOptions.Add(new DayOption(day, Localization));

            var current = _session.CurrentDay?.Number;
            SelectedDay = DayOptions.FirstOrDefault(o => o.Number == current) ?? DayOptions.FirstOrDefault();
        }
        finally
        {
            _syncingDay = false;
        }
        OnPropertyChanged(nameof(ShowDaySelector));

        _competitionInfo = info;
        await LoadDayTemplateAsync();
    }

    // Loads the selected day's template for the current kind (seeding from the kind default when none),
    // seeds header defaults, fetches the day's start data, and renders the preview.
    private async Task LoadDayTemplateAsync()
    {
        if (SelectedDay?.Day is not { } day)
        {
            _previewData = null;
            RefreshPreview();
            return;
        }

        var kind = Kind;
        var (settings, data) = await _busy.RunAsync(async () =>
        {
            // The day's saved template, or the app-level default for this kind when the day has none yet.
            var s = await _editor.GetStartProtocolSettingsAsync(day.Id, kind)
                    ?? await _appSettings.GetStartProtocolSettingsAsync(kind);
            var d = await _editor.GetStartProtocolDataAsync(day.Id);
            return (s, d);
        });

        _previewData = data;
        ApplySettings(settings);
        ResolveHeaderPlaceholders(_competitionInfo);
        RefreshPreview();
    }

    private void ApplySettings(StartProtocolSettings settings)
    {
        _applyingSettings = true;
        try
        {
            IsLandscape = settings.Orientation == ProtocolOrientation.Landscape;
            CompetitionName = settings.CompetitionName;
            Title = settings.Title;
            Subtitle = settings.Subtitle;
            Venue = settings.Venue;
            CompetitionType = settings.CompetitionType;
            DateText = settings.DateText;
        }
        finally
        {
            _applyingSettings = false;
        }

        Columns.Clear();
        foreach (var c in MergeWithDefaults(settings.Columns))
        {
            var item = new StartProtocolColumnItemViewModel(c.Column, CaptionKey(c.Column), c.Visible, Localization);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(StartProtocolColumnItemViewModel.Visible))
                {
                    RefreshPreview();
                    AutoSave();
                }
            };
            Columns.Add(item);
        }
    }

    // A saved template predates any column added to the enum later (e.g. «Прим.»), so it would be missing from
    // the loaded set and never show up as a checkbox or in the preview. Keep the saved order/visibility, then
    // append (hidden) any column from the kind default the saved set doesn't have — so new columns always appear.
    private IReadOnlyList<StartProtocolColumnSetting> MergeWithDefaults(IEnumerable<StartProtocolColumnSetting> saved)
    {
        var merged = saved.ToList();
        var present = merged.Select(c => c.Column).ToHashSet();
        foreach (var def in StartProtocolSettings.Default(Kind).Columns)
            if (present.Add(def.Column))
                merged.Add(new StartProtocolColumnSetting { Column = def.Column, Visible = false });
        return merged;
    }

    // Resolves the header watermark for each blank-able field. The per-day fields (date, venue, competition
    // type) come from the SELECTED DAY first and fall back to the competition; the rest from the competition.
    // Shown as the TextBox placeholders and used as the build-time fallback for a field left blank — never
    // written into the editable field, so an untouched field stays empty and a missing DB value yields an
    // empty header cell, not the placeholder hint.
    private void ResolveHeaderPlaceholders(CompetitionInfo? info)
    {
        var day = SelectedDay?.Day;

        var name = !string.IsNullOrWhiteSpace(info?.Name) ? info!.Name : _session.CurrentEvent?.Name;
        CompetitionNamePlaceholder = name?.Trim() ?? string.Empty;
        SubtitlePlaceholder = info?.Organisation?.Trim() ?? string.Empty;

        // Venue: the day's own venue, else the competition venue.
        VenuePlaceholder = FirstNonBlank(day?.Venue, info?.Venue);

        // Date: the day's date, else the competition's start date.
        var date = day?.Date ?? info?.StartDate;
        DateTextPlaceholder = date is { } d ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : string.Empty;

        // Competition type ("тип дистанції"): the day's default discipline, localized. No competition-level
        // equivalent, so blank when no day is selected.
        CompetitionTypePlaceholder = day is { } ? Localization.Get("Discipline.Type." + day.DefaultDiscipline) : string.Empty;
    }

    // The first of the candidates that is non-blank (trimmed), or empty when none is.
    private static string FirstNonBlank(params string?[] candidates)
    {
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c))
                return c!.Trim();
        return string.Empty;
    }

    partial void OnSelectedDayChanged(DayOption? value)
    {
        if (_syncingDay)
            return;
        _ = LoadDayTemplateAsync();
    }

    [RelayCommand]
    private void MoveColumnUp(StartProtocolColumnItemViewModel? item)
    {
        if (item is null)
            return;
        var i = Columns.IndexOf(item);
        if (i > 0)
        {
            Columns.Move(i, i - 1);
            RefreshPreview();
            AutoSave();
        }
    }

    [RelayCommand]
    private void MoveColumnDown(StartProtocolColumnItemViewModel? item)
    {
        if (item is null)
            return;
        var i = Columns.IndexOf(item);
        if (i >= 0 && i < Columns.Count - 1)
        {
            Columns.Move(i, i + 1);
            RefreshPreview();
            AutoSave();
        }
    }

    public void MoveColumnByKey(string draggedKey, string targetKey, bool insertAfter)
    {
        if (!Enum.TryParse<StartProtocolColumn>(draggedKey, out var dragged) ||
            !Enum.TryParse<StartProtocolColumn>(targetKey, out var target))
            return;
        var from = IndexOf(dragged);
        var targetIndex = IndexOf(target);
        if (from < 0 || targetIndex < 0 || from == targetIndex)
            return;

        // Insert before or after the target (in the full order). Removing the dragged item first shifts every
        // later slot left by one, so when the source sits before the destination, drop the index by one.
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

    private int IndexOf(StartProtocolColumn column)
    {
        for (var i = 0; i < Columns.Count; i++)
            if (Columns[i].Column == column)
                return i;
        return -1;
    }

    // Auto-saves the current template to the SELECTED DAY (for this kind) on every change. Fire-and-forget off
    // the UI thread; guarded so it doesn't fire while a template is being applied during a load.
    private void AutoSave()
    {
        if (_applyingSettings)
            return;
        // Any edit invalidates the "saved as default" flash from a previous app-default save.
        SettingsSaved = false;
        if (SelectedDay?.Day is not { } day)
            return;
        var kind = Kind;
        var settings = BuildSettings();
        // Observe the task's exception here: auto-save is fire-and-forget, so an unhandled failure would
        // otherwise surface on the finalizer thread as an UnobservedTaskException and crash the app.
        _ = Task.Run(async () =>
        {
            try
            {
                await _editor.SaveStartProtocolSettingsAsync(day.Id, kind, settings);
            }
            catch
            {
                // Best-effort auto-save; the next edit (or an explicit export) will persist again.
            }
        });
    }

    // "Save for next competitions": stores the current layout as the APPLICATION-LEVEL default for this kind,
    // so a new day with no saved template of its own seeds from this. The per-day template is already auto-
    // saved on every edit, so this button only sets the shared default.
    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var settings = BuildSettings();
        await _busy.RunAsync(() => _appSettings.SaveStartProtocolSettingsAsync(Kind, settings));
        SettingsSaved = true;
    }

    private void RefreshPreview()
    {
        // Build with the placeholder fallbacks folded in so the preview table mirrors the .docx. (The header
        // text itself is shown by the inline TextBoxes bound to the raw VM fields + their watermarks.)
        var settings = BuildDocumentSettings();

        Preview.IsLandscape = settings.Orientation == ProtocolOrientation.Landscape;
        Preview.CompetitionName = settings.CompetitionName;
        Preview.Title = settings.Title.Length > 0 ? settings.Title : Localization.Get(DefaultTitleKey);
        Preview.Subtitle = settings.Subtitle;
        Preview.DateText = settings.DateText;
        Preview.CompetitionType = settings.CompetitionType;
        Preview.Venue = settings.Venue;

        var data = _previewData ?? new StartProtocolData([]);
        var document = _builder.Build(data, settings, Kind, BuildLabels());

        Preview.Columns.Clear();
        var visible = settings.Columns.Where(c => c.Visible).Select(c => c.Column).ToList();
        if (visible.Count == 0)
            visible.Add(StartProtocolColumn.FullName);
        for (var i = 0; i < visible.Count && i < document.ColumnHeaders.Count; i++)
            Preview.Columns.Add(new ProtocolPreviewColumn(visible[i].ToString(), document.ColumnHeaders[i],
                i < document.ColumnHeadersShort.Count ? document.ColumnHeadersShort[i] : string.Empty,
                i < document.ColumnBodyWrap.Count && document.ColumnBodyWrap[i],
                i < document.ColumnShrinkPriority.Count ? document.ColumnShrinkPriority[i] : 1));

        // Render the sections exactly as the .docx stacks them (caption + table), capping the TOTAL body rows
        // across sections so the page mock-up fills but stays cheap to build. Start sections have no course
        // sub-caption.
        Preview.Sections.Clear();
        var remaining = PreviewRowCap;
        foreach (var section in document.Sections)
        {
            if (remaining <= 0)
                break;
            var rows = section.Rows.Take(remaining)
                .Select(r => new ProtocolPreviewRow(r.Cells, r.IsTeamHeader))
                .ToList();
            remaining -= rows.Count;
            Preview.Sections.Add(new ProtocolPreviewSection(
                section.GroupName, string.Empty, rows, section.CourseSetterText, section.IsBanded));
        }
        Preview.IsEmpty = Preview.Sections.Count == 0 || Preview.Sections.All(s => s.Rows.Count == 0);

        // Officials are deliberately NOT shown in the on-screen preview — they're a fixed signature block at
        // the very bottom of the printed sheet, off the visible mock-up area, and only clutter the preview.
        Preview.Officials.Clear();
        Preview.HasOfficials = false;
    }

    // The settings actually persisted on the day: the user's typed values, NOT the resolved placeholders, so a
    // field left blank stays blank in the saved template (the watermark is a hint, never stored).
    private StartProtocolSettings BuildSettings() => new()
    {
        Orientation = IsLandscape ? ProtocolOrientation.Landscape : ProtocolOrientation.Portrait,
        CompetitionName = CompetitionName?.Trim() ?? string.Empty,
        Title = Title?.Trim() ?? string.Empty,
        Subtitle = Subtitle?.Trim() ?? string.Empty,
        Venue = Venue?.Trim() ?? string.Empty,
        CompetitionType = CompetitionType?.Trim() ?? string.Empty,
        DateText = DateText?.Trim() ?? string.Empty,
        Columns = Columns.Select(c => c.ToSetting()).ToList()
    };

    // The settings used to BUILD the document (preview + export): the saved template with each blank header
    // field folded down to its resolved placeholder (the competition/day default). A field whose placeholder is
    // also empty stays empty. Title keeps its own localized default (handled by the builder), so it is untouched.
    private StartProtocolSettings BuildDocumentSettings()
    {
        var s = BuildSettings();
        if (s.CompetitionName.Length == 0) s.CompetitionName = CompetitionNamePlaceholder;
        if (s.Subtitle.Length == 0) s.Subtitle = SubtitlePlaceholder;
        if (s.Venue.Length == 0) s.Venue = VenuePlaceholder;
        if (s.DateText.Length == 0) s.DateText = DateTextPlaceholder;
        if (s.CompetitionType.Length == 0) s.CompetitionType = CompetitionTypePlaceholder;
        return s;
    }

    /// <summary>
    /// Builds the start protocol for the selected day and returns the .docx bytes + a suggested file name,
    /// or null when there is nothing to export. The View runs the save dialog. Also persists the template.
    /// </summary>
    public async Task<ProtocolExportResult?> GenerateAsync()
    {
        if (_session.CurrentEvent is null || SelectedDay?.Day is not { } day)
            return null;

        var settings = BuildSettings();          // persisted: the user's typed values (blanks stay blank)
        var documentSettings = BuildDocumentSettings(); // built: blanks folded to the competition defaults
        var labels = BuildLabels();
        var kind = Kind;

        var bytes = await _busy.RunAsync(async () =>
        {
            await _editor.SaveStartProtocolSettingsAsync(day.Id, kind, settings);
            var data = await _editor.GetStartProtocolDataAsync(day.Id);
            var document = _builder.Build(data, documentSettings, kind, labels);
            return _writer.Write(document);
        });
        SettingsSaved = true;

        return new ProtocolExportResult(bytes, SuggestedFileName(day));
    }

    private string DefaultTitleKey =>
        Kind == StartProtocolKind.Judges ? "StartProtocols.DefaultTitle.Judges" : "StartProtocols.DefaultTitle.Regular";

    private StartProtocolLabels BuildLabels()
    {
        var headers = new Dictionary<StartProtocolColumn, string>();
        var shortHeaders = new Dictionary<StartProtocolColumn, string>();
        foreach (StartProtocolColumn column in Enum.GetValues<StartProtocolColumn>())
        {
            headers[column] = Localization.Get(CaptionKey(column));
            if (ShortCaptionKey(column) is { } key)
                shortHeaders[column] = Localization.Get(key);
        }

        return new StartProtocolLabels(
            DefaultTitle: Localization.Get(DefaultTitleKey),
            ColumnHeaders: headers,
            NoStartTimeCaption: Localization.Get("StartProtocols.NoStartTime"),
            CourseSetterLabel: Localization.Get("Protocols.CourseSetter"),
            ChiefJudgeLabel: Localization.Get("Protocols.ChiefJudge"),
            ChiefSecretaryLabel: Localization.Get("Protocols.ChiefSecretary"),
            JuryLabel: Localization.Get("Protocols.Jury"),
            ColumnHeadersShort: shortHeaders,
            FooterSoftwareName: Localization.Get("Protocols.Footer.Software"),
            FooterGeneratedLabel: Localization.Get("Protocols.Footer.Generated"),
            FooterPageLabel: Localization.Get("Protocols.Footer.Page"));
    }

    // The short (abbreviated) caption key for a column, or null when it has no abbreviation (already short).
    private static string? ShortCaptionKey(StartProtocolColumn column) => column switch
    {
        StartProtocolColumn.FullName => "StartProtocols.Col.Short.FullName",
        StartProtocolColumn.BirthDate => "StartProtocols.Col.Short.BirthDate",
        StartProtocolColumn.Rank => "StartProtocols.Col.Short.Rank",
        StartProtocolColumn.Team => "StartProtocols.Col.Short.Team",
        _ => null
    };

    private string SuggestedFileName(EventDay day)
    {
        var competition = _session.CurrentEvent?.Name;
        if (string.IsNullOrWhiteSpace(competition))
            competition = Localization.Get("Protocols.DefaultName");
        var part = Localization.Get(Kind == StartProtocolKind.Judges
            ? "StartProtocols.NamePart.Judges" : "StartProtocols.NamePart.Regular");
        var stamp = DateTime.Now.ToString("yyyy-MM-dd");
        var baseName = $"{competition} — {part} {Localization.Get("Header.Day")} {day.Number} {stamp}";
        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(invalid, '_');
        return $"{baseName}.docx";
    }

    // The preview's header is the SAME two-way-bound text boxes, so a header-text edit updates the mock-up with
    // no rebuild — typing must not re-run the (expensive) builder or re-render the table, or every keystroke
    // would block the UI. Guarded against firing during ApplySettings (the load).
    private void OnHeaderEdited()
    {
        if (_applyingSettings)
            return;
        AutoSave();
    }

    // Orientation only reshapes the page (table content unchanged), so just push the flag the page-size
    // converter watches — no builder run / table rebuild — then auto-save to the current day.
    partial void OnIsLandscapeChanged(bool value)
    {
        if (_applyingSettings)
            return;
        Preview.IsLandscape = value;
        AutoSave();
    }

    partial void OnCompetitionNameChanged(string value) => OnHeaderEdited();
    partial void OnTitleChanged(string value) => OnHeaderEdited();
    partial void OnSubtitleChanged(string value) => OnHeaderEdited();
    partial void OnVenueChanged(string value) => OnHeaderEdited();
    partial void OnCompetitionTypeChanged(string value) => OnHeaderEdited();
    partial void OnDateTextChanged(string value) => OnHeaderEdited();
}
