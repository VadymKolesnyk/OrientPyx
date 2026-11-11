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

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// «Сканування чіпів»: hand out chips to the shown participants without one by touching each chip to the
/// read-out station. The dialog watches the read-out file (the same CSV the «Чіпи» page's auto-read uses) and
/// every chip that newly appears in it goes to the <i>target</i> participant — the selected row when it has no
/// chip yet, else the first row without one — after which the selection moves on to the next chip-less row.
/// <para>
/// What kind of chip a participant needs comes from a «chip type» additional field: each value of that field
/// is mapped (the <see cref="Rules"/>) to either «rental chip with note X» — recorded on the participant
/// <b>and</b> filed in the rental base under that note (so scanning is how the base gets filled, not a separate
/// pass) — or «own chip», meaning the participant brings their own and is <i>skipped</i>: listed for reference,
/// never a target, never counted (their number is typed into the roster by hand). A base chip whose note differs
/// from the one the target needs is held back with a message and a «присвоїти все одно» button rather than assigned.
/// </para>
/// Nothing is written while the dialog is open: «Застосувати» completes with the assignments and the chips to add
/// to the base, which the page commits in one go and then opens the statement for the same rows.
/// Completes with null on cancel. The page saves <see cref="BuildSettings"/> either way, so the field choice,
/// the rules and the file path survive to the next session.
/// </summary>
public sealed partial class ChipScanViewModel : ObservableObject
{
    private readonly TaskCompletionSource<ChipScanResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly IReadOnlyList<CustomField> _fields;
    private readonly ChipScanSettings _saved;
    // Rental base: number → note, so a scanned chip already on file is recognised and its note compared.
    private readonly Dictionary<string, string> _poolNotes;
    // Chip number → the participant(s) holding it on any day (nothing here may be handed out again).
    private readonly IReadOnlyDictionary<string, string> _holders;
    private readonly string _defaultFilePath;
    private readonly IFileReadoutPoller? _poller;
    private readonly IReadoutParserSelector? _parsers;

    // Read-out rows already in the watched file, keyed by chip + read mark + occurrence (see FinishRead's
    // repeat-read logic): the first tick only primes this set, so chips read before the dialog opened are
    // history, not scans. Touched on the poller's thread only.
    private readonly HashSet<string> _seenInFile = new(StringComparer.Ordinal);
    private bool _filePrimed;
    private bool _suppressRefresh;

    public ChipScanViewModel(
        ILocalizationService localization,
        IReadOnlyList<ChipScanCandidate> candidates,
        IReadOnlyList<CustomField> fields,
        IReadOnlyList<RentalChip> pool,
        IReadOnlyDictionary<string, string> holders,
        ChipScanSettings? settings,
        string defaultFilePath,
        ITableLayoutStore layoutStore,
        IFileReadoutPoller? poller = null,
        IReadoutParserSelector? parsers = null,
        bool sportIdentCards = true)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        Localization = localization;
        LayoutStore = layoutStore;
        ShowCardColumn = sportIdentCards;
        _fields = fields;
        _saved = settings ?? new ChipScanSettings();
        _holders = holders;
        _defaultFilePath = defaultFilePath;
        _poller = poller;
        _parsers = parsers;

        _poolNotes = new Dictionary<string, string>(TextKey.Comparer);
        foreach (var chip in pool)
        {
            var number = (chip.Number ?? string.Empty).Trim();
            if (number.Length > 0)
                _poolNotes[number] = (chip.Note ?? string.Empty).Trim();
        }

        Rows = new ObservableCollection<ChipScanRowViewModel>(candidates.Select(c => new ChipScanRowViewModel(c, Rules, sportIdentCards)));
        foreach (var row in Rows)
            row.RuleChanged += OnRowRuleChanged;

        // The field dropdown: «no field» first, then every text/choice field of the competition.
        FieldOptions = new ObservableCollection<ChipScanFieldOption>
        {
            new(string.Empty, localization.Get("Participants.ChipScan.Field.None"), null)
        };
        foreach (var f in fields.Where(f => f.Kind is CustomFieldKind.Choice or CustomFieldKind.Text))
            FieldOptions.Add(new ChipScanFieldOption(f.Key, f.Label, f));

        _selectedField = FieldOptions.FirstOrDefault(o => o.Key.Length > 0 && o.Key == _saved.FieldKey)
            ?? FieldOptions.FirstOrDefault(o => o.Field is not null && LooksLikeChipField(o.Field.Label))
            ?? FieldOptions[0];

        _filePath = string.IsNullOrWhiteSpace(_saved.ReadoutFilePath) ? defaultFilePath : _saved.ReadoutFilePath;
        // First time in this competition the set-up is open so the field and rules get looked at.
        _isSetupExpanded = settings is null;

        _suppressRefresh = true;
        RebuildRules();
        _suppressRefresh = false;
        Refresh();
        SelectedRow = Rows.FirstOrDefault(r => !r.HasChip);

        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            Refresh();
        };

        StartWatch();
    }

    public ILocalizationService Localization { get; }

    public ITableLayoutStore LayoutStore { get; }

    /// <summary>
    /// True when the competition is punched with SportIdent (app-level «Тип відмітки»): the chip number then
    /// names the card model (<see cref="SiCard"/>), so the table shows a «Тип картки» column and a card too
    /// small for the target's course is held back. Off for any other punching system.
    /// </summary>
    public bool ShowCardColumn { get; }

    public string Title => Localization.Get("Participants.ChipScan.Title");

    /// <summary>The shown participants without a chip, in table order.</summary>
    public ObservableCollection<ChipScanRowViewModel> Rows { get; }

    /// <summary>The row the next scanned chip goes to (bound to the table's selection).</summary>
    [ObservableProperty]
    private ChipScanRowViewModel? _selectedRow;

    partial void OnSelectedRowChanged(ChipScanRowViewModel? value) => RefreshNextPrompt();

    /// <summary>The big prompt above the table: «Зараз прикладіть: прокатний «AIR» — для Іваненко (№12)», or
    /// «усі отримали чіпи» when nobody is left.</summary>
    [ObservableProperty]
    private string _nextPrompt = string.Empty;

    /// <summary>True while someone is still waiting for a chip (the prompt names them).</summary>
    [ObservableProperty]
    private bool _hasNext;

    private void RefreshNextPrompt()
    {
        if (Target is { } target)
        {
            HasNext = true;
            NextPrompt = string.Format(CultureInfo.CurrentCulture, Localization.Get("Participants.ChipScan.Next"),
                RequirementLabel(target.Requirement), target.FullName, target.Number);
        }
        else
        {
            HasNext = false;
            NextPrompt = Localization.Get("Participants.ChipScan.NextDone");
        }
    }

    /// <summary>«(no field)» + the competition's text/choice fields.</summary>
    public ObservableCollection<ChipScanFieldOption> FieldOptions { get; }

    [ObservableProperty]
    private ChipScanFieldOption _selectedField;

    partial void OnSelectedFieldChanged(ChipScanFieldOption value)
    {
        RebuildRules();
        Refresh();
    }

    /// <summary>One rule per value of the chosen field (the blank value first). Shared with every row as its
    /// «Потрібен чіп» dropdown, so a type can be changed on the spot.</summary>
    public ObservableCollection<ChipScanRuleViewModel> Rules { get; } = [];

    /// <summary>Per type: how many of the rows need it and how many of those still have no chip.</summary>
    public ObservableCollection<ChipScanCounter> Counters { get; } = [];

    /// <summary>«Без чіпа залишилось: N з M» / «Відскановано: K».</summary>
    [ObservableProperty]
    private string _remainingText = string.Empty;

    /// <summary>True once at least one row has a chip — enables «Застосувати».</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _hasAssignments;

    /// <summary>The watched read-out file; a change restarts the watch.</summary>
    [ObservableProperty]
    private string _filePath;

    partial void OnFilePathChanged(string value) => StartWatch();

    [ObservableProperty]
    private bool _isWatching;

    /// <summary>
    /// Why the watched file cannot be read (it has content the current parser rejects — typically a
    /// SPORTident file with no «Config+ (card readout)» header); empty while it reads fine or is still empty.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFileError))]
    private string _fileError = string.Empty;

    public bool HasFileError => FileError.Length > 0;

    /// <summary>A chip number typed by hand (no station): Enter / the button scans it.</summary>
    [ObservableProperty]
    private string _manualNumber = string.Empty;

    /// <summary>Whether the set-up card (field + rules) is open; open by default until the set-up was saved once.</summary>
    [ObservableProperty]
    private bool _isSetupExpanded;

    /// <summary>The outcome of the last scan (assigned / refused), shown under the table.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    [NotifyPropertyChangedFor(nameof(ShowWarning))]
    [NotifyPropertyChangedFor(nameof(ShowInfo))]
    private string _message = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWarning))]
    [NotifyPropertyChangedFor(nameof(ShowInfo))]
    private bool _isMessageWarning;

    public bool HasMessage => Message.Length > 0;

    /// <summary>A scanned chip held back because it contradicts the target's type; «присвоїти все одно» takes it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending))]
    [NotifyPropertyChangedFor(nameof(ShowWarning))]
    [NotifyPropertyChangedFor(nameof(ShowInfo))]
    [NotifyCanExecuteChangedFor(nameof(ForceAssignCommand))]
    private string _pendingNumber = string.Empty;

    public bool HasPending => PendingNumber.Length > 0;

    /// <summary>The refused-scan banner: a message, no pending chip, and the message is a refusal.</summary>
    public bool ShowWarning => HasMessage && !HasPending && IsMessageWarning;

    /// <summary>The assigned banner: a message, no pending chip, and the message is a success.</summary>
    public bool ShowInfo => HasMessage && !HasPending && !IsMessageWarning;

    public Task<ChipScanResult?> Completion => _completion.Task;

    // ── Rules

    // One rule per value of the chosen field: the blank value, then the choice options (or, for a text field,
    // the distinct values the shown participants have). A saved rule for the same value is kept; otherwise a
    // value that reads as «own» defaults to an own chip and every other one to a rental note equal to the value.
    private void RebuildRules()
    {
        foreach (var rule in Rules)
            rule.Changed -= OnRuleChanged;
        Rules.Clear();

        var values = new List<string> { string.Empty };
        var field = SelectedField.Field;
        if (field is not null)
        {
            var seen = new HashSet<string>(TextKey.Comparer) { string.Empty };
            if (field.Kind == CustomFieldKind.Choice)
            {
                foreach (var option in field.Options)
                    if (seen.Add(option.Value))
                        values.Add(option.Value);
            }
            foreach (var row in Rows)
            {
                var v = ValueOf(row.Candidate, field);
                if (seen.Add(v))
                    values.Add(v);
            }
        }

        foreach (var value in values)
        {
            var saved = _saved.FieldKey == SelectedField.Key
                ? _saved.Rules.FirstOrDefault(r => TextKey.Same(r.Value ?? string.Empty, value))
                : null;
            var isOwn = saved?.IsOwn ?? LooksLikeOwn(value);
            var note = saved?.Note ?? (isOwn ? string.Empty : value);
            var label = value.Length == 0 ? Localization.Get("Participants.ChipScan.Rule.Blank") : value;
            var rule = new ChipScanRuleViewModel(value, label, isOwn, note);
            rule.Changed += OnRuleChanged;
            Rules.Add(rule);
        }

        // Every row starts on the rule of its own field value (the blank rule when no field is chosen).
        foreach (var row in Rows)
        {
            var value = field is null ? string.Empty : ValueOf(row.Candidate, field);
            row.OriginalValue = value;
            row.SetRuleSilently(Rules.FirstOrDefault(r => TextKey.Same(r.Value, value)) ?? Rules[0]);
        }
    }

    // A row's type was changed in its dropdown: a chip already scanned for a now-skipped row is taken back.
    private void OnRowRuleChanged(object? sender, EventArgs e)
    {
        if (sender is ChipScanRowViewModel { HasChip: true, SelectedRule.IsOwn: true } row)
            ClearRow(row);
        Refresh();
    }

    private void OnRuleChanged(object? sender, EventArgs e) => Refresh();

    // The participant's value of the field, normalised the way the field stores it — so a choice value typed in
    // another case («ВЛАСНИЙ») reads as its option («власний») and meets the option's rule.
    private static string ValueOf(ChipScanCandidate candidate, CustomField field)
    {
        var raw = candidate.FieldValues.TryGetValue(field.Key, out var v) ? v : string.Empty;
        return field.Normalize(raw);
    }

    private static readonly string[] OwnWords = ["власний", "свій", "own", "особистий", "личный", "свой"];

    private static bool LooksLikeOwn(string value) =>
        OwnWords.Any(w => TextKey.Same(w, value));

    private static bool LooksLikeChipField(string label) =>
        label.Contains("чіп", StringComparison.OrdinalIgnoreCase)
        || label.Contains("chip", StringComparison.OrdinalIgnoreCase)
        || label.Contains("чип", StringComparison.OrdinalIgnoreCase);

    /// <summary>What a rule asks for: own (= skipped), or a rental chip filed under its note.</summary>
    private static ChipRequirement RequirementOf(ChipScanRuleViewModel rule) =>
        new(rule.IsOwn, rule.IsOwn ? string.Empty : rule.Note.Trim());

    private string RequirementLabel(ChipRequirement req)
    {
        if (req.IsOwn)
            return Localization.Get("Participants.ChipScan.Need.Own");
        return req.Note.Length == 0
            ? Localization.Get("Participants.ChipScan.Need.Rental")
            : string.Format(CultureInfo.CurrentCulture, Localization.Get("Participants.ChipScan.Need.RentalNote"), req.Note);
    }

    // Recomputes every row's requirement + label, the per-type counters and the apply gate. Called after any
    // rule / field change and after every (un)assignment.
    private void Refresh()
    {
        if (_suppressRefresh)
            return;

        foreach (var rule in Rules)
            rule.Caption = RequirementLabel(RequirementOf(rule));

        foreach (var row in Rows)
        {
            row.Requirement = RequirementOf(row.SelectedRule ?? Rules[0]);
            row.NeedLabel = RequirementLabel(row.Requirement);
            if (row.HasChip)
                row.Status = StatusLabel(row);
        }

        // Counters per rental kind; the own-chip rows are skipped and shown as one «пропущено» line.
        Counters.Clear();
        foreach (var group in Rows.Where(r => !r.IsSkipped).GroupBy(r => r.Requirement))
        {
            var needed = group.Count();
            var remaining = group.Count(r => !r.HasChip);
            Counters.Add(new ChipScanCounter(RequirementLabel(group.Key), needed, remaining,
                string.Format(CultureInfo.CurrentCulture, Localization.Get("Participants.ChipScan.Counter"),
                    RequirementLabel(group.Key), needed, remaining)));
        }
        var skipped = Rows.Count(r => r.IsSkipped);
        if (skipped > 0)
            Counters.Add(new ChipScanCounter(Localization.Get("Participants.ChipScan.Need.Own"), skipped, 0,
                string.Format(CultureInfo.CurrentCulture, Localization.Get("Participants.ChipScan.Counter.Skipped"), skipped)));

        var scanned = Rows.Count(r => r.HasChip);
        var toServe = Rows.Count - skipped;
        RemainingText = string.Format(CultureInfo.CurrentCulture, Localization.Get("Participants.ChipScan.Remaining"),
            toServe - scanned, toServe, scanned);
        HasAssignments = scanned > 0;
        RefreshNextPrompt();
    }

    private string StatusLabel(ChipScanRowViewModel row) =>
        row.AddToBase
            ? Localization.Get("Participants.ChipScan.Status.RentalNew")
            : Localization.Get("Participants.ChipScan.Status.RentalExisting");

    // ── Scanning

    /// <summary>The row the next chip goes to: the selected one when it is waiting, else the first waiting row.
    /// A row is waiting when it has no chip yet and is not an own-chip (skipped) one.</summary>
    public ChipScanRowViewModel? Target =>
        SelectedRow is { IsWaiting: true } selected ? selected : Rows.FirstOrDefault(r => r.IsWaiting);

    /// <summary>
    /// Takes one scanned (or typed) chip number. Refuses a chip someone already holds, one already scanned here,
    /// and — unless <paramref name="force"/> — a rental-base chip whose note is not the one the target needs
    /// (held as pending instead). UI thread.
    /// </summary>
    public void HandleScan(string? rawNumber, bool force = false)
    {
        var number = (rawNumber ?? string.Empty).Trim();
        if (number.Length == 0)
            return;

        var already = Rows.FirstOrDefault(r => r.HasChip && TextKey.Same(r.Chip, number));
        if (already is not null)
        {
            Warn("Participants.ChipScan.Msg.AlreadyScanned", number, already.FullName);
            return;
        }

        if (_holders.TryGetValue(number, out var holder))
        {
            Warn("Participants.ChipScan.Msg.AlreadyHeld", number, holder);
            return;
        }

        var target = Target;
        if (target is null)
        {
            Warn("Participants.ChipScan.Msg.NoTarget", number);
            return;
        }

        var inBase = _poolNotes.TryGetValue(number, out var baseNote);
        var req = target.Requirement;
        if (!force && inBase && !TextKey.Same(baseNote ?? string.Empty, req.Note))
        {
            Hold(number, "Participants.ChipScan.Msg.MismatchNote", number, NoteLabel(baseNote), target.FullName, NoteLabel(req.Note));
            return;
        }

        // A card model that cannot hold the target's course (fewer punches than controls) is held back the
        // same way: scan another chip («Пропустити») or take it anyway («Присвоїти все одно»).
        if (!force && ShowCardColumn && SiCard.TooSmall(number, target.Candidate.NeededPunches) is { } card)
        {
            Hold(number, "Participants.ChipScan.Msg.TooSmall",
                number, card.Model, card.Capacity, target.FullName, target.Group, target.Candidate.NeededPunches);
            return;
        }

        target.Chip = number;
        target.AddToBase = !inBase;
        target.Note = req.Note;
        target.Status = StatusLabel(target);

        PendingNumber = string.Empty;
        Message = string.Format(CultureInfo.CurrentCulture, Localization.Get("Participants.ChipScan.Msg.Assigned"),
            number, target.FullName);
        IsMessageWarning = false;

        Advance(target);
        Refresh();
    }

    // «примітка «AIR»» / «без примітки» — the base-note wording inside the mismatch messages.
    private string NoteLabel(string? note) =>
        string.IsNullOrWhiteSpace(note)
            ? Localization.Get("Participants.ChipScan.BlankNote")
            : string.Format(CultureInfo.CurrentCulture, Localization.Get("Participants.ChipScan.NoteLabel"), note.Trim());

    // Selection moves to the next waiting row after the one just served (wrapping), or clears when none is left.
    private void Advance(ChipScanRowViewModel served)
    {
        var index = Rows.IndexOf(served);
        for (var step = 1; step <= Rows.Count; step++)
        {
            var candidate = Rows[(index + step) % Rows.Count];
            if (candidate.IsWaiting)
            {
                SelectedRow = candidate;
                return;
            }
        }
        SelectedRow = null;
    }

    private void Warn(string key, params object[] args)
    {
        Message = string.Format(CultureInfo.CurrentCulture, Localization.Get(key), args);
        IsMessageWarning = true;
    }

    private void Hold(string number, string key, params object[] args)
    {
        Warn(key, args);
        PendingNumber = number;
    }

    /// <summary>Takes the held-back chip for the target regardless of its type.</summary>
    [RelayCommand(CanExecute = nameof(HasPending))]
    private void ForceAssign()
    {
        var number = PendingNumber;
        PendingNumber = string.Empty;
        HandleScan(number, force: true);
    }

    [RelayCommand]
    private void DismissPending() => PendingNumber = string.Empty;

    [RelayCommand]
    private void SubmitManual()
    {
        var number = ManualNumber;
        ManualNumber = string.Empty;
        HandleScan(number);
    }

    /// <summary>Takes a row's chip back (it will be scanned again).</summary>
    public void ClearRow(object? row)
    {
        if (row is not ChipScanRowViewModel r || !r.HasChip)
            return;

        r.Chip = string.Empty;
        r.AddToBase = false;
        r.Note = string.Empty;
        r.Status = string.Empty;
        if (SelectedRow is null)
            SelectedRow = r;
        Refresh();
    }

    // ── Read-out file watch

    private void StartWatch()
    {
        _seenInFile.Clear();
        _filePrimed = false;
        if (_poller is null || _parsers is null || string.IsNullOrWhiteSpace(FilePath))
        {
            _poller?.Stop();
            IsWatching = false;
            return;
        }

        FileError = string.Empty;
        _poller.Start(FilePath, TimeSpan.FromSeconds(1), OnPolledContentAsync, NewFileHeaderAsync);
        IsWatching = true;
    }

    private void StopWatch()
    {
        _poller?.Stop();
        IsWatching = false;
    }

    // The header a brand-new read-out file is seeded with (ReaderUI appends to an existing file without one).
    private async Task<string?> NewFileHeaderAsync() =>
        _parsers is null ? null : (await _parsers.GetCurrentAsync()).NewFileHeader;

    // Poller thread: parse the whole file, pick out the rows not seen before and scan them on the UI thread.
    private async Task OnPolledContentAsync(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || _parsers is null)
            return;

        ChipReadData data;
        try
        {
            var parser = await _parsers.GetCurrentAsync();
            data = parser.Parse(content);
        }
        catch (ReadoutFormatException)
        {
            // Not a readable read-out: say so (a silent no-op looks exactly like «nothing scanned yet»).
            // The ReaderUI case: it appended data rows to a file that had no header.
            await Dispatcher.UIThread.InvokeAsync(() =>
                FileError = Localization.Get("Participants.ChipScan.File.WrongFormat"));
            return;
        }

        if (HasFileError)
            await Dispatcher.UIThread.InvokeAsync(() => FileError = string.Empty);

        var fresh = ExtractNewScans(data);
        if (fresh.Count == 0)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var number in fresh)
                HandleScan(number);
        });
    }

    /// <summary>
    /// The chip numbers of the read-out rows not seen on an earlier tick, in file order. Rows are keyed by chip +
    /// read mark + occurrence, so a chip touched to the station again (a new read mark, or a second identical row)
    /// counts as a new scan. The first call only primes the seen set and returns nothing.
    /// </summary>
    public IReadOnlyList<string> ExtractNewScans(ChipReadData data)
    {
        const char separator = '\u001F';
        var occurrence = new Dictionary<string, int>(StringComparer.Ordinal);
        var fresh = new List<string>();
        foreach (var record in data.Records)
        {
            var number = (record.ChipNumber ?? string.Empty).Trim();
            if (number.Length == 0)
                continue;
            var baseKey = number + separator + record.ReadMark;
            var n = occurrence.TryGetValue(baseKey, out var seen) ? seen + 1 : 1;
            occurrence[baseKey] = n;
            if (_seenInFile.Add($"{baseKey}{separator}{n}") && _filePrimed)
                fresh.Add(number);
        }

        if (!_filePrimed)
        {
            _filePrimed = true;
            return [];
        }
        return fresh;
    }

    // ── Completion

    /// <summary>The set-up to save for the competition: the field, its rules and the file path (blank when default).</summary>
    public ChipScanSettings BuildSettings() => new()
    {
        FieldKey = SelectedField.Key,
        ReadoutFilePath = string.Equals(FilePath, _defaultFilePath, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : FilePath,
        Rules = Rules.Select(r => new ChipScanTypeRule { Value = r.Value, IsOwn = r.IsOwn, Note = r.Note.Trim() }).ToList()
    };

    [RelayCommand(CanExecute = nameof(HasAssignments))]
    private void Apply()
    {
        StopWatch();
        var assignments = Rows
            .Where(r => r.HasChip)
            .Select(r => new ChipScanAssignment(r.Candidate, r.Chip, r.AddToBase, r.Note))
            .ToList();
        // Types changed in the dropdown are written back to the chip-type field (only when one is chosen).
        var fieldUpdates = SelectedField.Field is { } field
            ? Rows
                .Where(r => r.SelectedRule is not null && !TextKey.Same(r.SelectedRule.Value, r.OriginalValue))
                .Select(r => new ChipScanFieldUpdate(r.Candidate, field.Key, r.SelectedRule!.Value))
                .ToList()
            : [];
        _completion.TrySetResult(new ChipScanResult(assignments, fieldUpdates));
    }

    [RelayCommand]
    private void Cancel()
    {
        StopWatch();
        _completion.TrySetResult(null);
    }
}

/// <summary>A shown participant without a chip: identity for the list plus the field values the rules read and
/// the page's row object, handed back in the result so the page can update the very cell it came from.
/// <see cref="NeededPunches"/> is the longest course the participant runs on the days the chip is for
/// (<see cref="GroupOption.ControlCount"/>; 0 = unknown), so a card too small for it can be held back.</summary>
public sealed record ChipScanCandidate(
    Guid ParticipantId,
    string Number,
    string FullName,
    string Group,
    IReadOnlyDictionary<string, string> FieldValues,
    object Row,
    int NeededPunches = 0);

/// <summary>What a participant needs: a rental chip filed under <see cref="Note"/>, or their own chip
/// (<see cref="IsOwn"/> — the scan skips them).</summary>
public readonly record struct ChipRequirement(bool IsOwn, string Note);

/// <summary>One scanned chip → one participant. <see cref="AddToBase"/>: a rental chip the base did not have.</summary>
public sealed record ChipScanAssignment(ChipScanCandidate Candidate, string Chip, bool AddToBase, string Note);

/// <summary>A participant whose chip type was changed in the dialog: the field value to store.</summary>
public sealed record ChipScanFieldUpdate(ChipScanCandidate Candidate, string Key, string Value);

/// <summary>The dialog's outcome on «Застосувати».</summary>
public sealed record ChipScanResult(IReadOnlyList<ChipScanAssignment> Assignments, IReadOnlyList<ChipScanFieldUpdate> FieldUpdates)
{
    /// <summary>The rental chips to add to the base, with the note each is filed under.</summary>
    public IReadOnlyList<(string Number, string Note)> NewRentalChips =>
        Assignments.Where(a => a.AddToBase).Select(a => (a.Chip, a.Note)).ToList();
}

/// <summary>A choice in the «chip type field» dropdown: «no field» (empty key) or a field of the competition.</summary>
public sealed record ChipScanFieldOption(string Key, string Label, CustomField? Field);

/// <summary>A counter line: how many rows need this kind of chip and how many still lack one.</summary>
public sealed record ChipScanCounter(string Label, int Needed, int Remaining, string Text);

/// <summary>One value of the chip-type field and how it is scanned (rental chip with this note, or own chip = skipped).</summary>
public sealed partial class ChipScanRuleViewModel : ObservableObject
{
    public ChipScanRuleViewModel(string value, string label, bool isOwn, string note)
    {
        Value = value;
        Label = label;
        _isOwn = isOwn;
        _note = note;
    }

    /// <summary>The field value (empty for a blank cell).</summary>
    public string Value { get; }

    public string Label { get; }

    /// <summary>Raised on any edit so the dialog recomputes requirements and counters.</summary>
    public event EventHandler? Changed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRental))]
    private bool _isOwn;

    public bool IsRental => !IsOwn;

    [ObservableProperty]
    private string _note;

    /// <summary>What the rule asks for, as shown in the rows' «Потрібен чіп» dropdown («прокатний «AIR»»,
    /// «власний — пропускається»); set by the dialog whenever the rules change.</summary>
    [ObservableProperty]
    private string _caption = string.Empty;

    partial void OnIsOwnChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnNoteChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>One list row: the participant, what they need, and the chip scanned for them (if any).</summary>
public sealed partial class ChipScanRowViewModel : ObservableObject
{
    private bool _suppressRuleChanged;

    private readonly bool _sportIdentCards;

    public ChipScanRowViewModel(ChipScanCandidate candidate, ObservableCollection<ChipScanRuleViewModel> rules, bool sportIdentCards = true)
    {
        Candidate = candidate;
        Rules = rules;
        _sportIdentCards = sportIdentCards;
    }

    public ChipScanCandidate Candidate { get; }

    /// <summary>The dialog's rules — the row's «Потрібен чіп» dropdown items.</summary>
    public ObservableCollection<ChipScanRuleViewModel> Rules { get; }

    /// <summary>The type this participant needs; changed here on the spot, written to the field on apply.</summary>
    [ObservableProperty]
    private ChipScanRuleViewModel? _selectedRule;

    /// <summary>The field value the row opened with (normalised), to tell a changed type from an untouched one.</summary>
    public string OriginalValue { get; set; } = string.Empty;

    /// <summary>Raised when the user picks another type in the dropdown (not when the dialog re-points the row).</summary>
    public event EventHandler? RuleChanged;

    partial void OnSelectedRuleChanged(ChipScanRuleViewModel? value)
    {
        if (!_suppressRuleChanged && value is not null)
            RuleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Points the row at a rule without raising <see cref="RuleChanged"/> (used on rebuilds).</summary>
    public void SetRuleSilently(ChipScanRuleViewModel rule)
    {
        _suppressRuleChanged = true;
        try
        {
            SelectedRule = rule;
        }
        finally
        {
            _suppressRuleChanged = false;
        }
    }

    public string Number => Candidate.Number;

    public string FullName => Candidate.FullName;

    public string Group => Candidate.Group;

    /// <summary>The longest course this participant may run (the most punches the chip must hold); blank when the
    /// group has no course yet.</summary>
    public string ControlCountText => Candidate.NeededPunches > 0 ? Candidate.NeededPunches.ToString() : string.Empty;

    /// <summary>What the rules say this participant needs (recomputed by the dialog).</summary>
    public ChipRequirement Requirement { get; set; }

    /// <summary>True for an own-chip participant: listed for reference, never served.</summary>
    public bool IsSkipped => Requirement.IsOwn;

    /// <summary>True while this row still waits for a scan (no chip yet, not skipped).</summary>
    public bool IsWaiting => !HasChip && !IsSkipped;

    [ObservableProperty]
    private string _needLabel = string.Empty;

    /// <summary>The chip scanned for this row; empty until one is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChip))]
    [NotifyPropertyChangedFor(nameof(CardText))]
    private string _chip = string.Empty;

    public bool HasChip => Chip.Length > 0;

    /// <summary>The scanned chip's SportIdent model and capacity («SI-Card 9 · 50»); empty until a known card is
    /// scanned, and always empty when the competition is not punched with SportIdent.</summary>
    public string CardText => _sportIdentCards && SiCard.Identify(Chip) is { } card ? $"{card.Model} · {card.CapacityText}" : string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Assignment details, meaningful while <see cref="HasChip"/>.</summary>
    public bool AddToBase { get; set; }

    public string Note { get; set; } = string.Empty;
}
