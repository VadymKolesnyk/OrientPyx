using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// «Швидке зняття» (quick withdrawal): a small modal for quickly setting a manual finish status on several
/// competitors at once, built on the shared <c>SheetTable</c> (like the rest of the app's editable grids).
/// Each row has an editable <b>number</b> and <b>status</b>; the <b>surname</b> is looked up from the number
/// automatically and shown read-only. A trailing empty row is always present so typing a number into it
/// appends a fresh blank row.
///
/// Guard: DNS ("did not start") cannot be set on a competitor whose chip has already been read on the day —
/// they clearly started — so picking DNS on such a row is rejected (the status reverts) and a warning is
/// shown below the table. Callers <c>await</c> <see cref="Completion"/> for the entered «number → status»
/// assignments (rows without a resolved participant or status are dropped), or null on cancel.
/// </summary>
public sealed partial class QuickWithdrawalViewModel : ObservableObject
{
    private readonly TaskCompletionSource<IReadOnlyList<QuickWithdrawalAssignment>?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Day members keyed by start number (trimmed), for resolving a typed number to a competitor. Only
    // numbered members are keyed; the last writer wins (numbers are unique per competition, so it's safe).
    private readonly Dictionary<string, QuickWithdrawalMember> _byNumber;

    // Participants whose row was seeded from an existing override and then DELETED — their override must be
    // cleared on save (deleting the row = un-withdrawing them). A survivor row for the same participant
    // overrides this in Confirm, so it never wrongly clears someone who is still listed.
    private readonly HashSet<Guid> _clearedOnRemove = [];

    public QuickWithdrawalViewModel(ILocalizationService localization, QuickWithdrawalData data)
    {
        Localization = localization;

        _byNumber = new Dictionary<string, QuickWithdrawalMember>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in data.Members)
        {
            var key = (m.Number ?? string.Empty).Trim();
            if (key.Length > 0)
                _byNumber[key] = m;
        }

        Rows = [];
        // Seed rows for members that already carry a manual status override, so opening the dialog shows the
        // competitors already withdrawn (and lets the judge review / change them). Ordered as the data came.
        foreach (var m in data.Members)
            if (m.CurrentStatus is { } s && s != FinishStatus.None)
            {
                var row = NewRow();
                row.SetNumberSilently(m.Number);
                row.Resolve(m);
                row.SetStatusSilently(FinishStatusOptions.Select(row.Statuses, s));
                row.IsSeeded = true; // carried a prior override → a clear here is a deliberate "un-withdraw"
                Rows.Add(row);
            }

        // Always end with one empty row for adding a new entry.
        AppendEmptyRow();

        Localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public ILocalizationService Localization { get; }

    public string Title => Localization.Get("Participants.QuickWithdrawal.Title");

    /// <summary>The editable rows (each: number + status), always ending with one empty row.</summary>
    public ObservableCollection<QuickWithdrawalRowViewModel> Rows { get; }

    /// <summary>The row the table has selected (for keyboard delete via <c>DeleteRequested</c>).</summary>
    [ObservableProperty]
    private QuickWithdrawalRowViewModel? _selectedRow;

    /// <summary>The most recent validation warning (e.g. DNS blocked), shown below the table; blank when none.</summary>
    [ObservableProperty]
    private string _warning = string.Empty;

    /// <summary>True while <see cref="Warning"/> is non-blank, to toggle its visibility.</summary>
    public bool HasWarning => !string.IsNullOrEmpty(Warning);

    partial void OnWarningChanged(string value) => OnPropertyChanged(nameof(HasWarning));

    /// <summary>Completes with the entered assignments on confirm, or null on cancel/close.</summary>
    public Task<IReadOnlyList<QuickWithdrawalAssignment>?> Completion => _completion.Task;

    private QuickWithdrawalRowViewModel NewRow()
    {
        // A row raises its warning up to the dialog (shown below the table) and clears it when resolved.
        var row = new QuickWithdrawalRowViewModel(Localization, ResolveNumber, w => Warning = w);
        return row;
    }

    private void AppendEmptyRow()
    {
        var row = NewRow();
        row.NumberChanged += OnRowNumberChanged;
        Rows.Add(row);
    }

    // When the LAST (empty) row gets a number, it stops being the spare — append a new empty spare so there
    // is always one blank row to add into.
    private void OnRowNumberChanged(object? sender, EventArgs e)
    {
        if (sender is not QuickWithdrawalRowViewModel row)
            return;
        if (!ReferenceEquals(row, Rows[^1]))
            return;
        if (string.IsNullOrWhiteSpace(row.Number))
            return;

        row.NumberChanged -= OnRowNumberChanged;
        AppendEmptyRow();
    }

    // Resolves a typed number to a day member (or null when blank / unknown).
    private QuickWithdrawalMember? ResolveNumber(string? number)
    {
        var key = (number ?? string.Empty).Trim();
        return key.Length > 0 && _byNumber.TryGetValue(key, out var m) ? m : null;
    }

    /// <summary>Removes a row (the SheetTable delete-action / keyboard delete hands over the row object).
    /// The trailing spare row is never removed.</summary>
    public void RemoveRow(object? row)
    {
        if (row is not QuickWithdrawalRowViewModel r || ReferenceEquals(r, Rows[^1]))
            return;
        r.NumberChanged -= OnRowNumberChanged;
        if (ReferenceEquals(SelectedRow, r))
            SelectedRow = null;
        // Deleting a row that was seeded from an existing override means the override is being removed —
        // remember to clear it on save. (A still-listed row for the same participant wins in Confirm.)
        if (r.IsSeeded && r.Member is { } member)
            _clearedOnRemove.Add(member.ParticipantId);
        Rows.Remove(r);
    }

    [RelayCommand]
    private void Confirm()
    {
        var assignments = new List<QuickWithdrawalAssignment>();
        var seen = new HashSet<Guid>();
        foreach (var row in Rows)
        {
            if (row.Member is not { } member)
                continue;
            var status = row.SelectedStatus?.Status; // null = clear/auto sentinel
            // A row at the "auto" sentinel is only meaningful when it was seeded from an existing override
            // (the judge is clearing it). A freshly typed number left at "auto" is just a lookup — skip it,
            // so we never clobber a status the participant may hold from elsewhere.
            if (status is null && !row.IsSeeded)
                continue;
            // One assignment per participant (a competitor typed twice keeps the last row's status).
            if (seen.Add(member.ParticipantId))
                assignments.Add(new QuickWithdrawalAssignment(member.ParticipantId, status));
            else
            {
                var i = assignments.FindIndex(a => a.ParticipantId == member.ParticipantId);
                if (i >= 0)
                    assignments[i] = new QuickWithdrawalAssignment(member.ParticipantId, status);
            }
        }

        // A deleted seeded row clears the participant's override — unless they are still listed on a
        // surviving row (which already carries their intended status above).
        foreach (var participantId in _clearedOnRemove)
            if (seen.Add(participantId))
                assignments.Add(new QuickWithdrawalAssignment(participantId, null));

        _completion.TrySetResult(assignments);
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}

/// <summary>
/// One editable row of the quick-withdrawal modal: a typed number, the resolved (read-only) surname, and a
/// status pick. Setting the number resolves the member (auto-filling the surname / read-out flag); setting
/// the status is rejected when it is DNS while the competitor has already been read (their chip punched),
/// reporting a warning through the dialog (shown below the table).
/// </summary>
public sealed partial class QuickWithdrawalRowViewModel : ObservableObject
{
    private readonly Func<string?, QuickWithdrawalMember?> _resolve;
    // Raises a warning message up to the dialog (or clears it, with ""). Shown below the shared table.
    private readonly Action<string> _reportWarning;
    // True while the code (not the user) writes Number/SelectedStatus, so the change handlers don't fire.
    private bool _suppress;

    public QuickWithdrawalRowViewModel(
        ILocalizationService localization,
        Func<string?, QuickWithdrawalMember?> resolve,
        Action<string> reportWarning)
    {
        Localization = localization;
        _resolve = resolve;
        _reportWarning = reportWarning;
        Statuses = new ObservableCollection<FinishStatusOption>(FinishStatusOptions.Build(localization));
        _selectedStatus = Statuses[0]; // the "(автоматично)" / clear sentinel
    }

    public ILocalizationService Localization { get; }

    /// <summary>Raised when the user edits the number (used to append a new spare row).</summary>
    public event EventHandler? NumberChanged;

    /// <summary>The status dropdown options: a leading "auto" (clear) sentinel, then each settable status.</summary>
    public ObservableCollection<FinishStatusOption> Statuses { get; }

    /// <summary>The resolved day member, or null when the number is blank / unknown.</summary>
    public QuickWithdrawalMember? Member { get; private set; }

    /// <summary>True when this row was seeded from an existing override (so a clear here is a deliberate
    /// "un-withdraw", not a no-op lookup). Set by the owner after seeding.</summary>
    public bool IsSeeded { get; set; }

    [ObservableProperty]
    private string? _number;

    [ObservableProperty]
    private string _fullName = string.Empty;

    [ObservableProperty]
    private FinishStatusOption? _selectedStatus;

    partial void OnNumberChanged(string? value)
    {
        if (_suppress)
            return;

        _reportWarning(string.Empty);
        var member = _resolve(value);
        Resolve(member);
        // A number change may invalidate the current DNS pick (new competitor already read) — re-validate.
        RevalidateStatus();
        NumberChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedStatusChanged(FinishStatusOption? value)
    {
        if (_suppress)
            return;

        // Reject DNS when the competitor has already been read (they clearly started).
        if (value?.Status == FinishStatus.Dns && Member is { HasReadout: true })
        {
            _reportWarning(string.Format(Localization.Get("Participants.QuickWithdrawal.DnsBlocked"), FullName));
            // Revert to the "auto" sentinel without re-triggering this handler.
            SetStatusSilently(Statuses[0]);
            return;
        }

        _reportWarning(string.Empty);
    }

    // Re-checks the current status after the number changed: if it is now an invalid DNS, clear it + warn.
    private void RevalidateStatus()
    {
        if (SelectedStatus?.Status == FinishStatus.Dns && Member is { HasReadout: true })
        {
            _reportWarning(string.Format(Localization.Get("Participants.QuickWithdrawal.DnsBlocked"), FullName));
            SetStatusSilently(Statuses[0]);
        }
    }

    /// <summary>Applies a resolved member (or clears when null): fills the surname and read-out flag.</summary>
    public void Resolve(QuickWithdrawalMember? member)
    {
        Member = member;
        FullName = member?.FullName ?? string.Empty;
    }

    /// <summary>Sets the number without firing the change handler (used when seeding rows).</summary>
    public void SetNumberSilently(string? value)
    {
        _suppress = true;
        try { Number = value; }
        finally { _suppress = false; }
    }

    /// <summary>Sets the status without firing the change handler (used to revert / seed).</summary>
    public void SetStatusSilently(FinishStatusOption? value)
    {
        _suppress = true;
        try { SelectedStatus = value; }
        finally { _suppress = false; }
    }
}

/// <summary>One quick-withdrawal result: the participant and the status to set (null = clear the override).</summary>
public sealed record QuickWithdrawalAssignment(Guid ParticipantId, FinishStatus? Status);
