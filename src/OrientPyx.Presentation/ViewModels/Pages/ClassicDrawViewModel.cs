using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// «Класичне жеребкування»: the simple, table-based start draw. Unlike the lane-based draw page
/// (<see cref="DrawViewModel"/>), every group is one row drawn independently — a "take part" checkbox, its
/// own first-competitor start time and interval, and a computed first-free-minute column. The same
/// "not consecutive" separation dropdown and a draw button run the draw only for the checked groups and
/// write the resulting start times straight onto the day's participants (ParticipantDay.StartTime).
/// </summary>
public sealed partial class ClassicDrawViewModel : PageViewModelBase
{
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IStartDrawService _draw;
    private readonly IBusyService _busy;
    private readonly IDialogService _dialogs;

    // Guards SelectedDay sync during LoadAsync so the setter doesn't trigger a reload mid-load.
    private bool _syncingDay;

    // Default start/interval seeded onto each new row.
    private const string DefaultStart = "11:00:00";
    private const string DefaultInterval = "00:01:00";

    public ClassicDrawViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IStartDrawService draw,
        IBusyService busy,
        IDialogService dialogs)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _draw = draw;
        _busy = busy;
        _dialogs = dialogs;

        SeparationOptions =
        [
            new DrawSeparationOption(DrawSeparationField.None, localization),
            new DrawSeparationOption(DrawSeparationField.Region, localization),
            new DrawSeparationOption(DrawSeparationField.Club, localization),
            new DrawSeparationOption(DrawSeparationField.Team, localization),
        ];
        _selectedSeparation = SeparationOptions.First(o => o.Value == DrawSeparationField.Club);

        // Singleton VM: reload the day list + groups on a competition/day change (marshal to UI).
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
    }

    public override string NavKey => "Nav.ClassicDraw";
    public override string TitleKey => "Page.ClassicDraw.Title";
    public override string TextKey => "Page.ClassicDraw.Text";

    // A simple list/table glyph.
    public override string IconData =>
        "M4,5 h16 M4,10 h16 M4,15 h16 M4,20 h16";

    // ── Day picker (does NOT touch the session) ──────────────────────────────────────────────────────

    public ObservableCollection<DayOption> DayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _selectedDay;

    public bool ShowDaySelector => DayOptions.Count > 1;

    // ── Draw controls ────────────────────────────────────────────────────────────────────────────────

    public ObservableCollection<DrawSeparationOption> SeparationOptions { get; }

    [ObservableProperty]
    private DrawSeparationOption _selectedSeparation;

    /// <summary>The group rows for the selected day.</summary>
    public ObservableCollection<ClassicDrawGroupRowViewModel> Groups { get; } = [];

    /// <summary>The drawn result rows, ordered by start time; populated by <see cref="RunDrawCommand"/>.</summary>
    public ObservableCollection<DrawResultRowViewModel> Results { get; } = [];

    public bool HasResults => Results.Count > 0;

    /// <summary>Status line under the action buttons (e.g. "Збережено 42 старти").</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public async Task LoadAsync()
    {
        var days = await _busy.RunAsync(() => _editor.GetDaysAsync());

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

        await ReloadGroupsAsync();
    }

    partial void OnSelectedDayChanged(DayOption? value)
    {
        if (_syncingDay)
            return;
        _ = ReloadGroupsAsync();
    }

    // Loads the selected day's groups + members into the table. Groups with members are checked by default
    // (they're the ones that need a draw); empty groups stay unchecked. Clears any prior draw.
    private async Task ReloadGroupsAsync()
    {
        ClearGroups();

        if (SelectedDay?.Day is not { } day)
        {
            ClearResults();
            return;
        }

        var data = await _busy.RunAsync(() => _editor.GetDrawPrepDataAsync(day.Id));

        foreach (var group in data.Groups)
        {
            var row = new ClassicDrawGroupRowViewModel(group, DefaultStart, DefaultInterval)
            {
                Selected = group.Members.Count > 0,
            };
            row.FreeMinuteChanged += () => RecomputeFreeMinute(row);
            RecomputeFreeMinute(row);
            Groups.Add(row);
        }

        ClearResults();
    }

    // Computes a row's first free start minute: group start + members × interval. Blank when either the
    // start or interval can't be parsed, or the group has no members.
    private static void RecomputeFreeMinute(ClassicDrawGroupRowViewModel row)
    {
        if (row.MemberCount == 0 ||
            !StartTimeFormat.TryParse(row.Start, out var startOpt) || startOpt is not { } start ||
            !StartTimeFormat.TryParse(row.Interval, out var intervalOpt) || intervalOpt is not { } interval)
        {
            row.FreeMinute = string.Empty;
            return;
        }

        var free = start + TimeSpan.FromTicks(interval.Ticks * row.MemberCount);
        row.FreeMinute = StartTimeFormat.Format(free);
    }

    // ── Commands ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Checks every group row (selects all for the draw).</summary>
    [RelayCommand]
    private void SelectAll()
    {
        foreach (var row in Groups)
            row.Selected = true;
    }

    /// <summary>Unchecks every group row (selects none).</summary>
    [RelayCommand]
    private void SelectNone()
    {
        foreach (var row in Groups)
            row.Selected = false;
    }

    /// <summary>
    /// Runs the draw for the checked groups and writes the resulting start times onto the day's participants
    /// (ParticipantDay.StartTime). Asks for confirmation first, since this overwrites the start time of every
    /// participant in the selected groups.
    /// </summary>
    [RelayCommand]
    private async Task RunDrawAsync()
    {
        StatusMessage = string.Empty;

        var selected = Groups.Where(g => g.Selected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = Localization.Get("ClassicDraw.Error.NoGroups");
            return;
        }

        // Validate every selected group's start/interval before asking to confirm.
        var classicGroups = new List<ClassicDrawGroup>(selected.Count);
        foreach (var row in selected)
        {
            if (!StartTimeFormat.TryParse(row.Start, out var startOpt) || startOpt is not { } start)
            {
                StatusMessage = string.Format(Localization.Get("ClassicDraw.Error.Start"), row.Name);
                return;
            }
            if (!StartTimeFormat.TryParse(row.Interval, out var intervalOpt) || intervalOpt is not { } interval)
            {
                StatusMessage = string.Format(Localization.Get("ClassicDraw.Error.Interval"), row.Name);
                return;
            }
            classicGroups.Add(new ClassicDrawGroup(row.Group, start, interval));
        }

        var confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
            Localization,
            titleKey: "Draw.Run.ConfirmTitle",
            messageKey: "ClassicDraw.Run.ConfirmMessage",
            confirmKey: "Draw.Run.Confirm",
            cancelKey: "Common.Cancel"));
        if (!confirmed)
            return;

        BuildResults(classicGroups);

        if (Results.Count == 0)
        {
            StatusMessage = Localization.Get("Draw.Error.NoDraw");
            return;
        }

        var assignments = Results
            .Select(r => new DrawStartAssignment(r.LinkId, r.StartTime))
            .ToList();

        var saved = await _busy.RunAsync(() => _editor.SaveDrawStartTimesAsync(assignments));
        StatusMessage = string.Format(Localization.Get("Draw.Saved"), saved);
    }

    // Runs the classic draw for the supplied groups and fills the result table (ordered by start time).
    private void BuildResults(IReadOnlyList<ClassicDrawGroup> classicGroups)
    {
        var separation = SelectedSeparation.Value;
        var assignments = _draw.DrawClassic(classicGroups, separation);

        // Map each assignment back to the participant for display (group name, number, name, sep value).
        var memberByLink = classicGroups
            .SelectMany(g => g.Group.Members.Select(m => (g.Group.Name, Member: m)))
            .ToDictionary(x => x.Member.LinkId);

        ClearResults();
        var rows = new List<DrawResultRowViewModel>(assignments.Count);
        foreach (var a in assignments)
        {
            if (!memberByLink.TryGetValue(a.LinkId, out var info))
                continue;
            rows.Add(new DrawResultRowViewModel(
                a.LinkId,
                info.Name,
                info.Member.Number,
                info.Member.FullName,
                SeparationValueOf(info.Member, separation),
                a.StartTime,
                StartTimeFormat.Format(a.StartTime)));
        }
        foreach (var row in rows.OrderBy(r => r.StartTime).ThenBy(r => r.GroupName))
            Results.Add(row);
        OnPropertyChanged(nameof(HasResults));
    }

    private static string SeparationValueOf(DrawParticipant p, DrawSeparationField separation) => separation switch
    {
        DrawSeparationField.Region => p.RegionName,
        DrawSeparationField.Club => p.ClubName,
        DrawSeparationField.Team => p.Team,
        _ => string.Empty,
    };

    private void ClearGroups()
    {
        Groups.Clear();
    }

    private void ClearResults()
    {
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));
        StatusMessage = string.Empty;
    }
}
