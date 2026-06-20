using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// «Жеребкування»: prepares and runs the start draw for a day. The preparation mirrors the CourseParser
/// draw layout — the day's groups are clustered into start groups (columns), each carrying its first
/// control point and member count, and the user sets a global start, an interval and how many start groups
/// to auto-distribute into. The draw itself (random order inside each group, with same-region/club/team
/// competitors kept off consecutive slots) assigns a concrete start minute to every competitor; the result
/// table is then saved back onto the day's participants (ParticipantDay.StartTime).
///
/// Unlike CourseParser, the data comes from the competition database (the selected day's groups and their
/// real members), and the output is written into the database rather than exported to XML.
/// </summary>
public sealed partial class DrawViewModel : PageViewModelBase
{
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IStartDrawService _draw;
    private readonly IBusyService _busy;

    // Guards SelectedDay sync during LoadAsync so the setter doesn't trigger a reload mid-load.
    private bool _syncingDay;

    // The groups loaded for the selected day (source of truth for what's available to arrange).
    private IReadOnlyList<DrawGroup> _loadedGroups = [];

    public DrawViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IStartDrawService draw,
        IBusyService busy)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _draw = draw;
        _busy = busy;

        SeparationOptions =
        [
            new DrawSeparationOption(DrawSeparationField.None, localization),
            new DrawSeparationOption(DrawSeparationField.Region, localization),
            new DrawSeparationOption(DrawSeparationField.Club, localization),
            new DrawSeparationOption(DrawSeparationField.Team, localization),
        ];
        _selectedSeparation = SeparationOptions[0];

        // Singleton VM: reload the day list + groups on a competition/day change (marshal to UI).
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
    }

    public override string NavKey => "Nav.Draw";
    public override string TitleKey => "Page.Draw.Title";
    public override string TextKey => "Page.Draw.Text";

    // A clock/stopwatch glyph.
    public override string IconData =>
        "M12,3 a9,9 0 1 1 0,18 a9,9 0 0 1 0,-18 z M12,7 v5 l3,3";

    // ── Day picker (does NOT touch the session) ──────────────────────────────────────────────────────

    public ObservableCollection<DayOption> DayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _selectedDay;

    public bool ShowDaySelector => DayOptions.Count > 1;

    // ── Draw controls ────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _globalStart = "11:00:00";

    [ObservableProperty]
    private string _interval = "00:01:00";

    public ObservableCollection<DrawSeparationOption> SeparationOptions { get; }

    [ObservableProperty]
    private DrawSeparationOption _selectedSeparation;

    [ObservableProperty]
    private int _autoGroupCount = 1;

    /// <summary>
    /// When on, each group chip's height is made proportional to its member count so a start group reads like
    /// a timeline: groups in different columns that overlap in time line up vertically, making the cross-column
    /// clashes visible by eye. Off = normal content-sized chips.
    /// </summary>
    [ObservableProperty]
    private bool _proportionalHeights;

    // Pixels per competitor when proportional mode is on, plus a floor so a tiny group's chip still fits its text.
    private const double PixelsPerMember = 7.0;
    private const double MinProportionalHeight = 24.0;

    // The fixed gap above every chip (the drop-line holder) is part of each chip's vertical footprint, so it
    // must be subtracted from the proportional height — otherwise columns with more chips drift down by 6px
    // per chip and lanes stop lining up. Keep in sync with the gap Border's Height in DrawView.axaml.
    private const double ChipGap = 6.0;

    // A chip shorter than this can't fit two text rows (name + detail), so its detail moves onto the name line.
    private const double TwoRowMinHeight = 40.0;

    // ── Arrangement + output ─────────────────────────────────────────────────────────────────────────

    /// <summary>The start groups (columns), each an ordered set of groups.</summary>
    public ObservableCollection<DrawStartGroupViewModel> StartGroups { get; } = [];

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

    // Loads the selected day's groups + members and seeds the initial start groups. The day opens already
    // distributed into 5 balanced start groups (the same split the «Авто» button applies), clamped down when
    // the day has fewer groups. Clears any prior draw.
    private async Task ReloadGroupsAsync()
    {
        if (SelectedDay?.Day is not { } day)
        {
            _loadedGroups = [];
            StartGroups.Clear();
            ClearResults();
            return;
        }

        var data = await _busy.RunAsync(() => _editor.GetDrawPrepDataAsync(day.Id));
        _loadedGroups = data.Groups;

        // Default the auto-distribute count to 5 start groups (clamped down if the day has fewer groups),
        // then build that many balanced lanes.
        AutoGroupCount = Math.Clamp(5, 1, Math.Max(1, _loadedGroups.Count));
        DistributeIntoStartGroups(AutoGroupCount);
        ClearResults();
        RecomputeColumnTimes();
    }

    partial void OnGlobalStartChanged(string value) => RecomputeColumnTimes();

    partial void OnIntervalChanged(string value) => RecomputeColumnTimes();

    partial void OnProportionalHeightsChanged(bool value) => ApplyChipHeights();

    // Sizes every chip according to the current mode: proportional to the member count (timeline view) or
    // content-sized (NaN) in normal mode. Called whenever the mode toggles or the arrangement changes.
    //
    // The chip's footprint is (chip height + the 6px gap above it); we want that footprint proportional to the
    // member count so lanes line up. So the chip itself gets (members × px − gap), floored at a minimum, and
    // when that floor kicks in for a short group the detail row is folded onto the name line (Compact).
    private void ApplyChipHeights()
    {
        foreach (var column in StartGroups)
        {
            foreach (var item in column.Groups)
            {
                if (!ProportionalHeights)
                {
                    item.ProportionalHeight = double.NaN;
                    item.Compact = false;
                    continue;
                }

                var footprint = Math.Max(MinProportionalHeight, item.MemberCount * PixelsPerMember);
                item.ProportionalHeight = footprint - ChipGap;
                item.Compact = footprint < TwoRowMinHeight;
            }
        }
    }

    // Recomputes the per-group start time shown in each column, using the same sequential layout the draw
    // applies (each group starts after the previous group's members in its column).
    private void RecomputeColumnTimes()
    {
        if (!StartTimeFormat.TryParse(GlobalStart, out var startOpt) || startOpt is not { } start ||
            !StartTimeFormat.TryParse(Interval, out var intervalOpt) || intervalOpt is not { } interval)
        {
            foreach (var column in StartGroups)
            {
                foreach (var item in column.Groups)
                {
                    item.StartLabel = string.Empty;
                    item.FirstControlClash = false;
                    item.CourseClash = false;
                }
                column.FooterText = string.Empty;
            }
            ApplyChipHeights();
            return;
        }

        // Slot-based layout: each group's members occupy a half-open run of integer start slots [start, end)
        // within its column, the next group following on. Tracking slots (not formatted times) lets us detect
        // collisions across columns regardless of the interval.
        foreach (var column in StartGroups)
        {
            var offset = 0;
            foreach (var item in column.Groups)
            {
                var time = start + TimeSpan.FromTicks(interval.Ticks * offset);
                item.StartLabel = StartTimeFormat.Format(time);
                item.FirstSlot = offset;
                offset += item.MemberCount;
            }
            column.FooterText = offset > 0
                ? StartTimeFormat.Format(start + TimeSpan.FromTicks(interval.Ticks * (offset - 1)))
                : string.Empty;
            column.RaiseTotalsChanged();
        }

        RecomputeClashes();
        ApplyChipHeights();
    }

    // Flags groups whose start-slot run overlaps a group in a DIFFERENT column. A shared first control on an
    // overlapping minute means two runners would punch the same opening control at once (FirstControlClash →
    // КП N shown red); identical full courses is the stronger case (CourseClash → whole chip red). A group
    // with no members occupies no slots and never clashes.
    private void RecomputeClashes()
    {
        var all = StartGroups
            .SelectMany((column, ci) => column.Groups.Select(g => (Column: ci, Item: g)))
            .ToList();

        foreach (var (_, item) in all)
        {
            item.FirstControlClash = false;
            item.CourseClash = false;
        }

        for (var a = 0; a < all.Count; a++)
        {
            var (ca, ia) = all[a];
            if (ia.MemberCount == 0)
                continue;
            for (var b = a + 1; b < all.Count; b++)
            {
                var (cb, ib) = all[b];
                if (cb == ca || ib.MemberCount == 0)
                    continue; // same column never collides — its groups are sequential
                if (!SlotsOverlap(ia, ib))
                    continue;

                if (SameCourse(ia, ib))
                {
                    ia.CourseClash = true;
                    ib.CourseClash = true;
                }
                else if (!string.IsNullOrEmpty(ia.FirstControl) &&
                         string.Equals(ia.FirstControl, ib.FirstControl, StringComparison.OrdinalIgnoreCase))
                {
                    ia.FirstControlClash = true;
                    ib.FirstControlClash = true;
                }
            }
        }
    }

    private static bool SlotsOverlap(DrawGroupItemViewModel a, DrawGroupItemViewModel b)
    {
        var aEnd = a.FirstSlot + a.MemberCount;
        var bEnd = b.FirstSlot + b.MemberCount;
        return a.FirstSlot < bEnd && b.FirstSlot < aEnd;
    }

    private static bool SameCourse(DrawGroupItemViewModel a, DrawGroupItemViewModel b)
    {
        var ca = a.CourseControls;
        var cb = b.CourseControls;
        if (ca.Count == 0 || ca.Count != cb.Count)
            return false;
        for (var i = 0; i < ca.Count; i++)
            if (!string.Equals(ca[i], cb[i], StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    // ── Commands ─────────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void IncrementAutoGroupCount() => AutoGroupCount = Math.Min(50, AutoGroupCount + 1);

    [RelayCommand]
    private void DecrementAutoGroupCount() => AutoGroupCount = Math.Max(1, AutoGroupCount - 1);

    /// <summary>Adds an empty start group (column) the user can move groups into.</summary>
    [RelayCommand]
    private void AddStartGroup()
    {
        StartGroups.Add(new DrawStartGroupViewModel(StartGroups.Count, Localization));
    }

    /// <summary>Removes a start group; its groups fall back into the first column so none are lost.</summary>
    [RelayCommand]
    private void RemoveStartGroup(DrawStartGroupViewModel? column)
    {
        if (column is null || !StartGroups.Contains(column))
            return;
        if (StartGroups.Count == 1)
        {
            column.Groups.Clear();
        }
        else
        {
            StartGroups.Remove(column);
            if (column.Groups.Count > 0)
            {
                var target = StartGroups[0];
                foreach (var g in column.Groups)
                    target.Groups.Add(g);
            }
        }
        Reindex();
        RecomputeColumnTimes();
    }

    /// <summary>
    /// Drag-and-drop move: relocates <paramref name="item"/> into <paramref name="targetColumn"/> at
    /// <paramref name="targetIndex"/> (clamped). Mirrors the reference draw modal's moveLabelTo — when the
    /// drop stays in the same column and the item was before the target, the index is shifted back by one so
    /// the visual drop position is honoured. A no-op when the item isn't found or nothing actually changes.
    /// Called from the View's drop handler; the arrow buttons remain as a keyboard/click fallback.
    /// </summary>
    public void MoveGroupTo(DrawGroupItemViewModel? item, DrawStartGroupViewModel? targetColumn, int targetIndex)
    {
        if (item is null || targetColumn is null || !StartGroups.Contains(targetColumn))
            return;

        var fromIdx = IndexOfColumnContaining(item);
        if (fromIdx < 0)
            return;
        var sourceColumn = StartGroups[fromIdx];
        var sourceIndex = sourceColumn.Groups.IndexOf(item);

        var to = targetIndex;
        if (ReferenceEquals(sourceColumn, targetColumn) && sourceIndex < to)
            to--;
        to = Math.Clamp(to, 0, ReferenceEquals(sourceColumn, targetColumn)
            ? sourceColumn.Groups.Count - 1
            : targetColumn.Groups.Count);

        if (ReferenceEquals(sourceColumn, targetColumn) && sourceIndex == to)
            return; // dropped onto itself — nothing to do

        sourceColumn.Groups.Remove(item);
        targetColumn.Groups.Insert(to, item);
        RecomputeColumnTimes();
    }

    // ── Drag visuals ───────────────────────────────────────────────────────────────────────────────────
    // Driven by the View's drag handlers; pure presentation state (transparency of the dragged chip, the
    // hovered-column highlight, and the single insertion line between chips).

    /// <summary>Marks the chip being dragged so the View renders it semi-transparent.</summary>
    public void BeginDrag(DrawGroupItemViewModel? item)
    {
        if (item is not null)
            item.IsDragging = true;
    }

    /// <summary>Clears the dragged-chip transparency and any drop indicators (drag finished/cancelled).</summary>
    public void EndDrag()
    {
        foreach (var column in StartGroups)
        {
            column.IsDropTarget = false;
            column.ShowDropLineAtEnd = false;
            foreach (var item in column.Groups)
            {
                item.IsDragging = false;
                item.ShowDropLineBefore = false;
            }
        }
    }

    /// <summary>
    /// Shows the drop indicator for a hover over <paramref name="column"/> at <paramref name="index"/>: the
    /// column is highlighted and a single insertion line is drawn before the chip at that index (or below the
    /// last chip when the index is at the end). Clears every other column's indicators first so only one is
    /// ever shown.
    /// </summary>
    public void SetDropIndicator(DrawStartGroupViewModel? column, int index)
    {
        foreach (var c in StartGroups)
        {
            var isTarget = ReferenceEquals(c, column);
            c.IsDropTarget = isTarget;
            c.ShowDropLineAtEnd = isTarget && index >= c.Groups.Count;
            for (var i = 0; i < c.Groups.Count; i++)
                c.Groups[i].ShowDropLineBefore = isTarget && i == index;
        }
    }

    /// <summary>Redistributes the day's groups into <see cref="AutoGroupCount"/> balanced start groups.</summary>
    [RelayCommand]
    private void AutoGroup()
    {
        DistributeIntoStartGroups(Math.Max(1, AutoGroupCount));
        ClearResults();
        RecomputeColumnTimes();
    }

    /// <summary>
    /// Rebuilds <see cref="StartGroups"/> by clustering the loaded groups into <paramref name="target"/>
    /// balanced start groups (lanes), each with roughly the same number of competitors. Groups that share a
    /// first control point stay together (they can't start on different lanes at the same minute), packed
    /// greedily (largest first) into the lightest lane — the standard balanced split the CourseParser modal
    /// does. Used both by the «Авто» button and on day load.
    /// </summary>
    private void DistributeIntoStartGroups(int target)
    {
        // Cluster the loaded groups into atoms keyed by first control point (indivisible across lanes).
        // Groups with no members are skipped entirely — they don't start anyone and only add visual noise.
        var atoms = new Dictionary<string, (List<DrawGroupItemViewModel> Items, int Sum)>();
        foreach (var group in _loadedGroups)
        {
            if (group.Members.Count == 0)
                continue;
            var key = group.FirstControl;
            if (!atoms.TryGetValue(key, out var atom))
                atom = ([], 0);
            atom.Items.Add(new DrawGroupItemViewModel(group));
            atom = (atom.Items, atom.Sum + group.Members.Count);
            atoms[key] = atom;
        }

        var atomList = atoms.Values.ToList();
        if (atomList.Count == 0)
        {
            StartGroups.Clear();
            StartGroups.Add(new DrawStartGroupViewModel(0, Localization));
            return;
        }

        var k = Math.Max(1, Math.Min(target, atomList.Count));

        // LPT: heaviest atom first, into the lightest current bucket.
        atomList.Sort((a, b) => b.Sum.CompareTo(a.Sum));
        var buckets = Enumerable.Range(0, k)
            .Select(_ => (Items: new List<DrawGroupItemViewModel>(), Sum: 0))
            .ToList();
        foreach (var atom in atomList)
        {
            var best = 0;
            for (var i = 1; i < buckets.Count; i++)
                if (buckets[i].Sum < buckets[best].Sum)
                    best = i;
            buckets[best].Items.AddRange(atom.Items);
            buckets[best] = (buckets[best].Items, buckets[best].Sum + atom.Sum);
        }

        // Heaviest lane first reads nicer.
        buckets.Sort((a, b) => b.Sum.CompareTo(a.Sum));
        StartGroups.Clear();
        foreach (var bucket in buckets)
        {
            var column = new DrawStartGroupViewModel(StartGroups.Count, Localization);
            foreach (var item in bucket.Items)
                column.Groups.Add(item);
            StartGroups.Add(column);
        }
    }

    /// <summary>Runs the draw and fills the result table (does not save — that's a separate step).</summary>
    [RelayCommand]
    private void RunDraw()
    {
        StatusMessage = string.Empty;

        if (!StartTimeFormat.TryParse(GlobalStart, out var startOpt) || startOpt is not { } start)
        {
            StatusMessage = Localization.Get("Draw.Error.Start");
            return;
        }
        if (!StartTimeFormat.TryParse(Interval, out var intervalOpt) || intervalOpt is not { } interval)
        {
            StatusMessage = Localization.Get("Draw.Error.Interval");
            return;
        }

        var separation = SelectedSeparation.Value;
        var startGroups = StartGroups
            .Select(c => (IReadOnlyList<DrawGroup>)c.Groups.Select(g => g.Group).ToList())
            .ToList();

        var assignments = _draw.Draw(startGroups, start, interval, separation);

        // Map each assignment back to the participant for display (group name, number, name, sep value).
        var memberByLink = _loadedGroups
            .SelectMany(g => g.Members.Select(m => (g.Name, Member: m)))
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

    /// <summary>Writes the drawn start times back onto the day's participants.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
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

    private int IndexOfColumnContaining(DrawGroupItemViewModel item)
    {
        for (var i = 0; i < StartGroups.Count; i++)
            if (StartGroups[i].Groups.Contains(item))
                return i;
        return -1;
    }

    private void Reindex()
    {
        for (var i = 0; i < StartGroups.Count; i++)
            StartGroups[i].Index = i;
    }

    private void ClearResults()
    {
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));
        StatusMessage = string.Empty;
    }
}
