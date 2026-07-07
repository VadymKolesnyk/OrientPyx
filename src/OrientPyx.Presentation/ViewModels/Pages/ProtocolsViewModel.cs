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
/// «Протоколи результатів»: configures and exports a results protocol to a Word (.docx) document. The
/// template (page orientation, the ordered/visible column set, the header text) is stored <b>per competition
/// day</b> in the event database via <see cref="ICompetitionEditorService"/>; a day with no saved template
/// is seeded from the application-level default (the Settings page layout, via <see cref="IAppSettingsService"/>)
/// so a fresh day starts from the configured template and is saved per day thereafter. The header text fields
/// fall back to the current competition's metadata when left blank.
///
/// The page shows a live <see cref="ProtocolPreviewViewModel"/> — the actual document mock-up (header + one
/// group section as a real table, filled with real participants of the selected day) built from the same
/// <see cref="IResultProtocolBuilder"/> the export uses. Reordering a column (drag its header) or toggling its
/// visibility rebuilds the preview immediately. Generating builds the document for the selected day and hands
/// the .docx bytes to the View, which runs the save dialog. Choosing a day here never changes the active
/// session day (a protocol is read-only over a day's results).
/// </summary>
public sealed partial class ProtocolsViewModel : PageViewModelBase, IProtocolPreviewHost
{
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IAppSettingsService _appSettings;
    private readonly IResultProtocolBuilder _builder;
    private readonly IResultProtocolWriter _writer;
    private readonly IWinnersPrintBuilder _winnersBuilder;
    private readonly IWinnersPrintFlow _winnersPrint;
    private readonly IBusyService _busy;

    /// <summary>How many prize places the winners printout includes per group (a standard podium).</summary>
    private const int WinnersTopPlaces = 3;

    /// <summary>How many participant rows the preview shows — enough to fill an A4 page; the page clips the rest.</summary>
    private const int PreviewRowCap = 40;

    // Guards SelectedDay sync during LoadAsync so the setter doesn't fight the load.
    private bool _syncingDay;

    // Suppresses per-field preview refreshes while a template is being applied (the preview is rebuilt once
    // at the end of the load instead of on every header/column assignment).
    private bool _applyingSettings;

    // The protocol data for the selected day, cached so a column reorder/hide can re-render the preview
    // without a DB round-trip. Refreshed on a day change. Null until first loaded.
    private ResultProtocolData? _previewData;

    public ProtocolsViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IAppSettingsService appSettings,
        IResultProtocolBuilder builder,
        IResultProtocolWriter writer,
        IWinnersPrintBuilder winnersBuilder,
        IWinnersPrintFlow winnersPrint,
        IBusyService busy)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _appSettings = appSettings;
        _builder = builder;
        _writer = writer;
        _winnersBuilder = winnersBuilder;
        _winnersPrint = winnersPrint;
        _busy = busy;

        // Singleton VM: reload the day list + header defaults on a competition/day change (marshal to UI).
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
    }

    /// <summary>The live document preview shown on the page. Rebuilt by <see cref="RefreshPreview"/>.</summary>
    public ProtocolPreviewViewModel Preview { get; } = new();

    public override string NavKey => "Nav.Protocols";
    public override string TitleKey => "Page.Protocols.Title";
    public override string TextKey => "Page.Protocols.Text";

    // Lucide "file-text".
    public override string IconData =>
        "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z M14 2v4a2 2 0 0 0 2 2h4 M16 13H8 M16 17H8 M10 9H8";

    // ── Day picker (does NOT touch the session) ──────────────────────────────────────────────────────

    public ObservableCollection<DayOption> DayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _selectedDay;

    public bool ShowDaySelector => DayOptions.Count > 1;

    // ── Settings ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The configurable columns, in on-page order. Reordered with up/down; toggled visible.</summary>
    public ObservableCollection<ProtocolColumnItemViewModel> Columns { get; } = [];

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
    // user has typed nothing. A blank field falls back to this value at build/export time (so the printed
    // protocol carries the competition's own metadata); when the placeholder is itself empty (the DB has no
    // value) the field stays blank everywhere — the watermark is then a hint label, not inserted text.

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

    /// <summary>Confirmation flash after Save (the green "saved" hint), like the Settings page.</summary>
    [ObservableProperty]
    private bool _settingsSaved;

    // The current competition's metadata, used to seed blank header fields on each day load.
    private CompetitionInfo? _competitionInfo;

    /// <summary>The localized caption for a column, used to build the list items and the builder labels.</summary>
    private static string CaptionKey(ProtocolColumn column) => column switch
    {
        ProtocolColumn.Sequence => "Protocols.Col.Sequence",
        ProtocolColumn.Number => "Protocols.Col.Number",
        ProtocolColumn.FullName => "Protocols.Col.FullName",
        ProtocolColumn.BirthDate => "Protocols.Col.BirthDate",
        ProtocolColumn.Club => "Protocols.Col.Club",
        ProtocolColumn.Region => "Protocols.Col.Region",
        ProtocolColumn.Dussh => "Protocols.Col.Dussh",
        ProtocolColumn.Coach => "Protocols.Col.Coach",
        ProtocolColumn.Rank => "Protocols.Col.Rank",
        ProtocolColumn.Result => "Protocols.Col.Result",
        ProtocolColumn.Place => "Protocols.Col.Place",
        ProtocolColumn.Score => "Protocols.Col.Score",
        ProtocolColumn.Points => "Protocols.Col.Points",
        ProtocolColumn.AwardedRank => "Protocols.Col.AwardedRank",
        _ => "Protocols.Col.FullName"
    };

    public async Task LoadAsync()
    {
        // Load the day list + competition info first (no day chosen yet), then load the chosen day's
        // template. All DB reads happen off the UI thread (SQLite has no real async I/O); UI state is
        // written only after the await.
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

    // Loads the selected day's template (seeding from the app-level default when the day has none), seeds
    // the header defaults, fetches the day's protocol data, and renders the preview. Called on first load
    // and on every day change, so each day shows its own saved layout.
    private async Task LoadDayTemplateAsync()
    {
        if (SelectedDay?.Day is not { } day)
        {
            _previewData = null;
            RefreshPreview();
            return;
        }

        var (settings, data) = await _busy.RunAsync(async () =>
        {
            // The day's saved template, or the app-level default when the day has none yet.
            var s = await _editor.GetResultProtocolSettingsAsync(day.Id)
                    ?? await _appSettings.GetResultProtocolSettingsAsync();
            var d = await _editor.GetResultProtocolDataAsync(day.Id);
            return (s, d);
        });

        _previewData = data;
        ApplySettings(settings);
        ResolveHeaderPlaceholders(_competitionInfo);
        RefreshPreview();
    }

    private void ApplySettings(ResultProtocolSettings settings)
    {
        _applyingSettings = true; // suppress per-field preview refreshes; RefreshPreview runs once after the load
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

        // Reconcile against the full column set: a template saved before a column existed (e.g. «Очки»
        // added later) is missing it, so append any absent column (visible) in enum order. Keeps old
        // per-day templates current without a manual reset.
        var present = settings.Columns.Select(c => c.Column).ToHashSet();
        foreach (ProtocolColumn column in Enum.GetValues<ProtocolColumn>())
            if (present.Add(column))
                settings.Columns.Add(new ProtocolColumnSetting { Column = column, Visible = true });

        Columns.Clear();
        foreach (var c in settings.Columns)
        {
            var item = new ProtocolColumnItemViewModel(c.Column, CaptionKey(c.Column), c.Visible, Localization);
            // Toggling a column's visibility re-renders the preview live and auto-saves to the current day.
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ProtocolColumnItemViewModel.Visible))
                {
                    RefreshPreview();
                    AutoSave();
                }
            };
            Columns.Add(item);
        }
    }

    // Resolves the header watermark for each blank-able field. The per-day fields (date, venue, competition
    // type) come from the SELECTED DAY first and fall back to the competition; the rest from the competition.
    // These are shown as the TextBox placeholders and used as the build-time fallback for a field the user
    // left blank — never written into the editable field, so an untouched field stays empty and a missing DB
    // value yields an empty header cell, not the placeholder hint.
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
        // A day change here only repoints the protocol target; it must NOT switch the session day. Load the
        // newly chosen day's own template + preview data (each day has its own saved layout).
        if (_syncingDay)
            return;
        _ = LoadDayTemplateAsync();
    }

    // Moving a column reorders the on-page order, re-renders the preview, and auto-saves to the current day.
    [RelayCommand]
    private void MoveColumnUp(ProtocolColumnItemViewModel? item)
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
    private void MoveColumnDown(ProtocolColumnItemViewModel? item)
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

    /// <summary>
    /// Moves a column next to another in the on-page order. Used by the preview's drag-reorder, which names the
    /// dragged column and the target column it was dropped on (and which half). Both keys are resolved against
    /// the FULL column list, so hidden columns can't skew the destination. Re-renders the preview.
    /// </summary>
    public void MoveColumnByKey(string draggedKey, string targetKey, bool insertAfter)
    {
        if (!Enum.TryParse<ProtocolColumn>(draggedKey, out var dragged) ||
            !Enum.TryParse<ProtocolColumn>(targetKey, out var target))
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

    private int IndexOf(ProtocolColumn column)
    {
        for (var i = 0; i < Columns.Count; i++)
            if (Columns[i].Column == column)
                return i;
        return -1;
    }

    // Auto-saves the current template to the SELECTED DAY on every change (column reorder/visibility, header
    // text, orientation). Fire-and-forget off the UI thread; guarded so it doesn't fire while a template is
    // being applied during a load. No busy overlay — this runs silently on each edit.
    private void AutoSave()
    {
        if (_applyingSettings)
            return;
        // Any edit invalidates the "saved as default" flash from a previous app-default save.
        SettingsSaved = false;
        if (SelectedDay?.Day is not { } day)
            return;
        var settings = BuildSettings();
        // Observe the task's exception here: auto-save is fire-and-forget, so an unhandled failure would
        // otherwise surface on the finalizer thread as an UnobservedTaskException and crash the app.
        _ = Task.Run(async () =>
        {
            try
            {
                await _editor.SaveResultProtocolSettingsAsync(day.Id, settings);
            }
            catch
            {
                // Best-effort auto-save; the next edit (or an explicit export) will persist again.
            }
        });
    }

    // "Save for next competitions": stores the current layout as the APPLICATION-LEVEL default, so a new day
    // (in this or any future competition) that has no saved template of its own seeds from this. The per-day
    // template is already auto-saved on every edit, so this button only sets the shared default.
    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var settings = BuildSettings();
        await _busy.RunAsync(() => _appSettings.SaveResultProtocolSettingsAsync(settings));
        SettingsSaved = true;
    }

    // Rebuilds the live preview from the cached day data and the current template (columns + header). Uses
    // the same builder the export uses, so the preview matches the .docx; the body is capped to a few rows.
    private void RefreshPreview()
    {
        // Build with the placeholder fallbacks folded in so the preview table mirrors the .docx. (The header
        // text itself is shown by the inline TextBoxes bound to the raw VM fields + their watermarks, not by
        // these Preview.* properties — kept in sync for completeness.)
        var settings = BuildDocumentSettings();

        Preview.IsLandscape = settings.Orientation == ProtocolOrientation.Landscape;
        Preview.CompetitionName = settings.CompetitionName;
        Preview.Title = settings.Title.Length > 0 ? settings.Title : Localization.Get("Protocols.DefaultTitle");
        Preview.Subtitle = settings.Subtitle;
        Preview.DateText = settings.DateText;
        Preview.CompetitionType = settings.CompetitionType;
        Preview.Venue = settings.Venue;

        var data = _previewData ?? new ResultProtocolData([]);
        var document = _builder.Build(data, settings, BuildLabels());

        Preview.Columns.Clear();
        var visible = settings.Columns.Where(c => c.Visible).Select(c => c.Column).ToList();
        if (visible.Count == 0)
            visible.Add(ProtocolColumn.FullName); // mirrors the builder's "always one column" guard
        for (var i = 0; i < visible.Count && i < document.ColumnHeaders.Count; i++)
            Preview.Columns.Add(new ProtocolPreviewColumn(visible[i].ToString(), document.ColumnHeaders[i],
                i < document.ColumnHeadersShort.Count ? document.ColumnHeadersShort[i] : string.Empty,
                i < document.ColumnBodyWrap.Count && document.ColumnBodyWrap[i],
                i < document.ColumnShrinkPriority.Count ? document.ColumnShrinkPriority[i] : 1));

        // Render the group sections exactly as the .docx stacks them (caption + sub-caption + table), capping
        // the TOTAL body rows across sections so the page mock-up fills but stays cheap to build.
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
                section.GroupName, BuildSubcaption(section), rows, section.CourseSetterText,
                rankCalculation: section.RankCalculationText));
        }
        Preview.IsEmpty = Preview.Sections.Count == 0 || Preview.Sections.All(s => s.Rows.Count == 0);

        // Officials are deliberately NOT shown in the on-screen preview — they're a fixed signature block at
        // the very bottom of the printed sheet, off the visible mock-up area, and only clutter the preview.
        Preview.Officials.Clear();
        Preview.HasOfficials = false;
    }

    // Joins a section's non-blank course facts into one " · "-separated sub-caption line for the preview.
    private static string BuildSubcaption(ResultProtocolSection section)
    {
        var parts = new[] { section.DistanceText, section.ControlCountText, section.TimeLimitText }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join(" · ", parts);
    }

    // The settings actually persisted on the day: the user's typed values, NOT the resolved placeholders, so a
    // field left blank stays blank in the saved template (the watermark is a hint, never stored).
    private ResultProtocolSettings BuildSettings() => new()
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
    // also empty stays empty — the header cell is then simply blank. Title keeps its own localized default
    // (handled by the builder), so it is left as-is here.
    private ResultProtocolSettings BuildDocumentSettings()
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
    /// Builds the protocol for the selected day and returns the .docx bytes + a suggested file name, or
    /// null when there is nothing to export (no competition / no day). The View runs the save dialog.
    /// Generating also persists the current settings so the layout the user sees is the one saved.
    /// </summary>
    public async Task<ProtocolExportResult?> GenerateAsync()
    {
        if (_session.CurrentEvent is null || SelectedDay?.Day is not { } day)
            return null;

        var settings = BuildSettings();          // persisted: the user's typed values (blanks stay blank)
        var documentSettings = BuildDocumentSettings(); // built: blanks folded to the competition defaults
        var labels = BuildLabels();

        var bytes = await _busy.RunAsync(async () =>
        {
            await _editor.SaveResultProtocolSettingsAsync(day.Id, settings);
            var data = await _editor.GetResultProtocolDataAsync(day.Id);
            var document = _builder.Build(data, documentSettings, labels);
            return _writer.Write(document);
        });
        SettingsSaved = true;

        return new ProtocolExportResult(bytes, SuggestedFileName(day));
    }

    /// <summary>
    /// «Друк переможців»: prints the top-3 prize places of each group for the selected day to the read-out
    /// thermal printer (ties kept whole — "2 третіх" prints both). Reuses the same computed day results the
    /// protocol shows; a no-op when no competition/day is selected.
    /// </summary>
    [RelayCommand]
    private async Task PrintWinnersAsync()
    {
        if (_session.CurrentEvent is null || SelectedDay?.Day is not { } day)
            return;

        var header = BuildWinnersHeader(day);
        var labels = WinnersPrintLabelsFactory.Create(Localization);

        var document = await _busy.RunAsync(async () =>
        {
            var data = await _editor.GetResultProtocolDataAsync(day.Id);
            return _winnersBuilder.BuildForDay(data, header, labels, WinnersTopPlaces);
        });

        await _winnersPrint.PrintAsync(document);
    }

    // The winners-printout header: the resolved competition name + protocol title, and a "День N — date" line.
    private WinnersPrintHeader BuildWinnersHeader(EventDay day)
    {
        var settings = BuildDocumentSettings();
        var name = settings.CompetitionName.Length > 0 ? settings.CompetitionName : CompetitionNamePlaceholder;
        var title = settings.Title.Length > 0 ? settings.Title : Localization.Get("Protocols.DefaultTitle");
        var dateParts = new[] { $"{Localization.Get("Header.Day")} {day.Number}", settings.DateText }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        return new WinnersPrintHeader(name, title, string.Join(" — ", dateParts));
    }

    private ProtocolLabels BuildLabels()
    {
        var headers = new Dictionary<ProtocolColumn, string>();
        var shortHeaders = new Dictionary<ProtocolColumn, string>();
        foreach (ProtocolColumn column in Enum.GetValues<ProtocolColumn>())
        {
            headers[column] = Localization.Get(CaptionKey(column));
            if (ShortCaptionKey(column) is { } key)
                shortHeaders[column] = Localization.Get(key);
        }

        return new ProtocolLabels(
            DefaultTitle: Localization.Get("Protocols.DefaultTitle"),
            ColumnHeaders: headers,
            DistanceLabel: Localization.Get("Protocols.Section.Distance"),
            ControlCountLabel: Localization.Get("Protocols.Section.ControlCount"),
            TimeLimitLabel: Localization.Get("Protocols.Section.TimeLimit"),
            CourseSetterLabel: Localization.Get("Protocols.CourseSetter"),
            ChiefJudgeLabel: Localization.Get("Protocols.ChiefJudge"),
            ChiefSecretaryLabel: Localization.Get("Protocols.ChiefSecretary"),
            JuryLabel: Localization.Get("Protocols.Jury"),
            ColumnHeadersShort: shortHeaders,
            CourseClassLabel: Localization.Get("Protocols.RankCalc.CourseClass"),
            CompetitionRankLabel: Localization.Get("Protocols.RankCalc.CompetitionRank"),
            RankPointsUnitLabel: Localization.Get("Protocols.RankCalc.PointsUnit"),
            FooterSoftwareName: Localization.Get("Protocols.Footer.Software"),
            FooterGeneratedLabel: Localization.Get("Protocols.Footer.Generated"),
            FooterPageLabel: Localization.Get("Protocols.Footer.Page"));
    }

    // The short (abbreviated) caption key for a column, or null when the column has no abbreviation (its full
    // caption is already short). Used when a column is too narrow to fit the full header on one line.
    private static string? ShortCaptionKey(ProtocolColumn column) => column switch
    {
        ProtocolColumn.FullName => "Protocols.Col.Short.FullName",
        ProtocolColumn.BirthDate => "Protocols.Col.Short.BirthDate",
        ProtocolColumn.Coach => "Protocols.Col.Short.Coach",
        ProtocolColumn.Rank => "Protocols.Col.Short.Rank",
        ProtocolColumn.Result => "Protocols.Col.Short.Result",
        ProtocolColumn.Place => "Protocols.Col.Short.Place",
        ProtocolColumn.AwardedRank => "Protocols.Col.Short.AwardedRank",
        _ => null
    };

    // "<competition> — протокол <День N> <date>.docx", sanitised for the save dialog.
    private string SuggestedFileName(EventDay day)
    {
        var competition = _session.CurrentEvent?.Name;
        if (string.IsNullOrWhiteSpace(competition))
            competition = Localization.Get("Protocols.DefaultName");
        var part = Localization.Get("Protocols.NamePart");
        var stamp = DateTime.Now.ToString("yyyy-MM-dd");
        var baseName = $"{competition} — {part} {Localization.Get("Header.Day")} {day.Number} {stamp}";
        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(invalid, '_');
        return $"{baseName}.docx";
    }

    // Clear the "saved" flash on a header-text edit. The preview's header is the SAME two-way-bound text
    // boxes, so the document mock-up updates with no rebuild — typing must not re-run the (expensive) builder
    // or re-render the table grid, or every keystroke would block the UI. The guard stops these firing during
    // ApplySettings (the load), where the preview is rendered once at the end instead.
    private void OnHeaderEdited()
    {
        if (_applyingSettings)
            return;
        AutoSave();
    }

    // Orientation only reshapes the page (the table content is unchanged), so just push the flag the page-size
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

/// <summary>The result of building a protocol: the .docx bytes and a suggested save file name.</summary>
public sealed record ProtocolExportResult(byte[] Bytes, string SuggestedFileName);
