using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using NameKey = OrientPyx.Plugins.Sdk.Toolkit.TextKey;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// «Експорт IOF XML»: writes the IOF Data Standard 3.0 <c>ResultList</c> (results with splits) or <c>StartList</c> of
/// one day — or of several days in one file (Event/Race + <c>raceNumber</c>) — for WinSplits Online, Livelox,
/// RouteGadget and ranking systems. Reached from «Результати» (results) and «Старт» (start list); one singleton, the
/// kind set by the open command. Picking a day switches the session's day (like «Спліти (HTML)»). The options are kept
/// per competition (<see cref="IofExportSettings"/>); the group choice is not (it depends on the days).
/// </summary>
public sealed partial class IofExportViewModel : PageViewModelBase
{
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IIofExportBuilder _builder;
    private readonly IIofXmlWriter _writer;
    private readonly IBusyService _busy;
    private readonly IDayLockService _dayLock;
    private readonly IAppSettingsService _appSettings;

    // Guards the property handlers while a load rebuilds the lists.
    private bool _loading;

    // The groups the user unticked, by name — kept across reloads and day changes, so a new group starts ticked.
    private readonly HashSet<string> _unticked = new(NameKey.Comparer);

    public IofExportViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IIofExportBuilder builder,
        IIofXmlWriter writer,
        IBusyService busy,
        IDayLockService dayLock,
        IExportFileSaver fileSaver,
        IToastService toasts,
        IAppSettingsService appSettings,
        IDataChangeBus bus,
        IWorkspaceMode mode)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _builder = builder;
        _writer = writer;
        _busy = busy;
        _dayLock = dayLock;
        _appSettings = appSettings;
        FileSaver = fileSaver;
        Toasts = toasts;

        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
        // Live data (another station's writes): the days, the groups of a day, the competition name.
        ObserveData(bus, session, mode,
            DataArea.Days, DataArea.Groups, DataArea.GroupDaySettings, DataArea.Competition);
    }

    /// <summary>Writes the file the user picked (handles a locked target). Used by the view.</summary>
    public IExportFileSaver FileSaver { get; }

    /// <summary>The «збережено» toast. Used by the view.</summary>
    public IToastService Toasts { get; }

    public override string NavKey => "Nav.IofExport";
    public override string TitleKey => "Page.IofExport.Title";
    public override string TextKey => "Page.IofExport.Text";
    public override string HelpKeyPrefix => "Help.IofExport";

    // Lucide "file-code".
    public override string IconData =>
        "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z M14 2v6h6 M10 13l-2 2 2 2 M14 17l2-2-2-2";

    // ── What to export

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResults), nameof(IsStartList))]
    private IofListKind _kind = IofListKind.ResultList;

    public bool IsResults
    {
        get => Kind == IofListKind.ResultList;
        set { if (value) Kind = IofListKind.ResultList; }
    }

    public bool IsStartList
    {
        get => Kind == IofListKind.StartList;
        set { if (value) Kind = IofListKind.StartList; }
    }

    // ── Days

    public ObservableCollection<DayOption> DayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _selectedDay;

    /// <summary>The «Дні» card: only a competition of several days can put several in one file.</summary>
    public bool HasSeveralDays => DayOptions.Count > 1;

    /// <summary>The day picker in the heading — one day per file.</summary>
    public bool ShowDaySelector => HasSeveralDays && !MultiDay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SingleDay), nameof(ShowDaySelector), nameof(CanGenerate))]
    private bool _multiDay;

    public bool SingleDay
    {
        get => !MultiDay;
        set { if (value) MultiDay = false; }
    }

    /// <summary>The days ticked for a file of several days.</summary>
    public ObservableCollection<IofDayToggle> DayToggles { get; } = [];

    // ── Groups

    public ObservableCollection<GroupToggle> Groups { get; } = [];

    // ── Options

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OrganisationClub))]
    private bool _organisationRegion;

    public bool OrganisationClub
    {
        get => !OrganisationRegion;
        set { if (value) OrganisationRegion = false; }
    }

    [ObservableProperty]
    private bool _includeBirthDates = true;

    /// <summary>A group and a day (one, or at least one ticked) are chosen.</summary>
    public bool CanGenerate =>
        Groups.Any(g => g.IsSelected)
        && (MultiDay ? DayToggles.Any(d => d.IsSelected) : SelectedDay?.Day is not null);

    protected override Task ReloadAsync(CancellationToken cancellationToken = default) => LoadAsync();

    public async Task LoadAsync()
    {
        if (_session.CurrentEvent is null)
            return;
        var (days, settings) = await _busy.RunAsync(async () =>
        {
            var d = await _editor.GetDaysAsync();
            var s = await _editor.GetIofExportSettingsAsync();
            return (d, s);
        });

        _loading = true;
        try
        {
            DayOptions.Clear();
            foreach (var day in days.OrderBy(d => d.Number))
                DayOptions.Add(new DayOption(day, Localization));
            var current = _session.CurrentDay?.Number;
            SelectedDay = DayOptions.FirstOrDefault(o => o.Number == current) ?? DayOptions.FirstOrDefault();

            settings ??= new IofExportSettings();
            OrganisationRegion = settings.Organisation == IofOrganisationSource.Region;
            IncludeBirthDates = settings.IncludeBirthDates;
            MultiDay = settings.MultiDay && days.Count > 1;

            foreach (var toggle in DayToggles)
                toggle.PropertyChanged -= OnDayToggleChanged;
            DayToggles.Clear();
            var ticked = settings.DayIds.Count > 0 ? settings.DayIds.ToHashSet() : null;
            foreach (var day in days.OrderBy(d => d.Number))
            {
                var toggle = new IofDayToggle(day, $"{Localization.Get("Header.Day")} {day.Number}")
                {
                    IsSelected = ticked is null || ticked.Contains(day.Id)
                };
                toggle.PropertyChanged += OnDayToggleChanged;
                DayToggles.Add(toggle);
            }
        }
        finally
        {
            _loading = false;
        }
        OnPropertyChanged(nameof(HasSeveralDays));
        OnPropertyChanged(nameof(ShowDaySelector));
        await LoadGroupsAsync();
    }

    // The groups of the exported days (a file of several days: every group of the ticked days, by name), each ticked
    // unless the user unticked it before.
    private async Task LoadGroupsAsync()
    {
        var dayIds = ExportedDays().Select(d => d.Id).ToList();
        var rows = await _busy.RunAsync(async () =>
        {
            var all = new List<string>();
            foreach (var dayId in dayIds)
                all.AddRange((await _editor.GetGroupsForDayAsync(dayId)).Select(r => r.Name.Trim()));
            return all;
        });

        foreach (var group in Groups)
            group.PropertyChanged -= OnGroupToggleChanged;
        Groups.Clear();
        foreach (var name in rows.Where(n => n.Length > 0).Distinct(NameKey.Comparer))
        {
            var toggle = new GroupToggle(name) { IsSelected = !_unticked.Contains(name) };
            toggle.PropertyChanged += OnGroupToggleChanged;
            Groups.Add(toggle);
        }
        OnPropertyChanged(nameof(CanGenerate));
    }

    // The days the file is of: the ticked ones (several days), else the picked one.
    private IReadOnlyList<EventDay> ExportedDays() =>
        MultiDay
            ? DayToggles.Where(d => d.IsSelected).Select(d => d.Day).OrderBy(d => d.Number).ToList()
            : SelectedDay?.Day is { } day ? [day] : [];

    private void OnGroupToggleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not GroupToggle toggle || e.PropertyName != nameof(GroupToggle.IsSelected))
            return;
        if (toggle.IsSelected)
            _unticked.Remove(toggle.Name);
        else
            _unticked.Add(toggle.Name);
        OnPropertyChanged(nameof(CanGenerate));
    }

    private void OnDayToggleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading || e.PropertyName != nameof(IofDayToggle.IsSelected))
            return;
        OnPropertyChanged(nameof(CanGenerate));
        SaveSettings();
        _ = LoadGroupsAsync();
    }

    // The day picker switches the session's day (the other day pages follow); the reload re-selects it.
    partial void OnSelectedDayChanged(DayOption? value)
    {
        if (_loading || value?.Day is null)
            return;
        if (_session.CurrentDay?.Number == value.Number)
        {
            _ = LoadGroupsAsync();
            return;
        }
        _ = _busy.RunAsync(() => _session.SetCurrentDayAsync(value.Day));
    }

    partial void OnMultiDayChanged(bool value)
    {
        if (_loading)
            return;
        SaveSettings();
        _ = LoadGroupsAsync();
    }

    partial void OnOrganisationRegionChanged(bool value)
    {
        if (!_loading)
            SaveSettings();
    }

    partial void OnIncludeBirthDatesChanged(bool value)
    {
        if (!_loading)
            SaveSettings();
    }

    [RelayCommand]
    private void SelectAllGroups()
    {
        foreach (var group in Groups)
            group.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNoGroups()
    {
        foreach (var group in Groups)
            group.IsSelected = false;
    }

    private IofExportSettings BuildSettings() => new()
    {
        Organisation = OrganisationRegion ? IofOrganisationSource.Region : IofOrganisationSource.Club,
        IncludeBirthDates = IncludeBirthDates,
        MultiDay = MultiDay,
        // Every day ticked is stored as «all», so a day added later is in the file too.
        DayIds = DayToggles.All(d => d.IsSelected) ? [] : DayToggles.Where(d => d.IsSelected).Select(d => d.Day.Id).ToList()
    };

    // Saved right away, like the chip-scan set-up; a failed save only loses the remembered choice.
    private async void SaveSettings()
    {
        if (_session.CurrentEvent is null)
            return;
        try
        {
            await _editor.SaveIofExportSettingsAsync(BuildSettings());
        }
        catch (Exception)
        {
            // Not worth a dialog: the export itself does not depend on it.
        }
    }

    /// <summary>
    /// Builds the IOF XML of the chosen kind, days and groups; null when there is nothing to export. The view runs the
    /// save dialog.
    /// </summary>
    public async Task<IofExportResult?> GenerateAsync()
    {
        var days = ExportedDays();
        if (_session.CurrentEvent is null || days.Count == 0 || !CanGenerate)
            return null;

        // The file carries splits, and a closed day makes it final («Complete»): offer to close a single open day
        // first, as the HTML splits do. It never blocks the export. Several days are not asked one by one.
        if (Kind == IofListKind.ResultList && !MultiDay)
            await _dayLock.OfferCloseBeforeSplitsAsync(days[0]);

        var competition = _session.CurrentEvent.Name;
        var several = days.Count > 1;
        var eventName = !several && DayOptions.Count > 1
            ? $"{competition} — {Localization.Get("Header.Day")} {days[0].Number}"
            : competition;
        var options = new IofExportOptions(
            Groups.Where(g => g.IsSelected).Select(g => g.Name).ToHashSet(NameKey.Comparer),
            OrganisationRegion ? IofOrganisationSource.Region : IofOrganisationSource.Club,
            IncludeBirthDates,
            eventName,
            Creator: $"OrientPyx {typeof(IofExportViewModel).Assembly.GetName().Version?.ToString(3) ?? string.Empty}".Trim(),
            CreateTime: DateTimeOffset.Now,
            DayLabel: Localization.Get("Header.Day"));
        var kind = Kind;
        var dayIds = days.Select(d => d.Id).ToList();

        var bytes = await _busy.RunAsync(async () =>
        {
            var data = await _editor.GetIofExportDataAsync(dayIds, kind);
            // The summary page's settings: the competition's own, else the app default (as that page seeds them).
            if (data.Summary is not null && data.SummarySettings is null)
                data = data with { SummarySettings = await _appSettings.GetSummaryProtocolSettingsAsync() };
            return kind == IofListKind.ResultList
                ? _writer.Write(_builder.BuildResultList(data, options))
                : _writer.Write(_builder.BuildStartList(data, options));
        });

        var nameKey = kind == IofListKind.ResultList ? "IofExport.NamePart.Results" : "IofExport.NamePart.Start";
        var fileName = several
            ? ExportFileName.Build(Localization, nameKey, competition, "xml", date: days[^1].Date)
            : ExportFileName.Build(Localization, nameKey, competition, "xml", days[0], multiDay: DayOptions.Count > 1);
        return new IofExportResult(bytes, fileName);
    }
}

/// <summary>A day of the competition ticked for a file of several days.</summary>
public sealed partial class IofDayToggle : ObservableObject
{
    public IofDayToggle(EventDay day, string label)
    {
        Day = day;
        Label = label;
    }

    public EventDay Day { get; }

    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>The IOF XML bytes and a suggested file name.</summary>
public sealed record IofExportResult(byte[] Bytes, string SuggestedFileName);
