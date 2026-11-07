using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// Modal for editing one logged finish read-out: reassign the chip to a different participant (search by
/// bib / ПІБ) <b>or create a brand-new one on the spot</b>, edit the start and finish times, edit each
/// control-point punch (code + time, add/remove rows), and set a manual status override. The chip number
/// itself is shown read-only — it is reassigned through the holder dropdown, never retyped.
///
/// The holder dropdown carries a leading «+ Створити нового учасника» option; picking it swaps the block
/// for an identity form (ПІБ + group required, number / birth date / region / club / ДЮСШ / rank /
/// coach optional — the fields the protocols print). A bib number already held by another competitor is
/// rejected inline (numbers are unique per competition). Saving then creates the competitor, adds them
/// to the day in the chosen group, and hands them the chip.
///
/// Times are edited in <b>local time</b> as <c>hh:mm:ss</c> (an empty time clears it), reusing the
/// participant grid's lenient <see cref="StartTimeFormat"/> parser — so "9:30" / "9:99" auto-resolve and
/// an invalid shape reverts. The view commits a time only when its box loses focus, so the parser never
/// rewrites a half-typed value. The read's stored times keep their own UTC offset; we display and re-parse
/// in local time anchored on the read's local date, so the value round-trips without a timezone shift.
/// Punch rows carry a running number and stay where they are while edited (a moving row would steal the
/// focus); they are saved in chronological order. The chip's holder (current or newly picked) can also be
/// moved to another group of the day. Callers <c>await</c> <see cref="Completion"/> for the confirmed edit,
/// or null on cancel/close.
/// </summary>
public sealed partial class FinishReadoutEditViewModel : ObservableObject
{
    private readonly TaskCompletionSource<FinishReadoutEdit?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Guid _id;
    // The header title key — the default "edit read-out" heading, or a distinct one for the unknown-chip
    // assignment prompt that pops up when a read doesn't resolve to a day member.
    private readonly string _titleKey;
    // Whether to offer the "cancel for the whole batch" action. Set only when this modal is the unknown-chip
    // prompt and there may be more unknown reads queued behind it — lets the operator skip them all at once.
    private readonly bool _showCancelAll;
    // The read's local date — the day an edited time-of-day is spliced onto (read times are a time of day
    // on one date). Taken from the finish, else the start, else the first timed punch, in local time;
    // today's local date when the read carried no time at all.
    private readonly DateTime _anchorDate;
    // The competition's bib numbers already handed out, mapped to their holder's name — snapshotted when
    // the modal opened, so the new-participant form can flag a duplicate as it is typed.
    private readonly IReadOnlyDictionary<string, string> _takenNumbers;
    // The participant who held the chip when the modal opened (null for an unknown chip) — what the
    // "(не змінювати)" entry means for the group picker.
    private readonly Guid? _currentHolderId;
    // Each day member's group on the day, so picking a holder shows (and lets the operator change) theirs.
    private readonly Dictionary<Guid, Guid?> _participantGroups;
    // Each day member's start by the start protocol, shown under the read-out's own start for the holder.
    private readonly Dictionary<Guid, TimeSpan?> _participantStarts;
    // The shown holder's group when they were picked — a save sends a group only when it differs.
    private Guid? _holderGroupOriginal;

    public FinishReadoutEditViewModel(
        ILocalizationService localization,
        FinishReadoutEditData data,
        string titleKey = "FinishRead.Edit.Title",
        bool showCancelAll = false)
    {
        Localization = localization;
        _id = data.Id;
        _takenNumbers = data.TakenNumbers;
        _titleKey = titleKey;
        _showCancelAll = showCancelAll;

        // Anchor on the read's own local date, preferring the finish, then the start, then the first timed
        // punch — a read with only punch times (no finish) must not fall back to today, or every edited
        // punch would be re-dated onto the wrong day.
        var anchor = data.FinishTime ?? data.StartTime ?? data.Punches.FirstOrDefault(p => p.Time is not null)?.Time;
        _anchorDate = (anchor?.ToLocalTime() ?? DateTimeOffset.Now).Date;

        ChipNumber = data.ChipNumber;
        _startTimeOfDay = ToLocalTimeOfDay(data.StartTime);
        _finishTimeOfDay = ToLocalTimeOfDay(data.FinishTime);

        Punches = new ObservableCollection<PunchEditViewModel>(
            OrderByTime(data.Punches.Select(p => new PunchEditViewModel(p.ControlCode, ToLocalTimeOfDay(p.Time)))));
        Punches.CollectionChanged += (_, _) => RenumberPunches();
        RenumberPunches();

        // Reassign dropdown: a leading "(не змінювати)" sentinel, then the "create a new participant"
        // action, then each day member. Opens on the current holder when the chip is recognised, else on
        // "keep". The "create" entry is offered only when the day has a group to put the newcomer in.
        var keep = ReassignOption.Keep(localization.Get("FinishRead.Edit.KeepHolder"));
        var options = new List<ReassignOption> { keep };
        if (data.Groups.Count > 0)
            options.Add(ReassignOption.CreateNew(localization.Get("FinishRead.Edit.CreateNew")));
        foreach (var p in data.Participants)
            options.Add(ReassignOption.ForParticipant(p));
        Participants = new ObservableCollection<ReassignOption>(options);
        _selectedParticipant = data.CurrentHolderId is { } id
            ? Participants.FirstOrDefault(o => o.ParticipantId == id) ?? keep
            : keep;
        _chosen = _selectedParticipant;
        _currentHolderId = data.CurrentHolderId;
        _participantGroups = data.Participants
            .GroupBy(p => p.ParticipantId)
            .ToDictionary(g => g.Key, g => g.First().GroupId);
        _participantStarts = data.Participants
            .GroupBy(p => p.ParticipantId)
            .ToDictionary(g => g.Key, g => g.First().StartTime);

        // Lookups for the new-participant form. Region / club / ДЮСШ / rank each get a leading "(none)"
        // sentinel; the group list has none — a group is mandatory, so it opens on the first one.
        Groups = new ObservableCollection<FinishReadoutLookupOption>(data.Groups);
        _newGroup = Groups.FirstOrDefault();
        _chosenGroup = _newGroup;
        SyncHolderGroup();
        Regions = BuildOptional(data.Regions, localization.Get("Participants.Region.None"));
        _newRegion = Regions[0];
        Clubs = BuildOptional(data.Clubs, localization.Get("Participants.Club.None"));
        _newClub = Clubs[0];
        Dusshes = BuildOptional(data.Dusshes, localization.Get("Participants.Dussh.None"));
        _newDussh = Dusshes[0];

        var rankOptions = new List<string> { localization.Get("Participants.Rank.None") };
        rankOptions.AddRange(data.Ranks);
        Ranks = new ObservableCollection<string>(rankOptions);
        _newRank = Ranks[0];

        // Status dropdown: a leading "(автоматично)" = clear the override, then each settable status.
        Statuses = new ObservableCollection<FinishStatusOption>(FinishStatusOptions.Build(localization));
        _selectedStatus = data.HasManualStatus
            ? FinishStatusOptions.Select(Statuses, data.Status)
            : Statuses[0];

        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ChipLine));
            OnPropertyChanged(nameof(HolderLabel));
            OnPropertyChanged(nameof(HolderGroupLabel));
            OnPropertyChanged(nameof(StartLabel));
            OnPropertyChanged(nameof(ProtocolStartText));
            OnPropertyChanged(nameof(FinishLabel));
            OnPropertyChanged(nameof(PunchesLabel));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(TimeHint));
            OnPropertyChanged(nameof(CancelAllLabel));
            OnPropertyChanged(nameof(NewFullNameLabel));
            OnPropertyChanged(nameof(NewGroupLabel));
            OnPropertyChanged(nameof(NewNumberLabel));
            OnPropertyChanged(nameof(NewBirthDateLabel));
            OnPropertyChanged(nameof(NumberError));
            OnPropertyChanged(nameof(NewRegionLabel));
            OnPropertyChanged(nameof(NewClubLabel));
            OnPropertyChanged(nameof(NewDusshLabel));
            OnPropertyChanged(nameof(NewRankLabel));
            OnPropertyChanged(nameof(NewCoachLabel));
            OnPropertyChanged(nameof(NewParticipantHint));
        };
    }

    // An optional lookup dropdown: a "(none)" sentinel (null id) followed by the rows themselves.
    private static ObservableCollection<LookupChoice> BuildOptional(
        IReadOnlyList<FinishReadoutLookupOption> rows,
        string noneLabel)
    {
        var list = new List<LookupChoice> { LookupChoice.None(noneLabel) };
        foreach (var row in rows)
            list.Add(new LookupChoice(row.Id, row.Name));
        return new ObservableCollection<LookupChoice>(list);
    }

    public ILocalizationService Localization { get; }

    public string Title => Localization.Get(_titleKey);

    /// <summary>The read-only chip line shown above the editable fields, e.g. "Чіп №9007400".</summary>
    public string ChipLine => string.Format(Localization.Get("FinishRead.Edit.ChipLine"), ChipNumber);

    public string HolderLabel => Localization.Get("FinishRead.Edit.Holder");
    public string HolderGroupLabel => Localization.Get("FinishRead.Edit.Group");
    public string StartLabel => Localization.Get("FinishRead.Col.StartTime");
    public string FinishLabel => Localization.Get("FinishRead.Col.FinishTime");
    public string PunchesLabel => Localization.Get("FinishRead.Edit.Punches");
    public string StatusLabel => Localization.Get("FinishRead.Col.Status");
    public string TimeHint => Localization.Get("FinishRead.Edit.TimeHint");

    /// <summary>The chip number, shown read-only — the chip is reassigned via the holder dropdown, not retyped.</summary>
    public string ChipNumber { get; }

    /// <summary>Start time of day (local), null when unset. Backs <see cref="StartTimeText"/>.</summary>
    [ObservableProperty]
    private TimeSpan? _startTimeOfDay;

    /// <summary>Finish time of day (local), null when unset. Backs <see cref="FinishTimeText"/>.</summary>
    [ObservableProperty]
    private TimeSpan? _finishTimeOfDay;

    /// <summary>
    /// Start time as editable "hh:mm:ss" text (mirrors the participant grid): the getter formats the
    /// parsed value so a commit snaps to canonical form, blank clears it, partial entry is padded/clamped,
    /// and an unparseable value reverts.
    /// </summary>
    public string StartTimeText
    {
        get => StartTimeFormat.Format(StartTimeOfDay);
        set
        {
            if (StartTimeFormat.TryParse(value, out var parsed))
                StartTimeOfDay = parsed;
            // Always re-read: "12:30" committed over an unchanged 12:30:00 must still snap to the full form.
            OnPropertyChanged();
        }
    }

    /// <summary>Finish time as editable "hh:mm:ss" text (same rules as <see cref="StartTimeText"/>).</summary>
    public string FinishTimeText
    {
        get => StartTimeFormat.Format(FinishTimeOfDay);
        set
        {
            if (StartTimeFormat.TryParse(value, out var parsed))
                FinishTimeOfDay = parsed;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The holder's start by the start protocol, e.g. «За стартовим протоколом: 10:24:00» (or «не призначено»), shown
    /// under the read-out's start so the judge can compare the two. Follows the holder picked above.
    /// </summary>
    public string ProtocolStartText => string.Format(
        Localization.Get("FinishRead.Edit.ProtocolStart"),
        EffectiveHolderId is { } id && _participantStarts.TryGetValue(id, out var start) && start is not null
            ? StartTimeFormat.Format(start)
            : Localization.Get("FinishRead.Edit.ProtocolStartNone"));

    /// <summary>True when the chip has a holder (current or picked) whose protocol start can be shown.</summary>
    public bool ShowProtocolStart => EffectiveHolderId is not null;

    partial void OnStartTimeOfDayChanged(TimeSpan? value) => OnPropertyChanged(nameof(StartTimeText));
    partial void OnFinishTimeOfDayChanged(TimeSpan? value) => OnPropertyChanged(nameof(FinishTimeText));

    /// <summary>The control punches, each editable (code + time), reorderable by add/remove.</summary>
    public ObservableCollection<PunchEditViewModel> Punches { get; }

    /// <summary>The reassign-chip choices: a "keep" sentinel, then each day member.</summary>
    public ObservableCollection<ReassignOption> Participants { get; }

    /// <summary>
    /// The chosen holder. Nullable because <see cref="Controls.SearchableComboBox"/> swaps its
    /// <c>ItemsSource</c> for a filtered copy while the user types, which momentarily drops the current
    /// item out of the list and makes Avalonia write a null back — <see cref="Chosen"/> keeps reading the
    /// last real choice through that window.
    /// </summary>
    [ObservableProperty]
    private ReassignOption? _selectedParticipant;

    // The last non-null selection: what the modal actually acts on. A filter-induced null never becomes
    // "keep the current holder", which would silently undo the operator's pick mid-search.
    private ReassignOption _chosen;

    /// <summary>The effective holder choice — the selection, ignoring a transient search-filter null.</summary>
    private ReassignOption Chosen => SelectedParticipant ?? _chosen;

    // Picking "+ create a new participant" reveals the identity form (and re-checks whether Save is
    // allowed, since the form then has its own required fields).
    partial void OnSelectedParticipantChanged(ReassignOption? value)
    {
        // A real pick of another entry re-seeds the group picker with that runner's group; a transient
        // search-filter null (or the same entry re-selected after filtering) keeps the operator's group pick.
        if (value is not null && !ReferenceEquals(value, _chosen))
        {
            _chosen = value;
            SyncHolderGroup();
        }
        OnPropertyChanged(nameof(IsCreatingNew));
        OnPropertyChanged(nameof(ShowHolderGroup));
        OnPropertyChanged(nameof(ShowProtocolStart));
        OnPropertyChanged(nameof(ProtocolStartText));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    /// <summary>True while the "create a new participant" form is showing instead of a plain reassignment.</summary>
    public bool IsCreatingNew => Chosen.IsCreateNew;

    // The participant who ends up holding the chip: the picked runner, or — for "(не змінювати)" — the
    // current holder. Null while creating a new participant or when an unknown chip is left unassigned.
    private Guid? EffectiveHolderId => Chosen.IsCreateNew ? null : Chosen.ParticipantId ?? _currentHolderId;

    /// <summary>True when there is a holder whose group can be changed (and the day has groups to pick).</summary>
    public bool ShowHolderGroup => EffectiveHolderId is not null && Groups.Count > 0;

    /// <summary>
    /// The holder's group on the day, editable. Nullable for the searchable-combo reason above (and because
    /// a runner may have no group yet) — <see cref="ChosenHolderGroup"/> is what a save acts on.
    /// </summary>
    [ObservableProperty]
    private FinishReadoutLookupOption? _holderGroup;

    // The last real holder-group pick, so a mid-search null does not read as "no group".
    private FinishReadoutLookupOption? _chosenHolderGroup;

    private FinishReadoutLookupOption? ChosenHolderGroup => HolderGroup ?? _chosenHolderGroup;

    partial void OnHolderGroupChanged(FinishReadoutLookupOption? value)
    {
        if (value is not null)
            _chosenHolderGroup = value;
    }

    // Shows the effective holder's current group in the group picker (blank when they have none).
    private void SyncHolderGroup()
    {
        _holderGroupOriginal = EffectiveHolderId is { } id && _participantGroups.TryGetValue(id, out var groupId)
            ? groupId
            : null;
        var match = _holderGroupOriginal is { } gid ? Groups.FirstOrDefault(g => g.Id == gid) : null;
        _chosenHolderGroup = match;
        HolderGroup = match;
    }

    /// <summary>The day's groups a new participant can be put in (mandatory — no "(none)" entry).</summary>
    public ObservableCollection<FinishReadoutLookupOption> Groups { get; }

    /// <summary>The competition's regions, with a leading "(none)".</summary>
    public ObservableCollection<LookupChoice> Regions { get; }

    /// <summary>The competition's clubs, with a leading "(none)".</summary>
    public ObservableCollection<LookupChoice> Clubs { get; }

    /// <summary>The competition's sports schools (ДЮСШ), with a leading "(none)".</summary>
    public ObservableCollection<LookupChoice> Dusshes { get; }

    /// <summary>The app-level rank names, with a leading "(none)" first entry. Rank is stored as text.</summary>
    public ObservableCollection<string> Ranks { get; }

    /// <summary>New participant: full name (ПІБ). Required — Save stays disabled while it is blank.</summary>
    [ObservableProperty]
    private string _newFullName = string.Empty;

    partial void OnNewFullNameChanged(string value) => ConfirmCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// New participant: group on the current day. Required. Nullable for the searchable-combo reason
    /// above — <see cref="ChosenGroup"/> is what the form actually saves.
    /// </summary>
    [ObservableProperty]
    private FinishReadoutLookupOption? _newGroup;

    // The last non-null group pick, so a mid-search null neither clears the choice nor greys out Save.
    private FinishReadoutLookupOption? _chosenGroup;

    /// <summary>The effective group — the selection, ignoring a transient search-filter null.</summary>
    private FinishReadoutLookupOption? ChosenGroup => NewGroup ?? _chosenGroup;

    partial void OnNewGroupChanged(FinishReadoutLookupOption? value)
    {
        if (value is not null)
            _chosenGroup = value;
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// New participant: bib number (optional). Numbers are unique per competition, so one already held
    /// by somebody else is rejected — <see cref="NumberError"/> names the holder and Save stays disabled.
    /// </summary>
    [ObservableProperty]
    private string _newNumber = string.Empty;

    partial void OnNewNumberChanged(string value)
    {
        OnPropertyChanged(nameof(NumberError));
        OnPropertyChanged(nameof(HasNumberError));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The duplicate-number message ("Номер уже в «X»"), or empty when the typed number is free / blank.
    /// Checked against the competition's numbers snapshot taken when the modal opened.
    /// </summary>
    public string NumberError
    {
        get
        {
            var number = NewNumber.Trim();
            return number.Length > 0 && _takenNumbers.TryGetValue(number, out var holder)
                ? string.Format(Localization.Get("FinishRead.Edit.New.NumberTaken"), holder)
                : string.Empty;
        }
    }

    /// <summary>True while <see cref="NumberError"/> has something to show. Drives the warning line.</summary>
    public bool HasNumberError => NumberError.Length > 0;

    [ObservableProperty]
    private DateTimeOffset? _newBirthDate;

    // The optional lookups are nullable for the same reason as SelectedParticipant: the searchable combo
    // writes a null while its filtered list momentarily excludes the current item. Here a null simply
    // reads as "(none)", which is what the sentinel means anyway — no separate shadow field needed.

    /// <summary>New participant: region (optional); null / the sentinel both mean "none".</summary>
    [ObservableProperty]
    private LookupChoice? _newRegion;

    /// <summary>New participant: club (optional); null / the sentinel both mean "none".</summary>
    [ObservableProperty]
    private LookupChoice? _newClub;

    /// <summary>New participant: ДЮСШ (optional); null / the sentinel both mean "none".</summary>
    [ObservableProperty]
    private LookupChoice? _newDussh;

    /// <summary>New participant: sports rank (optional); the first entry (and null) mean "none".</summary>
    [ObservableProperty]
    private string? _newRank;

    /// <summary>New participant: coach (optional).</summary>
    [ObservableProperty]
    private string _newCoach = string.Empty;

    public string NewFullNameLabel => Localization.Get("FinishRead.Edit.New.FullName");
    public string NewGroupLabel => Localization.Get("FinishRead.Edit.New.Group");
    public string NewNumberLabel => Localization.Get("FinishRead.Edit.New.Number");
    public string NewBirthDateLabel => Localization.Get("FinishRead.Edit.New.BirthDate");
    public string NewRegionLabel => Localization.Get("FinishRead.Edit.New.Region");
    public string NewClubLabel => Localization.Get("FinishRead.Edit.New.Club");
    public string NewDusshLabel => Localization.Get("FinishRead.Edit.New.Dussh");
    public string NewRankLabel => Localization.Get("FinishRead.Edit.New.Rank");
    public string NewCoachLabel => Localization.Get("FinishRead.Edit.New.Coach");
    public string NewParticipantHint => Localization.Get("FinishRead.Edit.New.Hint");

    /// <summary>The status choices: an "auto" sentinel (clears the override), then each settable status.</summary>
    public ObservableCollection<FinishStatusOption> Statuses { get; }

    [ObservableProperty]
    private FinishStatusOption _selectedStatus;

    /// <summary>Completes with the confirmed edit on OK, or null on cancel/close.</summary>
    public Task<FinishReadoutEdit?> Completion => _completion.Task;

    /// <summary>
    /// Whether the "cancel for the whole batch" action is offered — true only for the unknown-chip prompt
    /// when more unknown reads may be queued behind this one. Drives the button's visibility in the view.
    /// </summary>
    public bool ShowCancelAll => _showCancelAll;

    /// <summary>The label for the "cancel for the whole batch" action.</summary>
    public string CancelAllLabel => Localization.Get("FinishRead.Unknown.CancelAll");

    /// <summary>
    /// Set when the operator chose "cancel for all": the caller reads this after <see cref="Completion"/>
    /// returns null to know it should stop prompting the remaining unknown reads in the current batch.
    /// </summary>
    public bool CancelledAll { get; private set; }

    /// <summary>Appends a blank punch row for the user to fill in.</summary>
    [RelayCommand]
    private void AddPunch() => Punches.Add(new PunchEditViewModel(string.Empty, null));

    [RelayCommand]
    private void RemovePunch(PunchEditViewModel? punch)
    {
        if (punch is not null)
            Punches.Remove(punch);
    }

    // The running «№» of each punch row (1-based, in the list's current order).
    private void RenumberPunches()
    {
        for (var i = 0; i < Punches.Count; i++)
            Punches[i].Number = i + 1;
    }

    // Stable sort by time of day; timed punches first (ascending), untimed rows keep their tail order.
    // Applied when the modal opens and when it saves — never while editing, where a row jumping to its new
    // place would take the focus with it.
    private static List<PunchEditViewModel> OrderByTime(IEnumerable<PunchEditViewModel> punches) =>
        punches
            .Select((p, i) => (p, i))
            .OrderBy(t => t.p.TimeOfDay is null)
            .ThenBy(t => t.p.TimeOfDay ?? TimeSpan.Zero)
            .ThenBy(t => t.i)
            .Select(t => t.p)
            .ToList();

    /// <summary>
    /// Save is blocked only while the "create a new participant" form is open and either a required field
    /// (ПІБ / group) is missing or the typed bib number is already held by somebody else — a plain edit /
    /// reassignment always saves.
    /// </summary>
    private bool CanConfirm() =>
        !IsCreatingNew ||
        (NewFullName.Trim().Length > 0 && ChosenGroup is not null && !HasNumberError);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        var punches = OrderByTime(Punches)
            .Where(p => p.Code.Trim().Length > 0)
            .Select(p => new ChipPunch(p.Code.Trim(), Combine(p.TimeOfDay)))
            .ToList();

        // A group is sent only when the operator actually changed the holder's group.
        var group = ShowHolderGroup && ChosenHolderGroup is { } picked && picked.Id != _holderGroupOriginal
            ? picked.Id
            : (Guid?)null;

        _completion.TrySetResult(new FinishReadoutEdit
        {
            Id = _id,
            ChipNumber = ChipNumber,
            StartTime = Combine(StartTimeOfDay),
            FinishTime = Combine(FinishTimeOfDay),
            Punches = punches,
            ManualStatus = SelectedStatus.Status,
            ReassignToParticipantId = Chosen.ParticipantId,
            GroupId = group,
            NewParticipant = BuildNewParticipant()
        });
    }

    // The filled-in new-participant form, or null when the operator picked an existing runner / left the
    // holder unchanged. CanConfirm has already guaranteed the required fields.
    private NewParticipantData? BuildNewParticipant()
    {
        if (!IsCreatingNew || ChosenGroup is not { } group)
            return null;

        // Rank's first entry is the "(none)" placeholder, so it (like a null) maps to a blank rank.
        var rank = NewRank is null || ReferenceEquals(NewRank, Ranks[0]) ? string.Empty : NewRank;

        return new NewParticipantData
        {
            FullName = NewFullName.Trim(),
            GroupId = group.Id,
            Number = NewNumber.Trim(),
            BirthDate = NewBirthDate,
            RegionId = NewRegion?.Id,
            ClubId = NewClub?.Id,
            DusshId = NewDussh?.Id,
            Rank = rank,
            Coach = NewCoach.Trim()
        };
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);

    // "Cancel for all": close this modal like a plain Cancel, but flag CancelledAll so the caller skips the
    // rest of the unknown reads in the current batch (they still print as-is when auto-print is on).
    [RelayCommand]
    private void CancelAll()
    {
        CancelledAll = true;
        _completion.TrySetResult(null);
    }

    // A stored time → its local time of day (null when unset). Converting to local first is what fixes the
    // timezone shift: punch times are stored as UTC ticks (offset 0), so they must be shown in local time,
    // same as the start/finish read times the log displays.
    private static TimeSpan? ToLocalTimeOfDay(DateTimeOffset? time) =>
        time is { } t ? t.ToLocalTime().TimeOfDay : null;

    // A local time of day → a stored timestamp at the local offset on the read's date (so it round-trips
    // without shifting). Null clears it.
    private DateTimeOffset? Combine(TimeSpan? localTimeOfDay)
    {
        if (localTimeOfDay is not { } tod)
            return null;
        var local = _anchorDate + tod;
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}

/// <summary>
/// One editable punch row in the finish-read edit modal: a running number, a control code and its local
/// time of day. The time text reuses the participant grid's lenient <see cref="StartTimeFormat"/> parsing
/// — the view commits it on focus loss, where a valid entry is normalized to <c>hh:mm:ss</c> in place, an
/// unparseable shape reverts to the last good text, and <see cref="TimeOfDay"/> exposes the parsed value
/// (null when blank) for sorting and saving.
/// </summary>
public sealed partial class PunchEditViewModel : ObservableObject
{
    // Guards the normalize-in-place write so it doesn't re-enter OnTimeTextChanged.
    private bool _normalizing;

    public PunchEditViewModel(string code, TimeSpan? timeOfDay)
    {
        _code = code;
        _timeOfDay = timeOfDay;
        _timeText = StartTimeFormat.Format(timeOfDay);
    }

    /// <summary>The row's 1-based position in the list («№»), kept up to date by the owning modal.</summary>
    [ObservableProperty]
    private int _number;

    [ObservableProperty]
    private string _code;

    [ObservableProperty]
    private string _timeText;

    /// <summary>The parsed local time of day, or null when the field is blank. Drives the chronological sort.</summary>
    [ObservableProperty]
    private TimeSpan? _timeOfDay;

    // On every text edit, try the lenient parse: a good value updates TimeOfDay and rewrites the text in
    // its canonical hh:mm:ss form; an invalid shape reverts the text to match the last accepted value.
    partial void OnTimeTextChanged(string value)
    {
        if (_normalizing)
            return;

        if (StartTimeFormat.TryParse(value, out var tod))
        {
            TimeOfDay = tod;
            var canonical = StartTimeFormat.Format(tod);
            if (canonical != value)
            {
                _normalizing = true;
                TimeText = canonical;
                _normalizing = false;
            }
        }
        else
        {
            _normalizing = true;
            TimeText = StartTimeFormat.Format(TimeOfDay);
            _normalizing = false;
        }
    }
}

/// <summary>
/// One choice in the reassign-chip dropdown: the leading "keep current holder" sentinel
/// (<see cref="ParticipantId"/> null), the "+ create a new participant" action
/// (<see cref="IsCreateNew"/>), or a specific day member.
/// </summary>
public sealed class ReassignOption
{
    private ReassignOption(Guid? participantId, string label, bool isCreateNew = false)
    {
        ParticipantId = participantId;
        Label = label;
        IsCreateNew = isCreateNew;
    }

    /// <summary>The participant to reassign to, or null for the "keep" / "create new" sentinels.</summary>
    public Guid? ParticipantId { get; }

    /// <summary>The text shown in the dropdown.</summary>
    public string Label { get; }

    /// <summary>True for the "+ create a new participant" entry, which reveals the identity form.</summary>
    public bool IsCreateNew { get; }

    public static ReassignOption Keep(string label) => new(null, label);

    public static ReassignOption CreateNew(string label) => new(null, label, isCreateNew: true);

    public static ReassignOption ForParticipant(FinishReadoutParticipantOption p)
    {
        var bib = string.IsNullOrWhiteSpace(p.Number) ? string.Empty : $"#{p.Number}  ";
        var group = string.IsNullOrWhiteSpace(p.GroupName) ? string.Empty : $"  ({p.GroupName})";
        return new ReassignOption(p.ParticipantId, $"{bib}{p.FullName}{group}");
    }
}


/// <summary>
/// One choice in an optional lookup dropdown of the "create a new participant" form (region / club /
/// ДЮСШ): a competition-level row, or the leading "(none)" sentinel with a null <see cref="Id"/>.
/// </summary>
public sealed class LookupChoice
{
    public LookupChoice(Guid id, string label)
    {
        Id = id;
        Label = label;
    }

    private LookupChoice(string label) => Label = label;

    /// <summary>The row's id, or null for the "(none)" sentinel.</summary>
    public Guid? Id { get; }

    /// <summary>The text shown in the dropdown.</summary>
    public string Label { get; }

    /// <summary>The "(none)" sentinel: leaves the field unset.</summary>
    public static LookupChoice None(string label) => new(label);
}
