using System.Collections.ObjectModel;
using System.ComponentModel;
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
/// bib / ПІБ), edit the start and finish times, edit each control-point punch (code + time, add/remove
/// rows), and set a manual status override. The chip number itself is shown read-only — it is reassigned
/// through the holder dropdown, never retyped.
///
/// Times are edited in <b>local time</b> as <c>hh:mm:ss</c> (an empty time clears it), reusing the
/// participant grid's lenient <see cref="StartTimeFormat"/> parser — so "9:30" / "9:99" auto-resolve and
/// an invalid shape reverts. The read's stored times keep their own UTC offset; we display and re-parse in
/// local time anchored on the read's local date, so the value round-trips without a timezone shift.
/// Punch rows re-sort by time as they are edited / added. Callers <c>await</c> <see cref="Completion"/>
/// for the confirmed edit, or null on cancel/close.
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
    // on one date). Taken from the finish (else start) read time, in local time; today's local date when
    // the read carried no time at all.
    private readonly DateTime _anchorDate;

    public FinishReadoutEditViewModel(
        ILocalizationService localization,
        FinishReadoutEditData data,
        string titleKey = "FinishRead.Edit.Title",
        bool showCancelAll = false)
    {
        Localization = localization;
        _id = data.Id;
        _titleKey = titleKey;
        _showCancelAll = showCancelAll;

        var anchor = data.FinishTime ?? data.StartTime;
        _anchorDate = (anchor?.ToLocalTime() ?? DateTimeOffset.Now).Date;

        ChipNumber = data.ChipNumber;
        _startTimeOfDay = ToLocalTimeOfDay(data.StartTime);
        _finishTimeOfDay = ToLocalTimeOfDay(data.FinishTime);

        Punches = new ObservableCollection<PunchEditViewModel>(
            data.Punches.Select(MakePunch));
        SortPunches();

        // Reassign dropdown: a leading "(не змінювати)" sentinel, then each day member. Opens on the
        // current holder when the chip is recognised, else on "keep".
        var keep = ReassignOption.Keep(localization.Get("FinishRead.Edit.KeepHolder"));
        var options = new List<ReassignOption> { keep };
        foreach (var p in data.Participants)
            options.Add(ReassignOption.ForParticipant(p));
        Participants = new ObservableCollection<ReassignOption>(options);
        _selectedParticipant = data.CurrentHolderId is { } id
            ? Participants.FirstOrDefault(o => o.ParticipantId == id) ?? keep
            : keep;

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
            OnPropertyChanged(nameof(StartLabel));
            OnPropertyChanged(nameof(FinishLabel));
            OnPropertyChanged(nameof(PunchesLabel));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(TimeHint));
            OnPropertyChanged(nameof(CancelAllLabel));
        };
    }

    public ILocalizationService Localization { get; }

    public string Title => Localization.Get(_titleKey);

    /// <summary>The read-only chip line shown above the editable fields, e.g. "Чіп №9007400".</summary>
    public string ChipLine => string.Format(Localization.Get("FinishRead.Edit.ChipLine"), ChipNumber);

    public string HolderLabel => Localization.Get("FinishRead.Edit.Holder");
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
            else
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
            else
                OnPropertyChanged();
        }
    }

    partial void OnStartTimeOfDayChanged(TimeSpan? value) => OnPropertyChanged(nameof(StartTimeText));
    partial void OnFinishTimeOfDayChanged(TimeSpan? value) => OnPropertyChanged(nameof(FinishTimeText));

    /// <summary>The control punches, each editable (code + time), reorderable by add/remove.</summary>
    public ObservableCollection<PunchEditViewModel> Punches { get; }

    /// <summary>The reassign-chip choices: a "keep" sentinel, then each day member.</summary>
    public ObservableCollection<ReassignOption> Participants { get; }

    [ObservableProperty]
    private ReassignOption _selectedParticipant;

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
    private void AddPunch() => Punches.Add(MakePunch(new ChipPunch(string.Empty, null)));

    /// <summary>Removes a punch row.</summary>
    [RelayCommand]
    private void RemovePunch(PunchEditViewModel? punch)
    {
        if (punch is not null)
            Punches.Remove(punch);
    }

    // Builds a punch row from a stored punch (its time shown in local time) and wires it to re-sort the
    // list whenever its time changes — so a freshly-entered punch slots into chronological order.
    private PunchEditViewModel MakePunch(ChipPunch punch)
    {
        var vm = new PunchEditViewModel(punch.ControlCode, ToLocalTimeOfDay(punch.Time));
        vm.PropertyChanged += OnPunchChanged;
        return vm;
    }

    private void OnPunchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PunchEditViewModel.TimeOfDay))
            SortPunches();
    }

    // Stable sort by time of day; timed punches first (ascending), untimed rows keep their tail order.
    private void SortPunches()
    {
        var ordered = Punches
            .Select((p, i) => (p, i))
            .OrderBy(t => t.p.TimeOfDay is null)
            .ThenBy(t => t.p.TimeOfDay ?? TimeSpan.Zero)
            .ThenBy(t => t.i)
            .Select(t => t.p)
            .ToList();

        for (var target = 0; target < ordered.Count; target++)
        {
            var current = Punches.IndexOf(ordered[target]);
            if (current != target)
                Punches.Move(current, target);
        }
    }

    [RelayCommand]
    private void Confirm()
    {
        var punches = Punches
            .Where(p => p.Code.Trim().Length > 0)
            .Select(p => new ChipPunch(p.Code.Trim(), Combine(p.TimeOfDay)))
            .ToList();

        _completion.TrySetResult(new FinishReadoutEdit
        {
            Id = _id,
            ChipNumber = ChipNumber,
            StartTime = Combine(StartTimeOfDay),
            FinishTime = Combine(FinishTimeOfDay),
            Punches = punches,
            ManualStatus = SelectedStatus.Status,
            ReassignToParticipantId = SelectedParticipant.ParticipantId
        });
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
/// One editable punch row in the finish-read edit modal: a control code and its local time of day. The
/// time text reuses the participant grid's lenient <see cref="StartTimeFormat"/> parsing — a valid entry
/// is normalized to <c>hh:mm:ss</c> in place, an unparseable shape reverts to the last good text, and
/// <see cref="TimeOfDay"/> exposes the parsed value (null when blank) for sorting and saving.
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
/// One choice in the reassign-chip dropdown: a leading "keep current holder" sentinel
/// (<see cref="ParticipantId"/> null) or a specific day member.
/// </summary>
public sealed class ReassignOption
{
    private ReassignOption(Guid? participantId, string label)
    {
        ParticipantId = participantId;
        Label = label;
    }

    /// <summary>The participant to reassign to, or null for the "keep" sentinel.</summary>
    public Guid? ParticipantId { get; }

    /// <summary>The text shown in the dropdown.</summary>
    public string Label { get; }

    public static ReassignOption Keep(string label) => new(null, label);

    public static ReassignOption ForParticipant(FinishReadoutParticipantOption p)
    {
        var bib = string.IsNullOrWhiteSpace(p.Number) ? string.Empty : $"#{p.Number}  ";
        var group = string.IsNullOrWhiteSpace(p.GroupName) ? string.Empty : $"  ({p.GroupName})";
        return new ReassignOption(p.ParticipantId, $"{bib}{p.FullName}{group}");
    }
}

