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
    }

    /// <summary>Which start protocol this page is currently configuring. Set by the open command before LoadAsync.</summary>
    public StartProtocolKind Kind { get; set; } = StartProtocolKind.Regular;

    public ProtocolPreviewViewModel Preview { get; } = new();

    // The nav/title keys switch with the kind so the shell tab + heading read correctly for each protocol.
    public override string NavKey => Kind == StartProtocolKind.Judges ? "Nav.StartProtocolJudges" : "Nav.StartProtocol";
    public override string TitleKey => Kind == StartProtocolKind.Judges ? "Page.StartProtocolJudges.Title" : "Page.StartProtocol.Title";
    public override string TextKey => Kind == StartProtocolKind.Judges ? "Page.StartProtocolJudges.Text" : "Page.StartProtocol.Text";

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
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Text));
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

        _applyingSettings = true;
        try
        {
            SeedHeaderDefaults(_competitionInfo);
        }
        finally
        {
            _applyingSettings = false;
        }

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
        foreach (var c in settings.Columns)
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

    private void SeedHeaderDefaults(CompetitionInfo? info)
    {
        if (string.IsNullOrWhiteSpace(CompetitionName))
        {
            var name = !string.IsNullOrWhiteSpace(info?.Name) ? info!.Name : _session.CurrentEvent?.Name;
            if (!string.IsNullOrWhiteSpace(name))
                CompetitionName = name!;
        }
        if (string.IsNullOrWhiteSpace(Subtitle) && !string.IsNullOrWhiteSpace(info?.Organisation))
            Subtitle = info!.Organisation;
        if (string.IsNullOrWhiteSpace(Venue) && !string.IsNullOrWhiteSpace(info?.Venue))
            Venue = info!.Venue;
        if (string.IsNullOrWhiteSpace(DateText) && SelectedDay?.Day?.Date is { } date)
            DateText = date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
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
        _ = Task.Run(() => _editor.SaveStartProtocolSettingsAsync(day.Id, kind, settings));
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
        var settings = BuildSettings();

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
            Preview.Columns.Add(new ProtocolPreviewColumn(visible[i].ToString(), document.ColumnHeaders[i]));

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
                section.GroupName, string.Empty, rows, section.CourseSetterText));
        }
        Preview.IsEmpty = Preview.Sections.Count == 0 || Preview.Sections.All(s => s.Rows.Count == 0);

        Preview.Officials.Clear();
        foreach (var official in document.Officials)
            Preview.Officials.Add($"{official.Role}:  {official.NameWithCategory}");
        Preview.HasOfficials = Preview.Officials.Count > 0;
    }

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

    /// <summary>
    /// Builds the start protocol for the selected day and returns the .docx bytes + a suggested file name,
    /// or null when there is nothing to export. The View runs the save dialog. Also persists the template.
    /// </summary>
    public async Task<ProtocolExportResult?> GenerateAsync()
    {
        if (_session.CurrentEvent is null || SelectedDay?.Day is not { } day)
            return null;

        var settings = BuildSettings();
        var labels = BuildLabels();
        var kind = Kind;

        var bytes = await _busy.RunAsync(async () =>
        {
            await _editor.SaveStartProtocolSettingsAsync(day.Id, kind, settings);
            var data = await _editor.GetStartProtocolDataAsync(day.Id);
            var document = _builder.Build(data, settings, kind, labels);
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
        foreach (StartProtocolColumn column in Enum.GetValues<StartProtocolColumn>())
            headers[column] = Localization.Get(CaptionKey(column));

        return new StartProtocolLabels(
            DefaultTitle: Localization.Get(DefaultTitleKey),
            ColumnHeaders: headers,
            NoStartTimeCaption: Localization.Get("StartProtocols.NoStartTime"),
            CourseSetterLabel: Localization.Get("Protocols.CourseSetter"),
            ChiefJudgeLabel: Localization.Get("Protocols.ChiefJudge"),
            ChiefSecretaryLabel: Localization.Get("Protocols.ChiefSecretary"),
            JuryLabel: Localization.Get("Protocols.Jury"));
    }

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
