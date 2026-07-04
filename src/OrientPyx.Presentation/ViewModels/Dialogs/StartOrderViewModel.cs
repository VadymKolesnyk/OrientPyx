using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// Modal for manually re-ordering the start sequence within a group on one day. A dropdown picks the group;
/// its members are listed ordered by start time (from the start protocol) and can be dragged to a new order.
/// The set of start minutes stays fixed — the time column is bound to the <em>row position</em>, so it never
/// moves while a competitor is dragged: position <c>i</c> always shows the <c>i</c>-th smallest start time,
/// and re-ordering just changes which competitor lands in each slot. On save each competitor is assigned the
/// time of its current slot. Callers <c>await</c> <see cref="Completion"/> for the resulting assignments
/// (empty list when nothing changed), or null on cancel.
/// </summary>
public sealed partial class StartOrderViewModel : ObservableObject
{
    private readonly TaskCompletionSource<IReadOnlyList<DrawStartAssignment>?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Every group's live (re-orderable) member list, kept in memory so switching the dropdown preserves the
    // user's edits until they save. Keyed by the group id.
    private readonly Dictionary<Guid, ObservableCollection<StartOrderMemberViewModel>> _membersByGroup = new();

    // Each group's fixed, sorted set of start minutes (only members that had a time contribute). Re-handed
    // out by position on every reorder — index i of this list is the time shown in row i.
    private readonly Dictionary<Guid, List<TimeSpan>> _timesByGroup = new();

    public StartOrderViewModel(ILocalizationService localization, StartOrderData data)
    {
        Localization = localization;

        Groups = new ObservableCollection<StartOrderGroupOption>(
            data.Groups.Select(g => new StartOrderGroupOption(g.GroupId, g.Name)));

        foreach (var g in data.Groups)
        {
            var members = new ObservableCollection<StartOrderMemberViewModel>(
                g.Members.Select(m => new StartOrderMemberViewModel(m)));
            _membersByGroup[g.GroupId] = members;
            _timesByGroup[g.GroupId] = g.Members
                .Where(m => m.StartTime is not null)
                .Select(m => m.StartTime!.Value)
                .OrderBy(t => t)
                .ToList();
            ApplySlotTimes(g.GroupId);
        }

        _selectedGroup = Groups.Count > 0 ? Groups[0] : null;
        Members = SelectedGroup is not null ? _membersByGroup[SelectedGroup.GroupId] : [];

        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Hint));
        };
    }

    public ILocalizationService Localization { get; }

    public string Title => Localization.Get("Participants.StartOrder.Title");
    public string Hint => Localization.Get("Participants.StartOrder.Hint");

    /// <summary>The groups running on the day, in day-grid order.</summary>
    public ObservableCollection<StartOrderGroupOption> Groups { get; }

    /// <summary>The chosen group; switching it swaps the shown member list (edits are preserved).</summary>
    [ObservableProperty]
    private StartOrderGroupOption? _selectedGroup;

    /// <summary>The selected group's members, in the current (draggable) order.</summary>
    [ObservableProperty]
    private ObservableCollection<StartOrderMemberViewModel> _members;

    partial void OnSelectedGroupChanged(StartOrderGroupOption? value)
    {
        Members = value is not null && _membersByGroup.TryGetValue(value.GroupId, out var list)
            ? list
            : [];
    }

    /// <summary>Completes with the reassignments on save (empty when nothing changed), or null on cancel/close.</summary>
    public Task<IReadOnlyList<DrawStartAssignment>?> Completion => _completion.Task;

    // ── Drag state (mirrors the Draw page's insertion-line preview) ─────────────────────────────────────

    private StartOrderMemberViewModel? _draggingItem;

    /// <summary>Marks a row as being dragged so the view fades it.</summary>
    public void BeginDrag(StartOrderMemberViewModel item)
    {
        _draggingItem = item;
        item.IsDragging = true;
    }

    /// <summary>Clears the drag state (fade off, any insertion line off).</summary>
    public void EndDrag()
    {
        if (_draggingItem is not null)
            _draggingItem.IsDragging = false;
        _draggingItem = null;
        ClearDropIndicator();
    }

    /// <summary>
    /// Shows the insertion line at <paramref name="index"/> (the slot a drop would land in): before the item
    /// at that index, or at the end when <paramref name="index"/> equals the count. A no-op indicator is fine.
    /// </summary>
    public void SetDropIndicator(int index)
    {
        for (var i = 0; i < Members.Count; i++)
            Members[i].ShowDropLineBefore = i == index;
        ShowDropLineAtEnd = index >= Members.Count;
    }

    /// <summary>Hides every insertion line.</summary>
    public void ClearDropIndicator()
    {
        foreach (var m in Members)
            m.ShowDropLineBefore = false;
        ShowDropLineAtEnd = false;
    }

    /// <summary>True when a drop would land at the very end of the current list.</summary>
    [ObservableProperty]
    private bool _showDropLineAtEnd;

    /// <summary>Moves the dragged member to a new index within the current group's list, then re-hands out the
    /// fixed times by position so the time column stays put.</summary>
    public void MoveTo(StartOrderMemberViewModel dragged, int targetIndex)
    {
        var from = Members.IndexOf(dragged);
        if (from < 0)
            return;

        // Adjust the target when removing an earlier item shifts everything down by one.
        if (targetIndex > from)
            targetIndex--;
        targetIndex = Math.Clamp(targetIndex, 0, Members.Count - 1);
        if (targetIndex != from)
            Members.Move(from, targetIndex);

        if (SelectedGroup is not null)
            ApplySlotTimes(SelectedGroup.GroupId);
    }

    // Re-hands out the group's fixed sorted times by position: row i shows time i (or "—" when there are
    // fewer times than members). The times don't move — only which competitor sits in each slot does.
    private void ApplySlotTimes(Guid groupId)
    {
        var members = _membersByGroup[groupId];
        var times = _timesByGroup[groupId];
        for (var i = 0; i < members.Count; i++)
            members[i].SlotTime = i < times.Count ? times[i] : null;
    }

    // The current slot each competitor sits in decides its time (member i ← i-th smallest time). Only the
    // rows whose time actually changed from the original are written, so the batch stays minimal.
    [RelayCommand]
    private void Confirm()
    {
        var assignments = new List<DrawStartAssignment>();

        foreach (var (groupId, members) in _membersByGroup)
        {
            var times = _timesByGroup[groupId];
            for (var i = 0; i < members.Count && i < times.Count; i++)
            {
                var member = members[i];
                if (member.OriginalStartTime != times[i])
                    assignments.Add(new DrawStartAssignment(member.LinkId, times[i]));
            }
        }

        _completion.TrySetResult(assignments);
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}

/// <summary>One choice in the start-order group dropdown.</summary>
public sealed record StartOrderGroupOption(Guid GroupId, string Name);

/// <summary>
/// One draggable member row in the start-order editor. It carries its <em>original</em> start time (used to
/// build the group's fixed set) and a <see cref="SlotTime"/> that reflects the time of the row's current
/// position — that slot time is what's shown, so the time column never moves while the competitor is dragged.
/// </summary>
public sealed partial class StartOrderMemberViewModel : ObservableObject
{
    public StartOrderMemberViewModel(StartOrderMember member)
    {
        LinkId = member.LinkId;
        OriginalStartTime = member.StartTime;
        _slotTime = member.StartTime;
        Number = member.Number;
        FullName = member.FullName;
        RegionName = member.RegionName;
        ClubName = member.ClubName;
    }

    public Guid LinkId { get; }

    /// <summary>The competitor's start time before any reorder (defines the group's fixed set of minutes).</summary>
    public TimeSpan? OriginalStartTime { get; }

    public string Number { get; }
    public string FullName { get; }
    public string RegionName { get; }
    public string ClubName { get; }

    /// <summary>The time of the row's current position — what the (fixed) time column shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SlotTimeText))]
    private TimeSpan? _slotTime;

    /// <summary>The slot time as "hh:mm:ss", or "—" when this row has no slot.</summary>
    public string SlotTimeText => SlotTime is { } t
        ? t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
        : "—";

    /// <summary>True while this row is the one being dragged (used to fade it).</summary>
    [ObservableProperty]
    private bool _isDragging;

    /// <summary>True when the insertion line should be drawn immediately above this row.</summary>
    [ObservableProperty]
    private bool _showDropLineBefore;
}
