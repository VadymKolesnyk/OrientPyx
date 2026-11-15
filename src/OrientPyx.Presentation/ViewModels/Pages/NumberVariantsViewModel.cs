using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// «Змагання → Естафета → Варіанти за номерами»: which variation (map) every number runs on the day — a relay leg number
/// «101.2» or a scatter runner's bib — as imported from OCAD or typed on «Команди», for handing out the maps and checking
/// them. Two tables over the session's day: the numbers with a variation (group, the group of the team holding the
/// number — both red when they differ, so the variation never applies —, number, variation, team, runner, controls) and,
/// below, the numbered teams' legs and runners that should have one and have none. The variation is a combo of the
/// group's variants in both, saved as soon as it is picked; F8 over mismatched rows moves them to the team's group.
/// The upper table exports to CSV / Excel through the participants' export flow.
/// </summary>
public sealed partial class NumberVariantsViewModel : PageViewModelBase
{
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;
    private readonly IParticipantExportFlow _exportFlow;
    private readonly IActivityLog _log;
    private readonly PendingSaves _pendingSaves = new();
    // Saves run one at a time, in the order they were picked (a fill-down paste sets many rows at once).
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private bool _syncingDay;

    public NumberVariantsViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IBusyService busy,
        IParticipantExportFlow exportFlow,
        IExportFileSaver fileSaver,
        ITableLayoutStore layoutStore,
        IDayLockService dayLock,
        IDataChangeBus bus,
        IWorkspaceMode mode,
        IActivityLog log)
        : base(localization)
    {
        UseDayLock(dayLock);
        _editor = editor;
        _session = session;
        _busy = busy;
        _exportFlow = exportFlow;
        _log = log;
        FileSaver = fileSaver;
        LayoutStore = layoutStore;

        // Singleton VM: reload on a competition/day change (marshal to UI).
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
        // Live data (another station's writes) — see ReloadAsync.
        ObserveData(bus, session, mode,
            DataArea.Days, DataArea.Groups, DataArea.GroupDaySettings, DataArea.Participants, DataArea.ParticipantDays,
            DataArea.Relay);
    }

    /// <summary>Writes the export to the file the user picked (handles a target locked by Excel). Used by the view.</summary>
    public IExportFileSaver FileSaver { get; }

    /// <summary>Persists the tables' column layout (per competition, app default). Bound by the view.</summary>
    public ITableLayoutStore LayoutStore { get; }

    public override string NavKey => "Nav.NumberVariants";
    public override string TitleKey => "Page.NumberVariants.Title";
    public override string TextKey => "Page.NumberVariants.Text";

    // Lucide "list-ordered".
    public override string IconData =>
        "M10 12h11 M10 18h11 M10 6h11 M4 10h2 M4 6h1v4 M6 18H4c0-1 2-2 2-3s-1-1.5-2-1";

    public ObservableCollection<DayOption> DayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _selectedDay;

    public bool ShowDaySelector => DayOptions.Count > 1;

    /// <summary>The numbers with a variation, one row per number and leg.</summary>
    public ObservableCollection<NumberVariantRowViewModel> Rows { get; } = [];

    /// <summary>The numbers that should have a variation and have none (a row stays here once given one, until reload).</summary>
    public ObservableCollection<NumberVariantRowViewModel> Unassigned { get; } = [];

    /// <summary>The lower table shows: some number of the day was found without a variation.</summary>
    [ObservableProperty]
    private bool _hasUnassigned;

    /// <summary>«Без варіанта: N» — the lower table's heading, counting the rows still without one.</summary>
    [ObservableProperty]
    private string _unassignedTitle = string.Empty;

    /// <summary>True when the day has no number at all to show (the page then explains where they come from).</summary>
    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>One line over the tables: how many numbers have a variation and what needs a hand.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private string _summary = string.Empty;

    public bool HasSummary => Summary.Length > 0;

    /// <summary>Some number is without a variation, in another group than its team, or names no variant of its group.</summary>
    [ObservableProperty]
    private bool _summaryIsWarning;

    /// <inheritdoc />
    protected override async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _pendingSaves.WaitAsync();
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        var hasDay = _session.CurrentDay is not null;
        var (days, data) = await _busy.RunAsync(async () =>
        {
            var d = await _editor.GetDaysAsync();
            var v = hasDay ? await _editor.GetNumberVariantsAsync() : NumberVariantsData.Empty;
            return (d, v);
        });

        _syncingDay = true;
        try
        {
            // Keep the DayOption instances while the day set is the same, so the picker's SelectedItem stays valid.
            if (DayOptions.Count != days.Count || DayOptions.Where((o, i) => o.Number != days[i].Number).Any())
            {
                DayOptions.Clear();
                foreach (var day in days)
                    DayOptions.Add(new DayOption(day, Localization));
            }
            else
                DayOption.SyncAll(DayOptions, days);
            SelectedDay = DayOptions.FirstOrDefault(o => o.Number == _session.CurrentDay?.Number) ?? DayOptions.FirstOrDefault();
        }
        finally
        {
            _syncingDay = false;
        }
        OnPropertyChanged(nameof(ShowDaySelector));
        RefreshDayLock();

        // One option list per group, shared by its rows (a combo matches its selection by reference); a code the group
        // has no variant for gets a list of its own with that code at the end.
        var none = VariantOption.None;
        var optionsByGroup = data.Groups.ToDictionary(
            g => g.GroupId,
            g => (IReadOnlyList<VariantOption>)[none, .. g.Variants.Select(v => new VariantOption(v.Code, v.CourseOrder, known: true))]);
        IReadOnlyList<VariantOption> OptionsFor(NumberVariantRow row)
        {
            var list = row.GroupId is { } id && optionsByGroup.TryGetValue(id, out var o) ? o : [none];
            return row.Course.Length == 0 || list.Any(v => OrientPyx.Plugins.Sdk.Toolkit.TextKey.Comparer.Equals(v.Code, row.Course))
                ? list
                : [.. list, new VariantOption(row.Course, string.Empty, known: false)];
        }

        Rows.Clear();
        foreach (var row in data.Rows)
            Rows.Add(new NumberVariantRowViewModel(row, Localization, OptionsFor(row), OnVariantPicked));
        Unassigned.Clear();
        foreach (var row in data.Unassigned)
            Unassigned.Add(new NumberVariantRowViewModel(row, Localization, OptionsFor(row), OnVariantPicked));
        HasUnassigned = Unassigned.Count > 0;
        IsEmpty = Rows.Count == 0 && Unassigned.Count == 0;
        RefreshSummary();
    }

    partial void OnSelectedDayChanged(DayOption? value)
    {
        if (_syncingDay || value?.Day is null || _session.CurrentDay?.Number == value.Number)
            return;
        _ = _busy.RunAsync(() => _session.SetCurrentDayAsync(value.Day));
    }

    // A variation picked in either table: saved at once (a blank one removes the number's row). The row stays where it
    // is until the next load, so a column of picks never jumps under the operator.
    private void OnVariantPicked(NumberVariantRowViewModel row)
    {
        RefreshSummary();
        if (IsDayLocked)
            return;
        _pendingSaves.Track(SaveAsync(row.Entry));
    }

    private async Task SaveAsync(LegCourseEntry entry)
    {
        await _saveGate.WaitAsync();
        try
        {
            await _editor.SetLegCoursesAsync([entry]);
        }
        catch (DayLockedException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("NumberVariants.Save", ex);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    // «Номерів з варіантом: N · без варіанта: M · …» over both tables as they are now.
    private void RefreshSummary()
    {
        var all = Rows.Concat(Unassigned).ToList();
        var missing = Unassigned.Count(r => r.IsMissing);
        UnassignedTitle = string.Format(CultureInfo.CurrentCulture, Localization.Get("NumberVariants.Unassigned.Title"), missing);
        if (all.Count == 0)
        {
            Summary = string.Empty;
            SummaryIsWarning = false;
            return;
        }

        var mismatch = Rows.Count(r => r.GroupMismatch);
        var unknown = all.Count(r => r.IsUnknown);
        var removed = Rows.Count(r => r.IsMissing);
        var parts = new List<string>(4)
        {
            string.Format(CultureInfo.CurrentCulture, Localization.Get("NumberVariants.Summary.Assigned"), all.Count(r => !r.IsMissing))
        };
        if (missing + removed > 0)
            parts.Add(string.Format(CultureInfo.CurrentCulture, Localization.Get("NumberVariants.Summary.Missing"), missing + removed));
        if (mismatch > 0)
            parts.Add(string.Format(CultureInfo.CurrentCulture, Localization.Get("NumberVariants.Summary.Mismatch"), mismatch));
        if (unknown > 0)
            parts.Add(string.Format(CultureInfo.CurrentCulture, Localization.Get("NumberVariants.Summary.Unknown"), unknown));
        Summary = string.Join(" · ", parts);
        SummaryIsWarning = missing + removed + mismatch + unknown > 0;
    }

    /// <summary>
    /// The upper table's row action (F8 / context menu): offered over rows whose variation names another group than the
    /// team (runner) holding the number. Null — not offered — when the selection has none.
    /// </summary>
    public string? MoveToHolderGroupLabel(IReadOnlyList<object> rows)
    {
        var count = rows.OfType<NumberVariantRowViewModel>().Count(r => r.CanMoveToHolderGroup);
        return count == 0
            ? null
            : string.Format(CultureInfo.CurrentCulture, Localization.Get("NumberVariants.MoveToHolderGroup"), count);
    }

    /// <summary>
    /// Gives the selected mismatched numbers to the group of the team (runner) holding them, keeping the code — when that
    /// group has no such variant the cell turns red and the operator picks one of its own.
    /// </summary>
    public async Task MoveToHolderGroupAsync(IReadOnlyList<object> rows)
    {
        var entries = rows.OfType<NumberVariantRowViewModel>()
            .Where(r => r.CanMoveToHolderGroup)
            .Select(r => new LegCourseEntry(r.Model.Number, r.Model.Leg, r.Course, r.Model.HolderGroupId))
            .ToList();
        if (entries.Count == 0 || !await EnsureDayEditableAsync())
            return;
        await _pendingSaves.WaitAsync();
        await _busy.RunAsync(() => _editor.SetLegCoursesAsync(entries));
        _log.Action(string.Format(CultureInfo.CurrentCulture, Localization.Get("NumberVariants.MoveToHolderGroup.Log"), entries.Count));
        await LoadAsync();
    }

    /// <summary>
    /// Exports the upper table's current on-screen view (captured by the view from the SheetTable) to CSV / Excel: shows
    /// the format modal and serialises the snapshot. Null when there is nothing to export or the user cancelled; the view
    /// runs the save dialog.
    /// </summary>
    public Task<ParticipantExportResult?> ExportAsync(CsvParticipantData view) =>
        _exportFlow.RunAsync(view, day: _session.CurrentDay, nameKey: "NumberVariants.NamePart");
}

/// <summary>One choice of the variation combo: a variant of the row's group, «—» (none) or a code the group lacks.</summary>
public sealed class VariantOption(string code, string courseOrder, bool known)
{
    /// <summary>«—»: no variation (picking it removes the number's row).</summary>
    public static VariantOption None { get; } = new(string.Empty, string.Empty, known: true);

    public string Code { get; } = code;

    public string CourseOrder { get; } = courseOrder;

    /// <summary>False for a code the row's group has no variant for (kept so the row shows what it holds).</summary>
    public bool Known { get; } = known;

    public string Label => Code.Length > 0 ? Code : "—";
}

/// <summary>One table row: a number and leg with its variation (a combo of the group's variants).</summary>
public sealed partial class NumberVariantRowViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly Action<NumberVariantRowViewModel> _picked;

    public NumberVariantRowViewModel(
        NumberVariantRow row, ILocalizationService localization, IReadOnlyList<VariantOption> options,
        Action<NumberVariantRowViewModel> picked)
    {
        Model = row;
        _localization = localization;
        _picked = picked;
        VariantOptions = options;
        _current = options.FirstOrDefault(o => TextKey.Comparer.Equals(o.Code, row.Course)) ?? VariantOption.None;
        _selectedVariant = _current;
    }

    // The last real choice: what the row shows and saves (a combo may push null while it rebuilds — never a choice;
    // «—» is the explicit «none»).
    private VariantOption _current;

    public NumberVariantRow Model { get; }

    public string GroupName => Model.GroupName.Length > 0 ? Model.GroupName : _localization.Get("NumberVariants.NoGroup");

    /// <summary>The group of the team (runner) holding the number; blank when nobody holds it.</summary>
    public string HolderGroupName => Model.HolderGroupName;

    public string Bib => Model.Bib;

    /// <summary>Sorts by number, then leg.</summary>
    public int BibSort => Model.Number * 100 + Math.Min(Model.Leg, 99);

    public string TeamName => Model.TeamName;

    public string Runner => Model.Runner;

    /// <summary>The group's variants (plus «—» and a code it lacks) — the combo's items.</summary>
    public IReadOnlyList<VariantOption> VariantOptions { get; }

    [ObservableProperty]
    private VariantOption? _selectedVariant;

    partial void OnSelectedVariantChanged(VariantOption? value)
    {
        if (value is null || ReferenceEquals(value, _current))
            return;
        _current = value;
        OnPropertyChanged(nameof(Course));
        OnPropertyChanged(nameof(CourseOrder));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(IsUnknown));
        OnPropertyChanged(nameof(IsProblem));
        OnPropertyChanged(nameof(ProblemTooltip));
        OnPropertyChanged(nameof(CanMoveToHolderGroup));
        _picked(this);
    }

    public string Course => _current.Code;

    public string CourseOrder => _current.CourseOrder;

    /// <summary>The number has no variation (yet, or it was just cleared).</summary>
    public bool IsMissing => Course.Length == 0;

    /// <summary>The code names no variant of the row's group.</summary>
    public bool IsUnknown => !_current.Known;

    /// <summary>The variation cell is tinted: none, or a code the group has no variant for.</summary>
    public bool IsProblem => IsMissing || IsUnknown;

    public string? ProblemTooltip =>
        IsMissing ? _localization.Get("NumberVariants.Missing")
        : IsUnknown ? _localization.Get("NumberVariants.Unknown")
        : null;

    /// <summary>The variation names another group than the team (runner) holding the number — it never applies.</summary>
    public bool GroupMismatch => Model.GroupMismatch;

    public string? MismatchTooltip =>
        GroupMismatch
            ? string.Format(CultureInfo.CurrentCulture, _localization.Get("NumberVariants.GroupMismatch"),
                Model.GroupName, Model.HolderGroupName)
            : null;

    /// <summary>F8 «Перенести в групу команди» applies: a mismatched row that still has a variation.</summary>
    public bool CanMoveToHolderGroup => GroupMismatch && Model.HolderGroupId is not null && !IsMissing;

    /// <summary>What a pick writes: this number and leg, the chosen code, the row's group.</summary>
    public LegCourseEntry Entry => new(Model.Number, Model.Leg, Course, Model.GroupId);
}
