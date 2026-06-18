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
/// «Протоколи результатів»: configures and exports a results protocol to a Word (.docx) document. The
/// settings (page orientation, the ordered/visible column set, the header text) are application-level and
/// persisted via <see cref="IAppSettingsService"/>; the header text fields fall back to the current
/// competition's metadata when left blank. Generating builds the document for the selected day and hands
/// the .docx bytes to the View, which runs the save dialog. Choosing a day here never changes the active
/// session day (a protocol is read-only over a day's results).
/// </summary>
public sealed partial class ProtocolsViewModel : PageViewModelBase
{
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IAppSettingsService _appSettings;
    private readonly IResultProtocolBuilder _builder;
    private readonly IResultProtocolWriter _writer;
    private readonly IBusyService _busy;

    // Guards SelectedDay sync during LoadAsync so the setter doesn't fight the load.
    private bool _syncingDay;

    public ProtocolsViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IAppSettingsService appSettings,
        IResultProtocolBuilder builder,
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

        // Singleton VM: reload the day list + header defaults on a competition/day change (marshal to UI).
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
    }

    public override string NavKey => "Nav.Protocols";
    public override string TitleKey => "Page.Protocols.Title";
    public override string TextKey => "Page.Protocols.Text";

    public override string IconData =>
        "M6,2 h8 l4,4 v14 a1,1 0 0 1 -1,1 h-11 a1,1 0 0 1 -1,-1 v-17 a1,1 0 0 1 1,-1 z M14,2 v4 h4 M8,12 h8 M8,16 h8 M8,8 h3";

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
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _venue = string.Empty;

    [ObservableProperty]
    private string _competitionType = string.Empty;

    [ObservableProperty]
    private string _dateText = string.Empty;

    /// <summary>Confirmation flash after Save (the green "saved" hint), like the Settings page.</summary>
    [ObservableProperty]
    private bool _settingsSaved;

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
        _ => "Protocols.Col.FullName"
    };

    public async Task LoadAsync()
    {
        // All DB reads in one busy scope (off the UI thread; SQLite has no real async I/O); UI state is
        // written only after the await.
        var (settings, days, info) = await _busy.RunAsync(async () =>
        {
            var s = await _appSettings.GetResultProtocolSettingsAsync();
            var d = await _editor.GetDaysAsync();
            var i = await _editor.GetInfoAsync();
            return (s, d, i);
        });

        ApplySettings(settings);

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

        // Seed the header defaults from the competition / selected day when the saved fields are blank, so
        // the user sees what the protocol will use without having to fill anything in.
        SeedHeaderDefaults(info);
    }

    private void ApplySettings(ResultProtocolSettings settings)
    {
        IsLandscape = settings.Orientation == ProtocolOrientation.Landscape;
        Title = settings.Title;
        Subtitle = settings.Subtitle;
        Venue = settings.Venue;
        CompetitionType = settings.CompetitionType;
        DateText = settings.DateText;

        Columns.Clear();
        foreach (var c in settings.Columns)
            Columns.Add(new ProtocolColumnItemViewModel(c.Column, CaptionKey(c.Column), c.Visible, Localization));
    }

    // Fills blank header fields with the competition's own values (and the selected day's date) so the
    // generated protocol always has a header. Only fills what the user hasn't already typed/saved.
    private void SeedHeaderDefaults(CompetitionInfo? info)
    {
        if (string.IsNullOrWhiteSpace(Subtitle) && !string.IsNullOrWhiteSpace(info?.Organisation))
            Subtitle = info!.Organisation;
        if (string.IsNullOrWhiteSpace(Venue) && !string.IsNullOrWhiteSpace(info?.Venue))
            Venue = info!.Venue;
        if (string.IsNullOrWhiteSpace(DateText) && SelectedDay?.Day?.Date is { } date)
            DateText = date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    }

    partial void OnSelectedDayChanged(DayOption? value)
    {
        // A day change here only repoints the protocol target; it must NOT switch the session day. Refresh
        // the default date to the newly chosen day when the date field is still empty.
        if (_syncingDay)
            return;
        if (string.IsNullOrWhiteSpace(DateText) && value?.Day?.Date is { } date)
            DateText = date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    }

    // Moving a column reorders the persisted on-page order; saved on the next Save.
    [RelayCommand]
    private void MoveColumnUp(ProtocolColumnItemViewModel? item)
    {
        if (item is null)
            return;
        var i = Columns.IndexOf(item);
        if (i > 0)
            Columns.Move(i, i - 1);
    }

    [RelayCommand]
    private void MoveColumnDown(ProtocolColumnItemViewModel? item)
    {
        if (item is null)
            return;
        var i = Columns.IndexOf(item);
        if (i >= 0 && i < Columns.Count - 1)
            Columns.Move(i, i + 1);
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var settings = BuildSettings();
        await _busy.RunAsync(() => _appSettings.SaveResultProtocolSettingsAsync(settings));
        SettingsSaved = true;
    }

    private ResultProtocolSettings BuildSettings() => new()
    {
        Orientation = IsLandscape ? ProtocolOrientation.Landscape : ProtocolOrientation.Portrait,
        Title = Title?.Trim() ?? string.Empty,
        Subtitle = Subtitle?.Trim() ?? string.Empty,
        Venue = Venue?.Trim() ?? string.Empty,
        CompetitionType = CompetitionType?.Trim() ?? string.Empty,
        DateText = DateText?.Trim() ?? string.Empty,
        Columns = Columns.Select(c => c.ToSetting()).ToList()
    };

    /// <summary>
    /// Builds the protocol for the selected day and returns the .docx bytes + a suggested file name, or
    /// null when there is nothing to export (no competition / no day). The View runs the save dialog.
    /// Generating also persists the current settings so the layout the user sees is the one saved.
    /// </summary>
    public async Task<ProtocolExportResult?> GenerateAsync()
    {
        if (_session.CurrentEvent is null || SelectedDay?.Day is not { } day)
            return null;

        var settings = BuildSettings();
        var labels = BuildLabels();

        var bytes = await _busy.RunAsync(async () =>
        {
            await _appSettings.SaveResultProtocolSettingsAsync(settings);
            var data = await _editor.GetResultProtocolDataAsync(day.Id);
            var document = _builder.Build(data, settings, labels);
            return _writer.Write(document);
        });
        SettingsSaved = true;

        return new ProtocolExportResult(bytes, SuggestedFileName(day));
    }

    private ProtocolLabels BuildLabels()
    {
        var headers = new Dictionary<ProtocolColumn, string>();
        foreach (ProtocolColumn column in Enum.GetValues<ProtocolColumn>())
            headers[column] = Localization.Get(CaptionKey(column));

        return new ProtocolLabels(
            DefaultTitle: Localization.Get("Protocols.DefaultTitle"),
            ColumnHeaders: headers,
            DistanceLabel: Localization.Get("Protocols.Section.Distance"),
            ControlCountLabel: Localization.Get("Protocols.Section.ControlCount"),
            TimeLimitLabel: Localization.Get("Protocols.Section.TimeLimit"));
    }

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

    // Clear the "saved" flash on any settings edit so it only shows right after a save.
    partial void OnIsLandscapeChanged(bool value) => SettingsSaved = false;
    partial void OnTitleChanged(string value) => SettingsSaved = false;
    partial void OnSubtitleChanged(string value) => SettingsSaved = false;
    partial void OnVenueChanged(string value) => SettingsSaved = false;
    partial void OnCompetitionTypeChanged(string value) => SettingsSaved = false;
    partial void OnDateTextChanged(string value) => SettingsSaved = false;
}

/// <summary>The result of building a protocol: the .docx bytes and a suggested save file name.</summary>
public sealed record ProtocolExportResult(byte[] Bytes, string SuggestedFileName);
