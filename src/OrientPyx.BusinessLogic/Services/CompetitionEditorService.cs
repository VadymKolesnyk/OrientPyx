using System.Globalization;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services.Results;
using static OrientPyx.BusinessLogic.Services.Results.CourseCodes;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Default <see cref="ICompetitionEditorService"/>: resolves the current competition's folder
/// from the session and delegates persistence to <see cref="IEventStore"/>.
/// </summary>
public sealed partial class CompetitionEditorService : ICompetitionEditorService
{
    private readonly ISessionService _session;
    private readonly IEventStore _eventStore;
    private readonly IAppStore _appStore;
    private readonly ICourseDistanceCalculator _distance;
    private readonly Disciplines.IDisciplineStrategyProvider _strategies;
    private readonly DayResultsCalculator _calculator;
    private readonly IEntryFeeCalculator _entryFees;
    private readonly IEventFiles _files;
    private readonly IResultsAppData _resultsAppData;
    private readonly PointsRuleCopySync _ruleCopies;
    private readonly IPluginCatalog? _plugins;

    public CompetitionEditorService(
        ISessionService session,
        IEventStore eventStore,
        IAppStore appStore,
        ICourseDistanceCalculator distance,
        Disciplines.IDisciplineStrategyProvider strategies,
        IEntryFeeCalculator entryFees,
        IEventFiles files,
        IResultsAppData resultsAppData,
        PointsRuleCopySync ruleCopies,
        IPluginCatalog? plugins = null)
    {
        _plugins = plugins;
        _ruleCopies = ruleCopies;
        _session = session;
        _eventStore = eventStore;
        _appStore = appStore;
        _files = files;
        _resultsAppData = resultsAppData;
        _distance = distance;
        _strategies = strategies;
        _calculator = new DayResultsCalculator(strategies);
        _entryFees = entryFees;
    }

    private string FolderPath =>
        _session.CurrentEvent?.FolderPath
        ?? throw new InvalidOperationException("No competition is currently selected.");

    // The day a call works on: the session's, unless the call named another one (ResolveDayAsync). AsyncLocal, so the
    // override covers exactly that call and everything it awaits — the finish auto-read keeps reading into its own
    // day while the pages show another.
    private readonly AsyncLocal<EventDay?> _dayOverride = new();

    private EventDay? CurrentDay => _dayOverride.Value ?? _session.CurrentDay;

    private Guid CurrentDayId =>
        CurrentDay?.Id
        ?? throw new InvalidOperationException("No competition day is currently selected.");

    private DisciplineId CurrentDayDefaultDiscipline =>
        CurrentDay?.DefaultDiscipline ?? DisciplineId.SetCourse;

    /// <summary>
    /// The day a call named instead of the session's: (true, null) for none / the session's own day, (true, day) for
    /// another existing day, (false, null) for an unknown one (the caller returns its "nothing" value). The CALLER
    /// sets <see cref="_dayOverride"/> — an AsyncLocal set inside this async helper would not flow back to it.
    /// </summary>
    private async Task<(bool Known, EventDay? Day)> ResolveDayAsync(Guid? dayId, CancellationToken cancellationToken)
    {
        if (dayId is not { } id || id == _session.CurrentDay?.Id || _session.CurrentEvent is null)
            return (true, null);
        var day = (await _eventStore.GetDaysAsync(FolderPath, cancellationToken)).FirstOrDefault(d => d.Id == id);
        return (day is not null, day);
    }

    /// <summary>
    /// Refuses the write when the day is closed for editing. Every per-day write goes through this,
    /// so no path (a page, bulk edit, an import, the auto read-out) can change a finished day —
    /// reads, results, protocols and publishing are deliberately left alone.
    /// </summary>
    private async Task EnsureDayEditableAsync(Guid dayId, CancellationToken cancellationToken)
    {
        var days = await _eventStore.GetDaysAsync(FolderPath, cancellationToken);
        var day = days.FirstOrDefault(d => d.Id == dayId);
        if (day is { IsLocked: true })
            throw new DayLockedException(day.Number);
    }

    /// <summary>
    /// Sets the day's lock and returns the day as it now stands in the database. Null means only
    /// "no such day" — a day already in the requested state is still returned, so a caller that
    /// asked from a stale copy (a cached day option, another window) is handed the real state
    /// instead of a silent no-op it would read as "the button did nothing".
    /// </summary>
    public async Task<EventDay?> SetDayLockedAsync(
        Guid dayId, bool locked, CancellationToken cancellationToken = default)
    {
        var days = await _eventStore.GetDaysAsync(FolderPath, cancellationToken);
        var day = days.FirstOrDefault(d => d.Id == dayId);
        if (day is null)
            return null;

        if (day.IsLocked == locked)
            return day;

        day.IsLocked = locked;
        await _eventStore.UpdateDayAsync(FolderPath, day, cancellationToken);
        return day;
    }

    /// <summary>
    /// Flips the day's lock from what the database currently says, and returns the day as it now
    /// stands (null when there is no such day). The direction is decided under the read, never from
    /// the caller's copy — that copy may predate a change made on another page or in another window.
    /// </summary>
    public async Task<EventDay?> ToggleDayLockedAsync(
        Guid dayId, CancellationToken cancellationToken = default)
    {
        var days = await _eventStore.GetDaysAsync(FolderPath, cancellationToken);
        var day = days.FirstOrDefault(d => d.Id == dayId);
        if (day is null)
            return null;

        day.IsLocked = !day.IsLocked;
        await _eventStore.UpdateDayAsync(FolderPath, day, cancellationToken);
        return day;
    }

    public Task<CompetitionInfo?> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return Task.FromResult<CompetitionInfo?>(null);

        return _eventStore.GetCompetitionInfoAsync(FolderPath, cancellationToken);
    }

    /// <summary>
    /// The competition's start year, used to turn an age class in a group name into birth-year bounds.
    /// Falls back to the current year when the competition has no start date set.
    /// </summary>
    private async Task<int> GetCompetitionStartYearAsync(string folder, CancellationToken cancellationToken)
    {
        var info = await _eventStore.GetCompetitionInfoAsync(folder, cancellationToken);
        return info?.StartDate?.Year ?? DateTimeOffset.Now.Year;
    }

    /// <summary>From the group name and the competition year.</summary>
    private async Task<(int? MinBirthYear, int? MaxBirthYear)> DeriveAgeWindowAsync(
        string name, CancellationToken cancellationToken)
    {
        var startYear = await GetCompetitionStartYearAsync(FolderPath, cancellationToken);
        return Group.DeriveAgeWindow(name, startYear);
    }

    public async Task SaveInfoAsync(CompetitionInfo info, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        await _eventStore.SaveCompetitionInfoAsync(FolderPath, info, cancellationToken);
        // The competition keeps a copy of the default rule, so an instance that does not know its id scores the same.
        await _ruleCopies.EnsureAsync(FolderPath, info.DefaultPointsRuleId, cancellationToken);
    }

    public async Task<IReadOnlyList<PointsRuleChoice>> GetPointsRuleChoicesAsync(CancellationToken cancellationToken = default)
    {
        // Through IResultsAppData, as the results are: on a station the host's catalogue, so a picked rule is one
        // the host knows.
        var catalogue = await _resultsAppData.GetPointsRulesAsync(cancellationToken);
        var choices = catalogue.Select(r => new PointsRuleChoice(r, IsCompetitionCopy: false)).ToList();
        if (_session.CurrentEvent is null)
            return choices;

        var info = await _eventStore.GetCompetitionInfoAsync(FolderPath, cancellationToken);
        var known = catalogue.Select(r => r.Id).ToHashSet();
        choices.AddRange(PointsRuleCopies.Parse(info?.PointsRulesJson)
            .Where(c => !known.Contains(c.Id))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new PointsRuleChoice(c, IsCompetitionCopy: true)));
        return choices;
    }

    public Task RefreshPointsRuleCopiesAsync(CancellationToken cancellationToken = default)
        => _session.CurrentEvent is null
            ? Task.CompletedTask
            : _ruleCopies.RefreshAsync(FolderPath, cancellationToken);

    public async Task<bool> GetRosterEnabledAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return true;

        var info = await _eventStore.GetCompetitionInfoAsync(FolderPath, cancellationToken);
        return info?.RosterEnabled ?? true;
    }

    public Task SetRosterEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return Task.CompletedTask;

        return _eventStore.SetRosterEnabledAsync(FolderPath, enabled, cancellationToken);
    }

    public async Task<DashboardInfo> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var ev = _session.CurrentEvent;
        var day = CurrentDay;
        if (ev is null || day is null)
            return new DashboardInfo { HasSelection = false };

        var folder = ev.FolderPath;

        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var chips = await _eventStore.GetRentalChipsAsync(folder, cancellationToken);
        var allLinks = await _eventStore.GetAllParticipantDaysAsync(folder, cancellationToken);

        // Rental chips: a chip is "handed out" when its number is held by at least one participant on any
        // day. Match the holder map's case-insensitive trimmed-number keying.
        var heldNumbers = new HashSet<string>(TextKey.Comparer);
        foreach (var link in allLinks)
        {
            var chip = link.Chip.Trim();
            if (chip.Length != 0)
                heldNumbers.Add(chip);
        }
        var chipsHandedOut = chips.Count(c => heldNumbers.Contains(c.Number.Trim()));

        // Current-day counts. Join to existing participants — an orphaned link (participant deleted but
        // its per-day link left behind) must not inflate the day counts. Every other view already skips
        // orphans by joining to a participant; the dashboard counts raw links, so filter here too. This
        // keeps «Учасників на дні» and «На дистанції» honest even if a stale orphan lingers in an old DB.
        var participantIds = new HashSet<Guid>(participants.Select(p => p.Id));
        var dayLinks = (await _eventStore.GetParticipantDaysAsync(folder, day.Id, cancellationToken))
            .Where(l => participantIds.Contains(l.ParticipantId))
            .ToList();
        var groupsToday = await _eventStore.GetGroupDaySettingsAsync(folder, day.Id, cancellationToken);
        var readouts = await _eventStore.GetFinishReadoutsAsync(folder, day.Id, cancellationToken);

        // Start times (жеребкування): the day's earliest/latest assigned start and how many are drawn.
        var startTimes = dayLinks
            .Where(l => l.StartTime is not null)
            .Select(l => l.StartTime!.Value)
            .ToList();
        TimeSpan? firstStart = startTimes.Count > 0 ? startTimes.Min() : null;
        TimeSpan? lastStart = startTimes.Count > 0 ? startTimes.Max() : null;
        var withoutChip = dayLinks.Count(l => string.IsNullOrWhiteSpace(l.Chip));
        var withoutGroup = dayLinks.Count(l => l.GroupId is null);

        // Computed exactly as the participant tables do.
        var results = await ComputeDayResultsAsync(folder, day, cancellationToken);

        int finishedOk = 0, finishedProblem = 0, onCourse = 0;
        // Per-status finisher tally, kept in enum order so the dashboard badges read OK, MP, OVT, DNF, …
        var byStatus = new SortedDictionary<FinishStatus, int>();
        // The latest assigned start among runners still on course — the dashboard shows how long the last
        // person who is still out has been running.
        TimeSpan? lastOnCourseStart = null;
        foreach (var link in dayLinks)
        {
            var r = results.TryGetValue(link.Id, out var res) ? res : ParticipantDayResult.Empty;
            if (r.Status != FinishStatus.None)
            {
                // A finisher carries a status (OK or a problem code).
                byStatus[r.Status] = byStatus.GetValueOrDefault(r.Status) + 1;
                if (r.Status == FinishStatus.Ok)
                    finishedOk++;
                else
                    finishedProblem++;
            }
            else if (IsOnCourse(r))
            {
                // Still on course: no actual (chip) start, no finish and no (blank) status — the exact same
                // three-field test the "На дистанції" participants filter uses, so the count matches the
                // filtered list. (A read that produced no start/finish punch and no status counts here too.)
                onCourse++;
                if (link.StartTime is { } st && (lastOnCourseStart is null || st > lastOnCourseStart))
                    lastOnCourseStart = st;
            }
        }

        var finishedByStatus = byStatus
            .Select(kv => new DashboardStatusCount(kv.Key, kv.Value))
            .ToList();

        var dateRange = ev.DateRange;
        if (string.IsNullOrEmpty(dateRange))
        {
            var info = await _eventStore.GetCompetitionInfoAsync(folder, cancellationToken);
            if (info?.StartDate is { } start)
                dateRange = info.EndDate is { } end && end.Date != start.Date
                    ? $"{start:dd.MM.yyyy} – {end:dd.MM.yyyy}"
                    : start.ToString("dd.MM.yyyy");
        }

        return new DashboardInfo
        {
            HasSelection = true,
            CompetitionName = ev.Name,
            Venue = ev.Venue,
            DateRange = dateRange,
            DayCount = ev.DayCount,

            CurrentDayNumber = day.Number,
            CurrentDayDate = day.Date is { } d ? d.ToString("dd.MM.yyyy") : string.Empty,
            CurrentDayDiscipline = day.DefaultDiscipline,

            ParticipantTotal = participants.Count,
            ParticipantsToday = dayLinks.Count,
            GroupsToday = groupsToday.Count,

            ChipsTotal = chips.Count,
            ChipsHandedOut = chipsHandedOut,
            ChipsFree = chips.Count - chipsHandedOut,

            FirstStart = firstStart,
            LastStart = lastStart,
            StartsAssigned = startTimes.Count,
            WithoutChip = withoutChip,
            WithoutGroup = withoutGroup,

            ReadoutsToday = readouts.Count,
            FinishedOk = finishedOk,
            FinishedWithProblem = finishedProblem,
            FinishedTotal = finishedOk + finishedProblem,
            FinishedByStatus = finishedByStatus,
            OnCourse = onCourse,
            LastOnCourseStart = lastOnCourseStart,
        };
    }

    public Task<IReadOnlyList<EventDay>> GetDaysAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return Task.FromResult<IReadOnlyList<EventDay>>([]);

        return _eventStore.GetDaysAsync(FolderPath, cancellationToken);
    }

    public async Task<EventDay> AddDayAsync(CancellationToken cancellationToken = default)
    {
        var days = await _eventStore.GetDaysAsync(FolderPath, cancellationToken);
        var nextNumber = days.Count == 0 ? 1 : days.Max(d => d.Number) + 1;

        var day = new EventDay { Number = nextNumber };
        await _eventStore.AddDayAsync(FolderPath, day, cancellationToken);

        // Where imported XML for the day is stored.
        await _files.EnsureDayFolderAsync(FolderPath, nextNumber, cancellationToken);
        return day;
    }

    // The day row itself (date, venue, default discipline) is day data too, so a closed day refuses
    // these — note SetDayLockedAsync writes through the store directly, so the lock can still be lifted.
    public async Task UpdateDayAsync(EventDay day, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(day);
        await EnsureDayEditableAsync(day.Id, cancellationToken);
        await _eventStore.UpdateDayAsync(FolderPath, day, cancellationToken);
        // A plugin discipline chosen for the first time brings its participant fields (Р-17).
        if (_strategies.For(day.DefaultDiscipline).PluginId is not null)
            await EnsurePluginFieldsAsync(cancellationToken);
    }

    public async Task DeleteDayAsync(Guid dayId, CancellationToken cancellationToken = default)
    {
        await EnsureDayEditableAsync(dayId, cancellationToken);
        var folder = FolderPath;
        var day = (await _eventStore.GetDaysAsync(folder, cancellationToken)).FirstOrDefault(d => d.Id == dayId);
        await _eventStore.DeleteDayAsync(folder, dayId, cancellationToken);
        // Its folder too (imported XML, monitor screens, the default read-out file): a later day with this number
        // must not inherit them.
        if (day is not null)
            await _files.DeleteDayFolderAsync(folder, day.Number, cancellationToken);
    }

    public async Task<EventDay?> ChangeDayNumberAsync(Guid dayId, int newNumber, CancellationToken cancellationToken = default)
    {
        if (newNumber < 1)
            return null;

        await EnsureDayEditableAsync(dayId, cancellationToken);

        var folder = FolderPath;
        var days = await _eventStore.GetDaysAsync(folder, cancellationToken);

        var day = days.FirstOrDefault(d => d.Id == dayId);
        if (day is null || day.Number == newNumber)
            return null;

        // The target number must be free — we swap a single day's number, we don't shift the others.
        if (days.Any(d => d.Id != dayId && d.Number == newNumber))
            return null;

        await _eventStore.UpdateDayNumberAsync(folder, dayId, newNumber, cancellationToken);

        // Keep the day's files folder name in step with its number (best-effort, see IEventFiles).
        await _files.MoveDayFolderAsync(folder, day.Number, newNumber, cancellationToken);

        return new EventDay
        {
            Id = day.Id,
            Number = newNumber,
            Date = day.Date,
            Venue = day.Venue,
            DefaultDiscipline = day.DefaultDiscipline,
            IsLocked = day.IsLocked,
            CreatedAt = day.CreatedAt
        };
    }

    public async Task<string?> SaveDayFileAsync(string fileName, byte[] content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (CurrentDay is not { } day)
            return null;

        // The file lands in the day's folder (on a station: on the host); the stored name comes back.
        return await _files.SaveDayFileAsync(FolderPath, day.Number, fileName ?? string.Empty, content, cancellationToken);
    }

    public Task<IReadOnlyList<ControlPoint>> GetControlPointsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || CurrentDay is null)
            return Task.FromResult<IReadOnlyList<ControlPoint>>([]);

        return _eventStore.GetControlPointsAsync(FolderPath, CurrentDayId, cancellationToken);
    }

    public async Task<ControlPoint> AddControlPointAsync(CancellationToken cancellationToken = default)
    {
        var dayId = CurrentDayId;
        await EnsureDayEditableAsync(dayId, cancellationToken);
        var existing = await _eventStore.GetControlPointsAsync(FolderPath, dayId, cancellationToken);
        var nextOrder = existing.Count == 0 ? 1 : existing.Max(cp => cp.Order) + 1;

        var point = new ControlPoint
        {
            EventDayId = dayId,
            Order = nextOrder,
            Type = ControlPointType.Regular
        };
        await _eventStore.AddControlPointAsync(FolderPath, point, cancellationToken);
        return point;
    }

    public async Task UpdateControlPointAsync(ControlPoint point, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(point);
        // A control point belongs to one day (EventDayId), so its own day decides — not the session's.
        await EnsureDayEditableAsync(point.EventDayId, cancellationToken);
        await _eventStore.UpdateControlPointAsync(FolderPath, point, cancellationToken);
    }

    public async Task DeleteControlPointAsync(Guid pointId, CancellationToken cancellationToken = default)
    {
        await EnsureDayEditableAsync(CurrentDayId, cancellationToken);
        await _eventStore.DeleteControlPointAsync(FolderPath, pointId, cancellationToken);
    }

    public async Task<int> SetProblematicControlsAsync(
        IReadOnlyCollection<Guid> disabledPointIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disabledPointIds);
        if (_session.CurrentEvent is null || CurrentDay is null)
            return 0;

        var dayId = CurrentDayId;
        await EnsureDayEditableAsync(dayId, cancellationToken);

        return await _eventStore.SetControlPointsDisabledAsync(
            FolderPath, dayId, disabledPointIds, cancellationToken);
    }

    public async Task<ControlPointImportResult> ImportControlPointsAsync(
        IofCourseData data,
        bool replaceAll,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var dayId = CurrentDayId;
        var folder = FolderPath;
        await EnsureDayEditableAsync(dayId, cancellationToken);

        // Collapse duplicate codes within the file (a control may also appear as start/finish),
        // keeping the first occurrence so file order — and thus display order — is preserved.
        var parsed = new List<IofControl>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in data.Controls)
        {
            var code = control.Code.Trim();
            if (code.Length > 0 && seen.Add(code))
                parsed.Add(control);
        }

        if (replaceAll)
        {
            var points = new List<ControlPoint>(parsed.Count);
            for (var i = 0; i < parsed.Count; i++)
                points.Add(ToEntity(parsed[i], dayId, order: i + 1, data.MapScale));

            await _eventStore.ReplaceControlPointsAsync(folder, dayId, points, cancellationToken);
            return new ControlPointImportResult(Imported: points.Count, Added: points.Count, Replaced: true);
        }

        // Add-only: append codes the day does not already have, numbering after the last one.
        var existing = await _eventStore.GetControlPointsAsync(folder, dayId, cancellationToken);
        var existingCodes = new HashSet<string>(
            existing.Select(cp => cp.Code.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var nextOrder = existing.Count == 0 ? 1 : existing.Max(cp => cp.Order) + 1;

        var toAdd = new List<ControlPoint>();
        foreach (var control in parsed)
        {
            if (existingCodes.Contains(control.Code.Trim()))
                continue;
            toAdd.Add(ToEntity(control, dayId, order: nextOrder++, data.MapScale));
        }

        await _eventStore.AddControlPointsAsync(folder, toAdd, cancellationToken);
        return new ControlPointImportResult(
            Imported: existing.Count + toAdd.Count,
            Added: toAdd.Count,
            Replaced: false);
    }

    private static ControlPoint ToEntity(IofControl control, Guid dayId, int order, int? mapScale) => new()
    {
        EventDayId = dayId,
        Order = order,
        Code = control.Code.Trim(),
        Latitude = control.Latitude,
        Longitude = control.Longitude,
        MapX = control.MapX,
        MapY = control.MapY,
        MapScale = mapScale,
        Type = control.Type
    };

    public async Task<IReadOnlyList<GroupDayRow>> GetGroupDayRowsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || CurrentDay is null)
            return [];

        var settings = await _eventStore.GetGroupDaySettingsAsync(FolderPath, CurrentDayId, cancellationToken);
        var groups = await _eventStore.GetGroupsAsync(FolderPath, cancellationToken);
        var byId = groups.ToDictionary(g => g.Id);

        // Count this day's participants per group so the grid can show a live headcount per group.
        var links = await _eventStore.GetParticipantDaysAsync(FolderPath, CurrentDayId, cancellationToken);
        var countByGroup = links
            .Where(l => l.GroupId is not null)
            .GroupBy(l => l.GroupId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        // Scatter («розсіювання») variant count per group, so the grid's course-order cell can read
        // «N варіантів дистанції» instead of a single order for a scatter group. Also fold each group's
        // variants down to the control-count of its LONGEST variant — the grid's control-count cell
        // reports that for a scatter group (the single-order field is unused for scatter).
        var scatterVariants = await _eventStore.GetCourseVariantsAsync(FolderPath, CurrentDayId, cancellationToken);
        var scatterByGroup = scatterVariants
            .GroupBy(v => v.GroupId)
            .ToDictionary(
                g => g.Key,
                g => (Count: g.Count(), MaxControls: g.Max(v => CourseOrder.CountCourseControls(v.CourseOrder))));

        var rows = new List<GroupDayRow>(settings.Count);
        foreach (var s in settings)
        {
            // Defensive: skip a settings row whose group was removed out from under it.
            if (byId.TryGetValue(s.GroupId, out var group))
            {
                var scatter = scatterByGroup.GetValueOrDefault(s.GroupId);
                rows.Add(ToRow(s, group,
                    countByGroup.GetValueOrDefault(s.GroupId),
                    scatter.Count,
                    scatter.MaxControls));
            }
        }
        return rows;
    }

    public async Task<IReadOnlyList<CourseVariantRow>> GetCourseVariantsAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || CurrentDay is null)
            return [];

        var rows = await _eventStore.GetCourseVariantsAsync(FolderPath, CurrentDayId, cancellationToken);
        return rows
            .Where(v => v.GroupId == groupId)
            .OrderBy(v => v.Order)
            .Select(v => new CourseVariantRow(v.Code, v.CourseOrder))
            .ToList();
    }

    public async Task SaveCourseVariantsAsync(Guid groupId, IReadOnlyList<CourseVariantRow> variants, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || CurrentDay is null)
            return;

        var dayId = CurrentDayId;
        await EnsureDayEditableAsync(dayId, cancellationToken);

        var order = 0;
        var rows = variants
            .Select(v => new GroupCourseVariant
            {
                EventDayId = dayId,
                GroupId = groupId,
                Code = (v.Code ?? string.Empty).Trim(),
                CourseOrder = (v.CourseOrder ?? string.Empty).Trim(),
                Order = order++
            })
            // Drop fully-blank rows so an empty editor line never persists.
            .Where(v => v.Code.Length > 0 || v.CourseOrder.Length > 0)
            .ToList();

        await _eventStore.ReplaceCourseVariantsForGroupAsync(FolderPath, dayId, groupId, rows, cancellationToken);
    }

    public Task<IReadOnlyList<Group>> GetGroupsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return Task.FromResult<IReadOnlyList<Group>>([]);

        return _eventStore.GetGroupsAsync(FolderPath, cancellationToken);
    }

    public Task UpdateGroupEntryFeeAsync(Guid groupId, decimal? entryFee, CancellationToken cancellationToken = default) =>
        _eventStore.UpdateGroupEntryFeeAsync(FolderPath, groupId, entryFee, cancellationToken);

    public async Task<GroupDayRow> AddGroupToDayAsync(string name, CancellationToken cancellationToken = default)
    {
        var dayId = CurrentDayId;
        await EnsureDayEditableAsync(dayId, cancellationToken);
        var trimmed = (name ?? string.Empty).Trim();

        // Match on name, case-insensitively.
        var groups = await _eventStore.GetGroupsAsync(FolderPath, cancellationToken);
        var group = groups.FirstOrDefault(g => TextKey.Same(g.Name, trimmed));
        if (group is null)
        {
            var (min, max) = await DeriveAgeWindowAsync(trimmed, cancellationToken);
            group = new Group { Name = trimmed, MinBirthYear = min, MaxBirthYear = max };
            await _eventStore.AddGroupAsync(FolderPath, group, cancellationToken);
        }

        // Already on this day ⇒ return the existing row.
        var settings = await _eventStore.GetGroupDaySettingsAsync(FolderPath, dayId, cancellationToken);
        var existing = settings.FirstOrDefault(s => s.GroupId == group.Id);
        if (existing is not null)
            return ToRow(existing, group);

        var nextOrder = settings.Count == 0 ? 1 : settings.Max(s => s.Order) + 1;
        var row = new GroupDaySettings
        {
            EventDayId = dayId,
            GroupId = group.Id,
            Order = nextOrder
        };
        await _eventStore.AddGroupDaySettingsAsync(FolderPath, row, cancellationToken);
        return ToRow(row, group);
    }

    public async Task<IReadOnlyList<GroupDayRow>> PullAllGroupsIntoDayAsync(CancellationToken cancellationToken = default)
    {
        var dayId = CurrentDayId;
        await EnsureDayEditableAsync(dayId, cancellationToken);

        var groups = await _eventStore.GetGroupsAsync(FolderPath, cancellationToken);
        var settings = await _eventStore.GetGroupDaySettingsAsync(FolderPath, dayId, cancellationToken);
        var present = settings.Select(s => s.GroupId).ToHashSet();
        var nextOrder = settings.Count == 0 ? 1 : settings.Max(s => s.Order) + 1;

        var toAdd = new List<GroupDaySettings>();
        foreach (var group in groups)
        {
            if (present.Contains(group.Id))
                continue;
            toAdd.Add(new GroupDaySettings
            {
                EventDayId = dayId,
                GroupId = group.Id,
                Order = nextOrder++
            });
        }

        await _eventStore.AddGroupDaySettingsRangeAsync(FolderPath, toAdd, cancellationToken);
        return await GetGroupDayRowsAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RequiredFieldsEntry>> GetRequiredFieldsAsync(CancellationToken cancellationToken = default)
    {
        var folder = FolderPath;
        var result = new List<RequiredFieldsEntry>();
        foreach (var day in await _eventStore.GetDaysAsync(folder, cancellationToken))
        {
            foreach (var gs in await _eventStore.GetGroupDaySettingsAsync(folder, day.Id, cancellationToken))
            {
                var required = _strategies.For(gs.DisciplineOverride ?? day.DefaultDiscipline).RequiredFields;
                if (required.Count > 0)
                    result.Add(new RequiredFieldsEntry(day.Id, gs.GroupId, required));
            }
        }
        return result;
    }

    public async Task<GroupNameClash?> FindGroupNameClashAsync(Guid groupId, string name, CancellationToken cancellationToken = default)
    {
        var target = (name ?? string.Empty).Trim();
        if (target.Length == 0)
            return null;
        var groups = await _eventStore.GetGroupsAsync(FolderPath, cancellationToken);
        var other = groups.FirstOrDefault(g => g.Id != groupId && TextKey.Same(g.Name, target));
        return other is null
            ? null
            : new GroupNameClash(other.Name.Trim(), groups.FirstOrDefault(g => g.Id == groupId)?.Name.Trim() ?? string.Empty);
    }

    public async Task<bool> UpdateGroupDayRowAsync(GroupDayRow row, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        await EnsureDayEditableAsync(CurrentDayId, cancellationToken);

        // Rename the group (affects every day it runs on); empty names are ignored. A name that matches
        // another group (TextKey — so a Latin/Cyrillic look-alike counts too) merges this group into it.
        var target = (row.Name ?? string.Empty).Trim();
        if (target.Length > 0)
        {
            var groups = await _eventStore.GetGroupsAsync(FolderPath, cancellationToken);
            var other = groups.FirstOrDefault(g =>
                g.Id != row.GroupId && TextKey.Same(g.Name, target));
            if (other is not null)
            {
                await _eventStore.MergeGroupAsync(FolderPath, row.GroupId, other.Id, cancellationToken);
                return true;
            }
            await _eventStore.UpdateGroupAsync(FolderPath, new Group { Id = row.GroupId, Name = target }, cancellationToken);
        }

        // The age window is a group-level field (shared across days) but edited from this per-day grid, so
        // it is written straight to the group rather than the per-day settings below.
        await _eventStore.UpdateGroupAgeWindowAsync(
            FolderPath, row.GroupId, row.MinBirthYear, row.MaxBirthYear, cancellationToken);

        await _eventStore.UpdateGroupDaySettingsAsync(FolderPath, new GroupDaySettings
        {
            Id = row.SettingsId,
            EventDayId = CurrentDayId,
            GroupId = row.GroupId,
            Order = row.Order,
            CourseOrder = (row.CourseOrder ?? string.Empty).Trim(),
            DistanceKm = row.DistanceKm,
            DisciplineOverride = row.DisciplineOverride,
            TimeLimitSeconds = row.TimeLimitSeconds,
            RequiredControlCount = row.RequiredControlCount,
            PenaltyPerMinute = row.PenaltyPerMinute,
            CourseSetter = (row.CourseSetter ?? string.Empty).Trim(),
            CourseSetterCategory = (row.CourseSetterCategory ?? string.Empty).Trim(),
            PointsRuleId = row.PointsRuleId,
            RankLevel = row.RankLevel,
            MasterCount = row.MasterCount
        }, cancellationToken);
        await _ruleCopies.EnsureAsync(FolderPath, row.PointsRuleId, cancellationToken);
        if (row.DisciplineOverride is { } overrideId && _strategies.For(overrideId).PluginId is not null)
            await EnsurePluginFieldsAsync(cancellationToken);
        return false;
    }

    public async Task<int> RecalculateGroupAgeWindowsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return 0;

        var folder = FolderPath;
        var groups = await _eventStore.GetGroupsAsync(folder, cancellationToken);
        var startYear = await GetCompetitionStartYearAsync(folder, cancellationToken);

        var updated = 0;
        foreach (var group in groups)
        {
            var (min, max) = Group.DeriveAgeWindow(group.Name, startYear);
            // Skip groups whose window already matches what the name derives — nothing to write.
            if (group.MinBirthYear == min && group.MaxBirthYear == max)
                continue;

            await _eventStore.UpdateGroupAgeWindowAsync(folder, group.Id, min, max, cancellationToken);
            updated++;
        }
        return updated;
    }

    public async Task RemoveGroupFromDayAsync(Guid settingsId, Guid groupId, CancellationToken cancellationToken = default)
    {
        await EnsureDayEditableAsync(CurrentDayId, cancellationToken);

        await _eventStore.DeleteRelayTeamsForGroupDayAsync(FolderPath, CurrentDayId, groupId, cancellationToken);
        await _eventStore.ReplaceGroupLegCourseAssignmentsAsync(FolderPath, CurrentDayId, groupId, [], cancellationToken);
        await _eventStore.DeleteGroupDaySettingsAsync(FolderPath, settingsId, cancellationToken);

        // A group that no longer runs on any day is removed entirely.
        var remaining = await _eventStore.CountGroupDaySettingsForGroupAsync(FolderPath, groupId, cancellationToken);
        if (remaining == 0)
            await _eventStore.DeleteGroupAsync(FolderPath, groupId, cancellationToken);
    }

    public async Task<GroupImportResult> ImportGroupsAsync(
        IofCourseData data,
        bool updateExisting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var dayId = CurrentDayId;
        await EnsureDayEditableAsync(dayId, cancellationToken);

        var folder = FolderPath;

        // Coordinate lookup for distance, keyed by control code (case-insensitive). The day's saved
        // control points win (they may be hand-edited); the file's own controls fill any gap so the
        // import still computes a distance even before КП were imported. A code missing from both, or
        // with no coordinates, makes its legs count as 0 km.
        var coordsByCode = new Dictionary<string, GeoPoint>(StringComparer.OrdinalIgnoreCase);
        // Paper-map positions (mm) from the same file: preferred distance source, since map mm × scale
        // is the undistorted course distance orienteering software prints — unlike the Web Mercator
        // geographic export, which is stretched by 1/cos(latitude). Some exports (Condes 3.0) carry
        // ONLY a MapPosition, so this is also the only distance source available for them.
        var mapByCode = new Dictionary<string, MapPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in data.Controls)
        {
            var code = control.Code.Trim();
            if (code.Length == 0)
                continue;
            coordsByCode[code] = new GeoPoint(control.Latitude, control.Longitude);
            mapByCode[code] = new MapPoint(control.MapX, control.MapY);
        }
        var controlPoints = await _eventStore.GetControlPointsAsync(folder, dayId, cancellationToken);
        foreach (var cp in controlPoints)
        {
            // Let a saved point override the file only when it actually carries coordinates, so a
            // blank КP row doesn't wipe out usable coordinates parsed from the same file.
            var point = new GeoPoint(cp.Latitude, cp.Longitude);
            if (point.HasCoordinates || !coordsByCode.ContainsKey(cp.Code.Trim()))
                coordsByCode[cp.Code.Trim()] = point;
        }

        var groups = await _eventStore.GetGroupsAsync(folder, cancellationToken);
        var groupsByName = groups
            .GroupBy(g => g.Name.Trim(), TextKey.Comparer)
            .ToDictionary(g => g.Key, g => g.First(), TextKey.Comparer);
        var startYear = await GetCompetitionStartYearAsync(folder, cancellationToken);

        var settings = await _eventStore.GetGroupDaySettingsAsync(folder, dayId, cancellationToken);
        var settingsByGroupId = settings.ToDictionary(s => s.GroupId);
        var nextOrder = settings.Count == 0 ? 1 : settings.Max(s => s.Order) + 1;

        var added = 0;
        var updated = 0;
        var scatterFallback = new List<string>();

        // On a Scatter («розсіювання») day, a course that turns out to have only one valid running order is not a
        // scatter course at all, so it must not inherit the day's Scatter default — it is forced to «Заданий
        // напрямок» (SetCourse) and reported. On a non-Scatter day a single-order course simply clears its
        // override (day default applies).
        var dayIsScatter = _strategies.For(CurrentDayDefaultDiscipline).UsesCourseVariants;
        var dayIsRelay = IsRelay(CurrentDayDefaultDiscipline);

        // Collapse duplicate course names within the file (keep the first), so a repeated course
        // does not create a second group or fight itself for the same day row.
        var seenNames = new HashSet<string>(TextKey.Comparer);

        foreach (var course in data.Courses)
        {
            var name = course.Name.Trim();
            if (name.Length == 0 || !seenNames.Add(name))
                continue;

            // How the file's variations are used — chosen per course in «Розділення груп». A relay («Естафета», за
            // номером етапу / набір на команду) keeps every variation under its own code, its legs from the file's team
            // combinations. Otherwise a course with more than one distinct running order is a scatter («розсіювання»)
            // course: the distinct orders — or, when the file hands the variations out to numbers, all of them, so each
            // number's map code is a stored variant. The group's own course order/distance mirror the first variation.
            var relayMode = course.HasVariations
                ? course.VariationUse switch
                {
                    CourseVariationUse.RelayByBib => RelayCourseMode.ByBib,
                    CourseVariationUse.RelayTeamSet => RelayCourseMode.TeamSet,
                    _ => (RelayCourseMode?)null
                }
                : null;
            var isRelayCourse = relayMode is not null;
            var isScatter = !isRelayCourse && course.Variants.Count > 1;
            var variants = isRelayCourse || (isScatter && course.TeamCombinations.Count > 0)
                ? course.AllVariants
                : isScatter ? course.Variants : [];
            var assignments = isRelayCourse || isScatter
                ? course.TeamCombinations
                    .SelectMany(t => t.Legs.Select(l => new LegCourseAssignment
                    {
                        EventDayId = dayId, TeamNumber = t.Number, Leg = l.Leg, CourseKey = l.Code
                    }))
                    .ToList()
                : [];
            var legCount = isRelayCourse && course.CombinationLegCount > 0 ? course.CombinationLegCount : (int?)null;

            // The discipline override to store: Scatter for a genuine multi-order course; Relay for a relay course (none
            // on a relay day — the day's own relay type applies); on a Scatter day a single-order course is pinned to
            // SetCourse so it doesn't wrongly inherit Scatter; otherwise no override (the day default applies).
            var scatterFallbackHere = !isScatter && !isRelayCourse && dayIsScatter;
            var disciplineOverride =
                isScatter ? DisciplineId.Scatter
                : isRelayCourse ? (dayIsRelay ? null : DisciplineId.Relay)
                : scatterFallbackHere ? DisciplineId.SetCourse
                : (DisciplineId?)null;
            var courseOrder = string.Join(' ', course.ControlCodes.Select(c => c.Trim()).Where(c => c.Length > 0));
            var distanceKm = ComputeDistance(course.ControlCodes, coordsByCode, mapByCode, data.MapScale);

            // Resolve (or create) the competition-level group for this course name.
            if (!groupsByName.TryGetValue(name, out var group))
            {
                var (min, max) = Group.DeriveAgeWindow(name, startYear);
                group = new Group { Name = name, MinBirthYear = min, MaxBirthYear = max };
                await _eventStore.AddGroupAsync(folder, group, cancellationToken);
                groupsByName[name] = group;
            }

            if (settingsByGroupId.TryGetValue(group.Id, out var existing))
            {
                // Already on the day. In add-only mode leave it as-is. Otherwise refresh the course from the file
                // but keep what the file cannot know: a type the judge chose for the group, a hand-written mixed
                // pattern, a relay group's named leg courses (unless the file brings a relay of its own).
                if (!updateExisting)
                    continue;

                var effective = _strategies.For(existing.DisciplineOverride ?? CurrentDayDefaultDiscipline);
                var isRelay = effective.Competitors == CompetitorModel.TeamWithLegs;
                var keepsPattern = effective.UsesCoursePattern && CourseVariantData.IsPatternText(existing.CourseOrder);

                if (!keepsPattern)
                {
                    existing.CourseOrder = courseOrder;
                    existing.DistanceKm = distanceKm;
                }

                // The type: the chosen relay makes the group a relay (a relay type it already has — a plugin's — stays);
                // a multi-order course makes it Scatter; a group that was Scatter and is single-order now gets the
                // import's own fallback; any other chosen type stays.
                var wasScatter = existing.DisciplineOverride == DisciplineId.Scatter;
                if (isRelayCourse)
                {
                    if (!isRelay)
                        existing.DisciplineOverride = disciplineOverride;
                }
                else if (isScatter)
                    existing.DisciplineOverride = DisciplineId.Scatter;
                else if (wasScatter)
                    existing.DisciplineOverride = disciplineOverride;

                await _eventStore.UpdateGroupDaySettingsAsync(folder, existing, cancellationToken);
                if (relayMode is { } mode)
                {
                    if (legCount is { } legs && legs != existing.LegCount)
                        await _eventStore.UpdateRelaySettingsAsync(folder, existing.Id, legs, existing.MassStartTime,
                            existing.RestartTimesJson, existing.RestartRanking, cancellationToken);
                    await _eventStore.UpdateRelayCourseSettingsAsync(folder, existing.Id, mode, existing.TeamCourseSet, cancellationToken);
                }

                // Variants: a relay or scatter course replaces them (and its number assignments); a relay group keeps
                // its named leg courses when the file brings none; a hand-kept mixed pattern keeps whatever it had.
                if (isRelayCourse || isScatter || (!isRelay && !keepsPattern))
                {
                    await SaveCourseVariantsAsync(folder, dayId, group.Id, variants, cancellationToken);
                    await _eventStore.ReplaceGroupLegCourseAssignmentsAsync(folder, dayId, group.Id, assignments, cancellationToken);
                }
                if (scatterFallbackHere && wasScatter)
                    scatterFallback.Add(name);
                updated++;
            }
            else
            {
                var row = new GroupDaySettings
                {
                    EventDayId = dayId,
                    GroupId = group.Id,
                    Order = nextOrder++,
                    CourseOrder = courseOrder,
                    DistanceKm = distanceKm,
                    DisciplineOverride = disciplineOverride,
                    LegCount = legCount,
                    CourseAssignmentMode = relayMode ?? RelayCourseMode.Group
                };
                await _eventStore.AddGroupDaySettingsAsync(folder, row, cancellationToken);
                settingsByGroupId[group.Id] = row;
                await SaveCourseVariantsAsync(folder, dayId, group.Id, variants, cancellationToken);
                await _eventStore.ReplaceGroupLegCourseAssignmentsAsync(folder, dayId, group.Id, assignments, cancellationToken);
                if (scatterFallbackHere)
                    scatterFallback.Add(name);
                added++;
            }
        }

        return new GroupImportResult(Added: added, Updated: updated, ScatterFallbackGroups: scatterFallback);
    }

    // Persists a course's named variants (scatter orders / relay courses) for a group on a day, replacing any existing
    // set: one row per variant, coded and ordered as parsed. Fewer than two clear the group's variants (so re-importing
    // a formerly-scatter group as a normal course leaves no stale orders behind).
    private Task SaveCourseVariantsAsync(
        string folder, Guid dayId, Guid groupId, IReadOnlyList<IofCourseVariant> variants, CancellationToken cancellationToken)
    {
        var rows = new List<GroupCourseVariant>();
        if (variants.Count > 1)
        {
            var order = 0;
            foreach (var v in variants)
                rows.Add(new GroupCourseVariant
                {
                    EventDayId = dayId,
                    GroupId = groupId,
                    Code = v.Code,
                    CourseOrder = string.Join(' ', v.ControlCodes.Select(c => c.Trim()).Where(c => c.Length > 0)),
                    Order = order++
                });
        }

        return _eventStore.ReplaceCourseVariantsForGroupAsync(folder, dayId, groupId, rows, cancellationToken);
    }

    // Maps a course's running control codes to their positions and sums the straight-line distance.
    // Prefers paper-map positions (mm × scale) — the undistorted course distance orienteering software
    // prints — and falls back to geographic coordinates only when the file carries no usable map data.
    // Unknown codes resolve to a coordinate-less point, so their legs count as 0 km.
    private decimal? ComputeDistance(
        IReadOnlyList<string> controlCodes,
        IReadOnlyDictionary<string, GeoPoint> coordsByCode,
        IReadOnlyDictionary<string, MapPoint> mapByCode,
        int? mapScale)
    {
        if (controlCodes.Count < 2)
            return null;

        // Use map mm × scale when the file states a scale and at least one leg has both endpoints
        // mapped; otherwise the result would be a misleading 0 and we should fall back to geography.
        if (mapScale is > 0)
        {
            var mapPoints = new List<MapPoint>(controlCodes.Count);
            foreach (var code in controlCodes)
                mapPoints.Add(mapByCode.TryGetValue(code.Trim(), out var p) ? p : new MapPoint(null, null));

            if (HasAnyLeg(mapPoints, static p => p.HasCoordinates))
                return _distance.TotalKilometresFromMap(mapPoints, mapScale.Value);
        }

        var points = new List<GeoPoint>(controlCodes.Count);
        foreach (var code in controlCodes)
            points.Add(coordsByCode.TryGetValue(code.Trim(), out var p) ? p : new GeoPoint(null, null));

        return _distance.TotalKilometres(points);
    }

    // True when at least one consecutive pair has coordinates on both ends, i.e. a leg contributes a
    // non-zero distance. Guards against preferring a positional source that would sum to a flat 0.
    private static bool HasAnyLeg<T>(IReadOnlyList<T> points, Func<T, bool> hasCoordinates)
    {
        for (var i = 1; i < points.Count; i++)
            if (hasCoordinates(points[i - 1]) && hasCoordinates(points[i]))
                return true;
        return false;
    }

    public Task<IReadOnlyList<RentalChip>> GetRentalChipsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return Task.FromResult<IReadOnlyList<RentalChip>>([]);

        return _eventStore.GetRentalChipsAsync(FolderPath, cancellationToken);
    }

    public async Task<RentalChip> AddRentalChipAsync(CancellationToken cancellationToken = default)
    {
        var chip = new RentalChip();
        await _eventStore.AddRentalChipAsync(FolderPath, chip, cancellationToken);
        return chip;
    }

    public async Task UpdateRentalChipAsync(RentalChip chip, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chip);

        // Keep numbers unique per competition: a change colliding with a different chip is dropped
        // (the row reverts on the next reload), mirroring the group rename behaviour.
        var number = (chip.Number ?? string.Empty).Trim();
        var chips = await _eventStore.GetRentalChipsAsync(FolderPath, cancellationToken);
        var collides = number.Length > 0 && chips.Any(c =>
            c.Id != chip.Id && TextKey.Same(c.Number.Trim(), number));
        if (collides)
            number = chips.First(c => c.Id == chip.Id).Number; // revert to the stored number

        await _eventStore.UpdateRentalChipAsync(
            FolderPath,
            new RentalChip { Id = chip.Id, Number = number, Note = (chip.Note ?? string.Empty).Trim() },
            cancellationToken);
    }

    public Task DeleteRentalChipAsync(Guid chipId, CancellationToken cancellationToken = default)
        => _eventStore.DeleteRentalChipAsync(FolderPath, chipId, cancellationToken);

    public async Task<IReadOnlyDictionary<string, string>> GetRentalChipHoldersAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return new Dictionary<string, string>();

        var folder = FolderPath;
        var links = await _eventStore.GetAllParticipantDaysAsync(folder, cancellationToken);
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var nameById = participants.ToDictionary(p => p.Id, p => p.FullName);

        // Group every (chip → participant) assignment by chip number (case-insensitive). The same
        // participant may hold a chip on several days; dedupe by participant id so a name appears once.
        // A blank chip or a link whose participant has gone missing is skipped.
        var holders = new Dictionary<string, (List<string> Names, HashSet<Guid> Seen)>(TextKey.Comparer);
        foreach (var link in links)
        {
            var number = link.Chip.Trim();
            if (number.Length == 0 || !nameById.TryGetValue(link.ParticipantId, out var name))
                continue;

            if (!holders.TryGetValue(number, out var entry))
            {
                entry = ([], []);
                holders[number] = entry;
            }
            if (entry.Seen.Add(link.ParticipantId))
                entry.Names.Add(name);
        }

        return holders.ToDictionary(
            kvp => kvp.Key,
            kvp => string.Join(", ", kvp.Value.Names),
            TextKey.Comparer);
    }

    public async Task<RentalChipHoldersByDay> GetRentalChipHoldersByDayAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return RentalChipHoldersByDay.Empty;

        var folder = FolderPath;
        var days = (await _eventStore.GetDaysAsync(folder, cancellationToken)).OrderBy(d => d.Number).ToList();
        var links = await _eventStore.GetAllParticipantDaysAsync(folder, cancellationToken);
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var nameById = participants.ToDictionary(p => p.Id, p => p.FullName);
        var indexByDayId = days.Select((d, i) => (d.Id, i)).ToDictionary(x => x.Id, x => x.i);

        // chip → one name list per day. A chip is unique per day, but a stray duplicate is joined rather
        // than hidden so the grid still shows it. Blank chips and orphaned links are skipped.
        var holders = new Dictionary<string, List<string>[]>(TextKey.Comparer);
        foreach (var link in links)
        {
            var number = link.Chip.Trim();
            if (number.Length == 0
                || !nameById.TryGetValue(link.ParticipantId, out var name)
                || !indexByDayId.TryGetValue(link.EventDayId, out var dayIndex))
                continue;

            if (!holders.TryGetValue(number, out var perDay))
            {
                perDay = days.Select(_ => new List<string>()).ToArray();
                holders[number] = perDay;
            }
            if (!perDay[dayIndex].Contains(name))
                perDay[dayIndex].Add(name);
        }

        var byChip = holders.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)kvp.Value.Select(names => string.Join(", ", names)).ToList(),
            TextKey.Comparer);
        return new RentalChipHoldersByDay(days.Select(d => d.Number).ToList(), byChip);
    }

    public Task<int> ClearRentalChipsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return Task.FromResult(0);

        return _eventStore.DeleteAllRentalChipsAsync(FolderPath, cancellationToken);
    }

    public async Task<RentalChipBulkResult> AddRentalChipRangeAsync(
        string startNumber,
        int count,
        string note,
        CancellationToken cancellationToken = default)
    {
        var start = (startNumber ?? string.Empty).Trim();
        if (count <= 0 || start.Length == 0 || !ulong.TryParse(start, out var first))
            return new RentalChipBulkResult(0, 0);

        var folder = FolderPath;
        var existing = await _eventStore.GetRentalChipsAsync(folder, cancellationToken);
        var existingNumbers = new HashSet<string>(
            existing.Select(c => c.Number.Trim()),
            TextKey.Comparer);

        // Keep the digit width: "0042" ⇒ "0043", not "43".
        var width = start.Length;
        var trimmedNote = (note ?? string.Empty).Trim();

        var toAdd = new List<RentalChip>();
        var skipped = 0;
        for (var i = 0; i < count; i++)
        {
            var number = (first + (ulong)i).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
            if (!existingNumbers.Add(number))
            {
                skipped++;
                continue;
            }
            toAdd.Add(new RentalChip { Number = number, Note = trimmedNote });
        }

        await _eventStore.AddRentalChipsAsync(folder, toAdd, cancellationToken);
        return new RentalChipBulkResult(Added: toAdd.Count, Skipped: skipped);
    }

    public async Task<RentalChipImportResult> ImportRentalChipsAsync(
        ChipReadData data,
        string note,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        var folder = FolderPath;
        var existing = await _eventStore.GetRentalChipsAsync(folder, cancellationToken);
        var existingNumbers = new HashSet<string>(
            existing.Select(c => c.Number.Trim()),
            TextKey.Comparer);
        var trimmedNote = (note ?? string.Empty).Trim();

        // The readout is a raw log: collapse duplicate numbers and add only the ones we don't have.
        // Existing chips are never touched and none are removed for being absent from the file.
        var toAdd = new List<RentalChip>();
        var skipped = 0;
        foreach (var record in data.Records)
        {
            var number = record.ChipNumber.Trim();
            if (number.Length == 0)
                continue;
            if (!existingNumbers.Add(number))
            {
                skipped++;
                continue;
            }
            toAdd.Add(new RentalChip { Number = number, Note = trimmedNote });
        }

        await _eventStore.AddRentalChipsAsync(folder, toAdd, cancellationToken);
        return new RentalChipImportResult(Added: toAdd.Count, Skipped: skipped);
    }

    public async Task<RentalChipBulkResult> AddRentalChipsAsync(
        IReadOnlyList<(string Number, string Note)> chips,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chips);
        if (_session.CurrentEvent is null || chips.Count == 0)
            return new RentalChipBulkResult(0, 0);

        var folder = FolderPath;
        var existing = await _eventStore.GetRentalChipsAsync(folder, cancellationToken);
        var existingNumbers = new HashSet<string>(
            existing.Select(c => c.Number.Trim()),
            TextKey.Comparer);

        var toAdd = new List<RentalChip>();
        var skipped = 0;
        foreach (var (rawNumber, rawNote) in chips)
        {
            var number = (rawNumber ?? string.Empty).Trim();
            if (number.Length == 0)
                continue;
            if (!existingNumbers.Add(number))
            {
                skipped++;
                continue;
            }
            toAdd.Add(new RentalChip { Number = number, Note = (rawNote ?? string.Empty).Trim() });
        }

        await _eventStore.AddRentalChipsAsync(folder, toAdd, cancellationToken);
        return new RentalChipBulkResult(Added: toAdd.Count, Skipped: skipped);
    }

    public Task<IReadOnlyList<Region>> GetRegionsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return Task.FromResult<IReadOnlyList<Region>>([]);

        return _eventStore.GetRegionsAsync(FolderPath, cancellationToken);
    }

    public async Task<Region> AddRegionRowAsync(CancellationToken cancellationToken = default)
    {
        var region = new Region();
        await _eventStore.AddRegionAsync(FolderPath, region, cancellationToken);
        return region;
    }

    public async Task<Region?> AddRegionAsync(string name, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return null;

        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return null;

        // Match on name, case-insensitively.
        var regions = await _eventStore.GetRegionsAsync(FolderPath, cancellationToken);
        var region = regions.FirstOrDefault(r => TextKey.Same(r.Name, trimmed));
        if (region is null)
        {
            region = new Region { Name = trimmed };
            await _eventStore.AddRegionAsync(FolderPath, region, cancellationToken);
        }
        return region;
    }

    public async Task UpdateRegionAsync(Region region, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(region);

        // Keep names unique per competition: a change colliding with a different region is dropped
        // (the row reverts on the next reload), mirroring the group rename / rental-chip behaviour.
        var name = (region.Name ?? string.Empty).Trim();
        var regions = await _eventStore.GetRegionsAsync(FolderPath, cancellationToken);
        var collides = name.Length > 0 && regions.Any(r =>
            r.Id != region.Id && TextKey.Same(r.Name.Trim(), name));
        if (collides || name.Length == 0)
            name = regions.FirstOrDefault(r => r.Id == region.Id)?.Name ?? name;

        await _eventStore.UpdateRegionAsync(FolderPath, new Region { Id = region.Id, Name = name }, cancellationToken);
    }

    public async Task DeleteRegionAsync(Guid regionId, CancellationToken cancellationToken = default)
    {
        var folder = FolderPath;
        // Optional on a participant — clear it from anyone using it first.
        await _eventStore.ClearParticipantsRegionAsync(folder, regionId, cancellationToken);
        await _eventStore.DeleteRegionAsync(folder, regionId, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetRegionParticipantCountsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return new Dictionary<Guid, int>();

        var folder = FolderPath;
        var regions = await _eventStore.GetRegionsAsync(folder, cancellationToken);
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);

        // Seed every region at 0 so a region with no participants still shows a count.
        var counts = regions.ToDictionary(r => r.Id, _ => 0);
        foreach (var p in participants)
        {
            if (p.RegionId is { } id && counts.ContainsKey(id))
                counts[id]++;
        }
        return counts;
    }

    public async Task SetParticipantRegionAsync(Guid participantId, Guid? regionId, CancellationToken cancellationToken = default)
    {
        var folder = FolderPath;
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var participant = participants.FirstOrDefault(p => p.Id == participantId);
        if (participant is null)
            return;

        // Competition-level, so it lives on the participant identity row.
        participant.RegionId = regionId;
        await _eventStore.UpdateParticipantAsync(folder, participant, cancellationToken);
    }

    public Task<IReadOnlyList<Club>> GetClubsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return Task.FromResult<IReadOnlyList<Club>>([]);

        return _eventStore.GetClubsAsync(FolderPath, cancellationToken);
    }

    public async Task<Club> AddClubRowAsync(CancellationToken cancellationToken = default)
    {
        var club = new Club();
        await _eventStore.AddClubAsync(FolderPath, club, cancellationToken);
        return club;
    }

    public async Task<Club?> AddClubAsync(string name, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return null;

        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return null;

        var clubs = await _eventStore.GetClubsAsync(FolderPath, cancellationToken);
        var club = clubs.FirstOrDefault(c => TextKey.Same(c.Name, trimmed));
        if (club is null)
        {
            club = new Club { Name = trimmed };
            await _eventStore.AddClubAsync(FolderPath, club, cancellationToken);
        }
        return club;
    }

    public async Task UpdateClubAsync(Club club, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(club);

        var name = (club.Name ?? string.Empty).Trim();
        var clubs = await _eventStore.GetClubsAsync(FolderPath, cancellationToken);
        var collides = name.Length > 0 && clubs.Any(c =>
            c.Id != club.Id && TextKey.Same(c.Name.Trim(), name));
        if (collides || name.Length == 0)
            name = clubs.FirstOrDefault(c => c.Id == club.Id)?.Name ?? name;

        await _eventStore.UpdateClubAsync(FolderPath, new Club { Id = club.Id, Name = name }, cancellationToken);
    }

    public async Task DeleteClubAsync(Guid clubId, CancellationToken cancellationToken = default)
    {
        var folder = FolderPath;
        await _eventStore.ClearParticipantsClubAsync(folder, clubId, cancellationToken);
        await _eventStore.DeleteClubAsync(folder, clubId, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetClubParticipantCountsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return new Dictionary<Guid, int>();

        var folder = FolderPath;
        var clubs = await _eventStore.GetClubsAsync(folder, cancellationToken);
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);

        var counts = clubs.ToDictionary(c => c.Id, _ => 0);
        foreach (var p in participants)
        {
            if (p.ClubId is { } id && counts.ContainsKey(id))
                counts[id]++;
        }
        return counts;
    }

    public async Task SetParticipantClubAsync(Guid participantId, Guid? clubId, CancellationToken cancellationToken = default)
    {
        var folder = FolderPath;
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var participant = participants.FirstOrDefault(p => p.Id == participantId);
        if (participant is null)
            return;

        participant.ClubId = clubId;
        await _eventStore.UpdateParticipantAsync(folder, participant, cancellationToken);
    }

    public Task<IReadOnlyList<Dussh>> GetDusshesAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return Task.FromResult<IReadOnlyList<Dussh>>([]);

        return _eventStore.GetDusshesAsync(FolderPath, cancellationToken);
    }

    public async Task<Dussh> AddDusshRowAsync(CancellationToken cancellationToken = default)
    {
        var dussh = new Dussh();
        await _eventStore.AddDusshAsync(FolderPath, dussh, cancellationToken);
        return dussh;
    }

    public async Task<Dussh?> AddDusshAsync(string name, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return null;

        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return null;

        var dusshes = await _eventStore.GetDusshesAsync(FolderPath, cancellationToken);
        var dussh = dusshes.FirstOrDefault(d => TextKey.Same(d.Name, trimmed));
        if (dussh is null)
        {
            dussh = new Dussh { Name = trimmed };
            await _eventStore.AddDusshAsync(FolderPath, dussh, cancellationToken);
        }
        return dussh;
    }

    public async Task UpdateDusshAsync(Dussh dussh, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dussh);

        var name = (dussh.Name ?? string.Empty).Trim();
        var dusshes = await _eventStore.GetDusshesAsync(FolderPath, cancellationToken);
        var collides = name.Length > 0 && dusshes.Any(d =>
            d.Id != dussh.Id && TextKey.Same(d.Name.Trim(), name));
        if (collides || name.Length == 0)
            name = dusshes.FirstOrDefault(d => d.Id == dussh.Id)?.Name ?? name;

        await _eventStore.UpdateDusshAsync(FolderPath, new Dussh { Id = dussh.Id, Name = name }, cancellationToken);
    }

    public async Task DeleteDusshAsync(Guid dusshId, CancellationToken cancellationToken = default)
    {
        var folder = FolderPath;
        await _eventStore.ClearParticipantsDusshAsync(folder, dusshId, cancellationToken);
        await _eventStore.DeleteDusshAsync(folder, dusshId, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetDusshParticipantCountsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return new Dictionary<Guid, int>();

        var folder = FolderPath;
        var dusshes = await _eventStore.GetDusshesAsync(folder, cancellationToken);
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);

        var counts = dusshes.ToDictionary(d => d.Id, _ => 0);
        foreach (var p in participants)
        {
            if (p.DusshId is { } id && counts.ContainsKey(id))
                counts[id]++;
        }
        return counts;
    }

    public async Task SetParticipantDusshAsync(Guid participantId, Guid? dusshId, CancellationToken cancellationToken = default)
    {
        var folder = FolderPath;
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var participant = participants.FirstOrDefault(p => p.Id == participantId);
        if (participant is null)
            return;

        participant.DusshId = dusshId;
        await _eventStore.UpdateParticipantAsync(folder, participant, cancellationToken);
    }

    public async Task<IReadOnlyList<ParticipantDayRow>> GetParticipantDayRowsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || CurrentDay is null)
            return [];

        var folder = FolderPath;
        var links = await _eventStore.GetParticipantDaysAsync(folder, CurrentDayId, cancellationToken);
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var groups = await _eventStore.GetGroupsAsync(folder, cancellationToken);
        var regions = await _eventStore.GetRegionsAsync(folder, cancellationToken);
        var clubs = await _eventStore.GetClubsAsync(folder, cancellationToken);
        var dusshes = await _eventStore.GetDusshesAsync(folder, cancellationToken);
        var byParticipant = participants.ToDictionary(p => p.Id);
        var groupName = groups.ToDictionary(g => g.Id, g => g.Name);
        var regionName = regions.ToDictionary(r => r.Id, r => r.Name);
        var clubName = clubs.ToDictionary(c => c.Id, c => c.Name);
        var dusshName = dusshes.ToDictionary(d => d.Id, d => d.Name);

        // The fee total spans every day the participant runs, so gather their links across all days
        // (grouped by participant) — the day grid still shows the same competition-wide total.
        var fees = await LoadFeeContextAsync(folder, cancellationToken);
        var paymentPerDay = (await _eventStore.GetCompetitionInfoAsync(folder, cancellationToken))?.PaymentPerDay ?? false;
        var allLinks = await _eventStore.GetAllParticipantDaysAsync(folder, cancellationToken);
        var linksByParticipant = allLinks
            .GroupBy(l => l.ParticipantId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Keyed by participant-day link id.
        var results = await ComputeDayResultsAsync(folder, CurrentDay, cancellationToken);
        var fieldValues = await LoadFieldValuesAsync(folder, cancellationToken);
        var (relayPlaces, _) = await LoadRelayPlacementsAsync(folder, CurrentDay, links, cancellationToken);

        var rows = new List<ParticipantDayRow>(links.Count);
        foreach (var link in links)
        {
            // Defensive: skip a link whose participant was removed out from under it.
            if (!byParticipant.TryGetValue(link.ParticipantId, out var participant))
                continue;

            var all = linksByParticipant.TryGetValue(participant.Id, out var ls) ? ls : [link];
            var memberDays = all.Select(l => new EntryFeeDay(l.EventDayId, l.GroupId, l.Chip, l.PaysRaisedFee)).ToList();
            // The day grid lets a row recompute live; it needs the OTHER days' fixed contributions.
            var otherDays = all
                .Where(l => l.Id != link.Id)
                .Select(l => new ParticipantFeeDay(l.EventDayId, l.GroupId, l.Chip, l.PaysRaisedFee))
                .ToList();
            var result = results.TryGetValue(link.Id, out var r) ? r : ParticipantDayResult.Empty;
            // The grid shows one day, so its raised-fee checkbox reads THIS day's flag in per-day payment
            // mode and the competition-level one otherwise — matching where the edit is written back.
            rows.Add(ToRow(link, participant, groupName, regionName, clubName, dusshName,
                paymentPerDay ? link.PaysRaisedFee : participant.PaysRaisedFee,
                fees.SelectedDiscountIds(participant.Id),
                fees.Total(participant, memberDays), otherDays, result, paymentPerDay)
                with { Fields = FieldsOf(fieldValues, participant.Id), Relay = relayPlaces.GetValueOrDefault(link.Id) });
        }
        return rows;
    }

    public async Task<IReadOnlyList<ParticipantRosterRow>> GetParticipantRosterAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return [];

        var folder = FolderPath;
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var days = await _eventStore.GetDaysAsync(folder, cancellationToken);
        var links = await _eventStore.GetAllParticipantDaysAsync(folder, cancellationToken);
        var groups = await _eventStore.GetGroupsAsync(folder, cancellationToken);
        var regions = await _eventStore.GetRegionsAsync(folder, cancellationToken);
        var clubs = await _eventStore.GetClubsAsync(folder, cancellationToken);
        var dusshes = await _eventStore.GetDusshesAsync(folder, cancellationToken);
        var groupName = groups.ToDictionary(g => g.Id, g => g.Name);
        var regionName = regions.ToDictionary(r => r.Id, r => r.Name);
        var clubName = clubs.ToDictionary(c => c.Id, c => c.Name);
        var dusshName = dusshes.ToDictionary(d => d.Id, d => d.Name);

        var fees = await LoadFeeContextAsync(folder, cancellationToken);
        var rosterPaymentPerDay = (await _eventStore.GetCompetitionInfoAsync(folder, cancellationToken))?.PaymentPerDay ?? false;

        // Per day, keyed by link id — the roster spans all days.
        var resultsByDay = new Dictionary<Guid, IReadOnlyDictionary<Guid, ParticipantDayResult>>(days.Count);
        var appData = await LoadDayResultsAppDataAsync(withRanks: true, cancellationToken);
        foreach (var day in days)
            resultsByDay[day.Id] = await ComputeDayResultsAsync(folder, day, cancellationToken, appData: appData);

        // Each day's relay places and whether it runs a relay at all (the roster's «Естафета» cells).
        var relayByDay = new Dictionary<Guid, (Dictionary<Guid, RelayPlacement> ByLink, bool IsRelayDay)>(days.Count);
        foreach (var day in days)
            relayByDay[day.Id] = await LoadRelayPlacementsAsync(folder, day, links, cancellationToken);

        // Keyed for per-cell lookup.
        var linkByKey = links.ToDictionary(l => (l.ParticipantId, l.EventDayId));
        var fieldValues = await LoadFieldValuesAsync(folder, cancellationToken);

        var rows = new List<ParticipantRosterRow>(participants.Count);
        foreach (var participant in participants)
        {
            var cells = new List<RosterDayCell>(days.Count);
            var memberDays = new List<EntryFeeDay>();
            foreach (var day in days)
            {
                var relay = relayByDay[day.Id];
                if (linkByKey.TryGetValue((participant.Id, day.Id), out var link))
                {
                    var name = link.GroupId is { } gid && groupName.TryGetValue(gid, out var n) ? n : string.Empty;
                    var result = resultsByDay.TryGetValue(day.Id, out var dr) && dr.TryGetValue(link.Id, out var r)
                        ? r : ParticipantDayResult.Empty;
                    cells.Add(new RosterDayCell(day.Id, day.Number, link.Id, IsMember: true, link.GroupId, name, link.Chip, link.Payment, link.PaysRaisedFee, link.StartTime, link.OutOfCompetition, link.Bonus, result)
                    {
                        IsRelayDay = relay.IsRelayDay,
                        Relay = relay.ByLink.GetValueOrDefault(link.Id)
                    });
                    memberDays.Add(new EntryFeeDay(day.Id, link.GroupId, link.Chip, link.PaysRaisedFee));
                }
                else
                {
                    cells.Add(new RosterDayCell(day.Id, day.Number, LinkId: null, IsMember: false, GroupId: null, GroupName: string.Empty, Chip: string.Empty, Payment: string.Empty, PaysRaisedFee: false, StartTime: null, OutOfCompetition: false, Bonus: null, ParticipantDayResult.Empty)
                    {
                        IsRelayDay = relay.IsRelayDay
                    });
                }
            }
            var region = participant.RegionId is { } rid && regionName.TryGetValue(rid, out var rn) ? rn : string.Empty;
            var club = participant.ClubId is { } cid && clubName.TryGetValue(cid, out var cn) ? cn : string.Empty;
            var dussh = participant.DusshId is { } did && dusshName.TryGetValue(did, out var dn) ? dn : string.Empty;
            rows.Add(new ParticipantRosterRow(
                participant.Id,
                participant.FullName,
                participant.Number,
                participant.Rank,
                participant.Coach,
                participant.BirthDate,
                participant.RegionId,
                region,
                participant.ClubId,
                club,
                participant.DusshId,
                dussh,
                participant.Representative,
                participant.FsouCode,
                participant.IsFsouMember,
                participant.Payment,
                rosterPaymentPerDay,
                participant.Note,
                participant.Team,
                participant.PaysRaisedFee,
                fees.SelectedDiscountIds(participant.Id),
                fees.Total(participant, memberDays),
                cells) { Fields = FieldsOf(fieldValues, participant.Id), CheckedIn = participant.CheckedIn });
        }
        return rows;
    }

    public async Task<ParticipantDayRow?> AddParticipantToDayAsync(CancellationToken cancellationToken = default)
    {
        var folder = FolderPath;
        var dayId = CurrentDayId;
        await EnsureDayEditableAsync(dayId, cancellationToken);

        // A member always has a group; refuse to add when the day has no groups to assign.
        var dayGroups = await GetGroupsForDayAsync(dayId, cancellationToken);
        if (dayGroups.Count == 0)
            return null;
        var firstGroup = dayGroups[0];

        var participant = new Participant();
        await _eventStore.AddParticipantAsync(folder, participant, cancellationToken);

        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var nextOrder = links.Count == 0 ? 1 : links.Max(l => l.Order) + 1;
        var link = new ParticipantDay
        {
            EventDayId = dayId,
            ParticipantId = participant.Id,
            Order = nextOrder,
            GroupId = firstGroup.GroupId
        };
        await _eventStore.AddParticipantDayAsync(folder, link, cancellationToken);

        // A fresh participant has no region/club/ДЮСШ yet, so empty name maps are fine. The payment mode
        // still has to travel with the row, or the new row's «Оплата» cell would edit the wrong field
        // until the page is reloaded.
        var paymentPerDay = (await _eventStore.GetCompetitionInfoAsync(folder, cancellationToken))?.PaymentPerDay ?? false;
        return ToRow(link, participant,
            new Dictionary<Guid, string> { [firstGroup.GroupId] = firstGroup.Name },
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string>(),
            paymentPerDay: paymentPerDay);
    }

    public async Task<ParticipantRosterRow?> AddRosterParticipantAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return null;

        var folder = FolderPath;

        // The participant starts out "not participating" on every day: just create the identity row
        // with no day links. The user assigns days by picking a group in the roster's per-day columns.
        var participant = new Participant();
        await _eventStore.AddParticipantAsync(folder, participant, cancellationToken);

        var roster = await GetParticipantRosterAsync(cancellationToken);
        return roster.FirstOrDefault(r => r.ParticipantId == participant.Id);
    }

    public async Task UpdateParticipantDayRowAsync(ParticipantDayRow row, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        var folder = FolderPath;
        var dayId = CurrentDayId;
        await EnsureDayEditableAsync(dayId, cancellationToken);

        // Save the participant identity (affects every day). A number colliding with a different
        // participant is dropped, keeping the stored number — the row reverts on the next reload.
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var number = (row.Number ?? string.Empty).Trim();
        var numberCollides = number.Length > 0 && participants.Any(p =>
            p.Id != row.ParticipantId && TextKey.Same(p.Number.Trim(), number));
        if (numberCollides)
            number = participants.FirstOrDefault(p => p.Id == row.ParticipantId)?.Number ?? number;

        await _eventStore.UpdateParticipantAsync(folder, new Participant
        {
            Id = row.ParticipantId,
            FullName = (row.FullName ?? string.Empty).Trim(),
            Number = number,
            Rank = (row.Rank ?? string.Empty).Trim(),
            Coach = (row.Coach ?? string.Empty).Trim(),
            BirthDate = row.BirthDate,
            RegionId = row.RegionId,
            ClubId = row.ClubId,
            DusshId = row.DusshId,
            Representative = (row.Representative ?? string.Empty).Trim(),
            FsouCode = (row.FsouCode ?? string.Empty).Trim(),
            IsFsouMember = row.IsFsouMember,
            Payment = (row.Payment ?? string.Empty).Trim(),
            Note = (row.Note ?? string.Empty).Trim(),
            // Team is competition-level (shared across days), persisted with the participant identity.
            Team = (row.Team ?? string.Empty).Trim()
        }, cancellationToken);

        // Save the day link. A chip colliding with another participant on the same day is dropped,
        // keeping the stored chip — the row reverts on the next reload.
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var chip = (row.Chip ?? string.Empty).Trim();
        var chipCollides = chip.Length > 0 && links.Any(l =>
            l.Id != row.LinkId && TextKey.Same(l.Chip.Trim(), chip));
        if (chipCollides)
            chip = links.FirstOrDefault(l => l.Id == row.LinkId)?.Chip ?? chip;

        await _eventStore.UpdateParticipantDayAsync(folder, new ParticipantDay
        {
            Id = row.LinkId,
            EventDayId = dayId,
            ParticipantId = row.ParticipantId,
            Order = row.Order,
            GroupId = row.GroupId,
            Chip = chip,
            StartTime = row.StartTime,
            OutOfCompetition = row.OutOfCompetition
        }, cancellationToken);
    }

    public async Task<CopyParticipantsResult> CopyParticipantsBetweenDaysAsync(
        CopyParticipantsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_session.CurrentEvent is null)
            return new CopyParticipantsResult(0, 0, 0, 0);

        // Only the target day is written, so a closed source day may still be copied FROM.
        await EnsureDayEditableAsync(request.TargetDayId, cancellationToken);

        return await _eventStore.CopyParticipantsBetweenDaysAsync(FolderPath, request, cancellationToken);
    }

    public async Task RemoveParticipantFromDayAsync(Guid linkId, Guid participantId, CancellationToken cancellationToken = default)
    {
        var folder = FolderPath;
        // The lock that matters is the link's own day («Мандатка» edits any day, not the session's).
        var link = (await _eventStore.GetAllParticipantDaysAsync(folder, cancellationToken)).FirstOrDefault(l => l.Id == linkId);
        if (link is null)
            return;
        await EnsureDayEditableAsync(link.EventDayId, cancellationToken);

        await _eventStore.DeleteParticipantDayAsync(folder, linkId, cancellationToken);

        // A participant that no longer runs on any day is removed entirely.
        var remaining = await _eventStore.CountParticipantDaysForParticipantAsync(folder, participantId, cancellationToken);
        if (remaining == 0)
            await _eventStore.DeleteParticipantAsync(folder, participantId, cancellationToken);
    }

    public async Task DeleteParticipantAsync(Guid participantId, CancellationToken cancellationToken = default)
    {
        var folder = FolderPath;
        // Deleting the person deletes every day of theirs: none of them may be closed.
        foreach (var link in (await _eventStore.GetAllParticipantDaysAsync(folder, cancellationToken)).Where(l => l.ParticipantId == participantId))
            await EnsureDayEditableAsync(link.EventDayId, cancellationToken);
        await _eventStore.DeleteParticipantAsync(folder, participantId, cancellationToken);
    }

    public async Task<NumberConflict?> FindNumberConflictAsync(Guid participantId, string number, CancellationToken cancellationToken = default)
    {
        var trimmed = (number ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return null;
        var participants = await _eventStore.GetParticipantsAsync(FolderPath, cancellationToken);
        var holder = participants.FirstOrDefault(p => p.Id != participantId && TextKey.Same(p.Number.Trim(), trimmed));
        return holder is null
            ? null
            : new NumberConflict(holder.FullName.Trim(), participants.FirstOrDefault(p => p.Id == participantId)?.Number ?? string.Empty);
    }

    public Task<int> CountParticipantDaysAsync(Guid participantId, CancellationToken cancellationToken = default)
        => _eventStore.CountParticipantDaysForParticipantAsync(FolderPath, participantId, cancellationToken);

    // Joining a day and changing a group are the same operation here; the store does the look-up and the
    // insert in one transaction so two concurrent edits can't each create a link for the same (day, person).
    public async Task<Guid> SetParticipantDayGroupAsync(Guid participantId, Guid dayId, Guid? groupId, CancellationToken cancellationToken = default)
    {
        await EnsureDayEditableAsync(dayId, cancellationToken);
        return await _eventStore.SetParticipantDayGroupAsync(FolderPath, participantId, dayId, groupId, cancellationToken);
    }

    public async Task SetParticipantDayChipAsync(Guid participantId, Guid dayId, string chip, CancellationToken cancellationToken = default)
    {
        await EnsureDayEditableAsync(dayId, cancellationToken);
        var folder = FolderPath;
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var existing = links.FirstOrDefault(l => l.ParticipantId == participantId);

        // The chip is per-day and only meaningful for a member; nothing to do otherwise.
        if (existing is null)
            return;

        // A chip colliding with another participant on the same day is dropped, keeping the stored
        // chip — the cell reverts on the next reload (matches UpdateParticipantDayRowAsync).
        var trimmed = (chip ?? string.Empty).Trim();
        var collides = trimmed.Length > 0 && links.Any(l =>
            l.Id != existing.Id && TextKey.Same(l.Chip.Trim(), trimmed));
        if (collides)
            trimmed = existing.Chip;

        await _eventStore.UpdateParticipantDayAsync(folder, new ParticipantDay
        {
            Id = existing.Id,
            EventDayId = dayId,
            ParticipantId = participantId,
            Order = existing.Order,
            GroupId = existing.GroupId,
            Chip = trimmed,
            StartTime = existing.StartTime,
            OutOfCompetition = existing.OutOfCompetition
        }, cancellationToken);
    }

    public async Task<int> SetParticipantDayChipsBatchAsync(
        IReadOnlyList<(Guid ParticipantId, Guid DayId, string Chip)> assignments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignments);

        // The batch may span several days (the roster hands one chip out across days), so every day
        // it writes to must be open — a partly-applied batch would be worse than a refused one.
        foreach (var dayId in assignments.Select(a => a.DayId).Distinct())
            await EnsureDayEditableAsync(dayId, cancellationToken);

        return await _eventStore.SetParticipantDayChipsBatchAsync(FolderPath, assignments, cancellationToken);
    }

    public Task<int> SetParticipantNumbersBatchAsync(
        IReadOnlyList<(Guid ParticipantId, string Number)> assignments,
        CancellationToken cancellationToken = default)
        => _eventStore.SetParticipantNumbersBatchAsync(FolderPath, assignments, cancellationToken);

    public async Task SetParticipantDayStartTimeAsync(Guid participantId, Guid dayId, TimeSpan? startTime, CancellationToken cancellationToken = default)
    {
        await EnsureDayEditableAsync(dayId, cancellationToken);
        var folder = FolderPath;
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var existing = links.FirstOrDefault(l => l.ParticipantId == participantId);
        // Per-day, member-only — nothing to do for a non-member. No uniqueness rule, so set directly.
        if (existing is null)
            return;

        existing.StartTime = startTime;
        await _eventStore.UpdateParticipantDayAsync(folder, existing, cancellationToken);
    }

    public async Task SetParticipantDayOutOfCompetitionAsync(Guid participantId, Guid dayId, bool outOfCompetition, CancellationToken cancellationToken = default)
    {
        await EnsureDayEditableAsync(dayId, cancellationToken);
        var folder = FolderPath;
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var existing = links.FirstOrDefault(l => l.ParticipantId == participantId);
        if (existing is null)
            return;

        existing.OutOfCompetition = outOfCompetition;
        await _eventStore.UpdateParticipantDayAsync(folder, existing, cancellationToken);
    }

    public async Task SetParticipantDayResultStatusAsync(Guid participantId, Guid dayId, FinishStatus? status, CancellationToken cancellationToken = default)
    {
        await EnsureDayEditableAsync(dayId, cancellationToken);
        var folder = FolderPath;
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var existing = links.FirstOrDefault(l => l.ParticipantId == participantId);
        if (existing is null)
            return;

        // Resolve the participant's latest read-out for the day (the one the result uses), if any.
        var chip = existing.Chip.Trim();
        var readouts = chip.Length == 0
            ? Array.Empty<FinishReadout>() as IReadOnlyList<FinishReadout>
            : await _eventStore.GetFinishReadoutsAsync(folder, dayId, cancellationToken);
        var latest = readouts
            .Where(r => TextKey.Same(r.ChipNumber.Trim(), chip))
            .OrderByDescending(r => r.Order)
            .FirstOrDefault();

        // For a participant who has no finish read-out (no result), "OK" carries no meaning — it is the
        // all-clear for someone who actually ran — so picking it leaves the status blank: normalize it to
        // the "auto" sentinel (null) so the cell stays empty. A real problem status (DNS/DNF/…) is kept.
        if (latest is null && status == FinishStatus.Ok)
            status = null;

        // Persist only the override via its dedicated store writer. The debounced row save goes through
        // UpdateParticipantDayAsync, which deliberately leaves this column alone, so the two never fight —
        // and crucially the row save can't wipe a status set here.
        await _eventStore.SetParticipantDayResultStatusAsync(folder, existing.Id, status, cancellationToken);

        // Mirror the override onto that latest read-out so the finish-read log shows the same manual
        // status. Null clears it back to the computed status.
        if (latest is not null && latest.ManualStatus != status)
        {
            latest.ManualStatus = status;
            await _eventStore.UpdateFinishReadoutAsync(folder, latest, cancellationToken);
        }
    }

    public async Task SetParticipantDayBonusAsync(Guid participantId, Guid dayId, int? bonus, CancellationToken cancellationToken = default)
    {
        await EnsureDayEditableAsync(dayId, cancellationToken);
        var folder = FolderPath;
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var existing = links.FirstOrDefault(l => l.ParticipantId == participantId);
        if (existing is null)
            return;

        // Its own writer (UpdateParticipantDayAsync leaves the bonus column alone), so the debounced row
        // save can't wipe the correction set here. The recompute folds it into «Бали» (see ComputeDayResultsAsync).
        await _eventStore.SetParticipantDayBonusAsync(folder, existing.Id, bonus, cancellationToken);
    }

    public async Task SetParticipantDayPaymentAsync(Guid participantId, Guid dayId, string payment, CancellationToken cancellationToken = default)
    {
        await EnsureDayEditableAsync(dayId, cancellationToken);
        var folder = FolderPath;
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var existing = links.FirstOrDefault(l => l.ParticipantId == participantId);
        if (existing is null)
            return;

        // Its own writer, like the bonus — the debounced row save leaves this column alone.
        await _eventStore.SetParticipantDayPaymentAsync(folder, existing.Id, payment, cancellationToken);
    }

    public async Task SetParticipantDayRaisedFeeAsync(Guid participantId, Guid dayId, bool paysRaisedFee, CancellationToken cancellationToken = default)
    {
        await EnsureDayEditableAsync(dayId, cancellationToken);
        var folder = FolderPath;
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var existing = links.FirstOrDefault(l => l.ParticipantId == participantId);
        if (existing is null)
            return;

        // Its own writer, like the per-day payment — the debounced row save leaves this column alone.
        await _eventStore.SetParticipantDayRaisedFeeAsync(folder, existing.Id, paysRaisedFee, cancellationToken);
    }

    public async Task<int> CountParticipantsWithPaymentAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return 0;

        var folder = FolderPath;
        var info = await _eventStore.GetCompetitionInfoAsync(folder, cancellationToken);
        if (info?.PaymentPerDay == true)
        {
            var links = await _eventStore.GetAllParticipantDaysAsync(folder, cancellationToken);
            return links
                .Where(l => !string.IsNullOrWhiteSpace(l.Payment))
                .Select(l => l.ParticipantId)
                .Distinct()
                .Count();
        }

        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        return participants.Count(p => !string.IsNullOrWhiteSpace(p.Payment));
    }

    public async Task SetPaymentPerDayAsync(bool paymentPerDay, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return;

        var folder = FolderPath;
        var info = await _eventStore.GetCompetitionInfoAsync(folder, cancellationToken);
        if (info is null || info.PaymentPerDay == paymentPerDay)
            return;

        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var links = await _eventStore.GetAllParticipantDaysAsync(folder, cancellationToken);
        var linksByParticipant = links
            .GroupBy(l => l.ParticipantId)
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.Order).ToList());

        var participantPayments = new Dictionary<Guid, string>();
        var dayPayments = new Dictionary<Guid, string>();
        // The raised-fee flag moves with the payment mode, so switching back and forth never loses it:
        // OFF → ON copies the participant's one flag onto every day they run; ON → OFF flags the
        // participant when any of their days was flagged.
        var participantRaised = new Dictionary<Guid, bool>();
        var dayRaised = new Dictionary<Guid, bool>();

        if (paymentPerDay)
        {
            // OFF → ON: spread each single payment across the days the participant runs, in proportion to
            // each day's share of their fee. The fee context is the same one the tables compute with.
            var fees = await LoadFeeContextAsync(folder, cancellationToken);
            foreach (var participant in participants)
            {
                if (participant.PaysRaisedFee && linksByParticipant.TryGetValue(participant.Id, out var raisedLinks))
                    foreach (var link in raisedLinks)
                        dayRaised[link.Id] = true;

                var text = (participant.Payment ?? string.Empty).Trim();
                if (text.Length == 0)
                    continue;
                if (!linksByParticipant.TryGetValue(participant.Id, out var memberLinks) || memberLinks.Count == 0)
                    continue;

                if (!PaymentValue.TryParse(text, out var amount))
                {
                    // Not a number — the text can't be split, so it goes to the first day whole.
                    dayPayments[memberLinks[0].Id] = text;
                    continue;
                }

                var breakdown = fees.Describe(
                    participant,
                    memberLinks.Select(l => new EntryFeeDay(l.EventDayId, l.GroupId, l.Chip, l.PaysRaisedFee)));
                var shares = ProportionalShares(amount, memberLinks.Select(l => breakdown.DayTotal(l.EventDayId)).ToList());
                for (var i = 0; i < memberLinks.Count; i++)
                    dayPayments[memberLinks[i].Id] = PaymentValue.Format(shares[i]);
            }
        }
        else
        {
            // ON → OFF: add the per-day numbers back up into the one competition-level value. A per-day
            // value that isn't a number can't be summed, so the first such text wins for the whole row.
            foreach (var participant in participants)
            {
                if (!linksByParticipant.TryGetValue(participant.Id, out var memberLinks))
                    continue;

                if (memberLinks.Any(l => l.PaysRaisedFee))
                    participantRaised[participant.Id] = true;

                var sum = 0m;
                var any = false;
                string? text = null;
                foreach (var link in memberLinks)
                {
                    var value = (link.Payment ?? string.Empty).Trim();
                    if (value.Length == 0)
                        continue;
                    if (PaymentValue.TryParse(value, out var amount))
                    {
                        sum += amount;
                        any = true;
                    }
                    else
                    {
                        text ??= value;
                    }
                }

                if (text is not null)
                    participantPayments[participant.Id] = text;
                else if (any)
                    participantPayments[participant.Id] = PaymentValue.Format(sum);
            }
        }

        await _eventStore.SetPaymentPerDayAsync(
            folder, paymentPerDay, participantPayments, dayPayments, participantRaised, dayRaised, cancellationToken);
    }

    // Splits an amount across the given day weights, rounded to 0.01, with the last day absorbing the
    // remainder so the parts add back up to the amount exactly. All-zero weights split it equally.
    private static IReadOnlyList<decimal> ProportionalShares(decimal amount, IReadOnlyList<decimal> weights)
    {
        var shares = new decimal[weights.Count];
        if (weights.Count == 0)
            return shares;

        var totalWeight = weights.Sum();
        var running = 0m;
        for (var i = 0; i < weights.Count - 1; i++)
        {
            var share = totalWeight > 0m
                ? decimal.Round(amount * weights[i] / totalWeight, 2, MidpointRounding.AwayFromZero)
                : decimal.Round(amount / weights.Count, 2, MidpointRounding.AwayFromZero);
            shares[i] = share;
            running += share;
        }
        shares[^1] = amount - running;
        return shares;
    }

    public async Task<IReadOnlyDictionary<Guid, ParticipantDayResult>> GetDayResultsByParticipantAsync(Guid dayId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return new Dictionary<Guid, ParticipantDayResult>();

        var folder = FolderPath;
        var days = await _eventStore.GetDaysAsync(folder, cancellationToken);
        var day = days.FirstOrDefault(d => d.Id == dayId);
        if (day is null)
            return new Dictionary<Guid, ParticipantDayResult>();

        // ComputeDayResultsAsync keys by link id; re-key by participant id for the UI to re-apply by row.
        var byLink = await ComputeDayResultsAsync(folder, day, cancellationToken);
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var byParticipant = new Dictionary<Guid, ParticipantDayResult>(links.Count);
        foreach (var link in links)
            if (byLink.TryGetValue(link.Id, out var r))
                byParticipant[link.ParticipantId] = r;
        return byParticipant;
    }

    public async Task<ResultProtocolData> GetResultProtocolDataAsync(Guid dayId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return new ResultProtocolData([]);

        var folder = FolderPath;
        // One transactional read: every list below is from the same snapshot (see GetDaySnapshotAsync).
        var snapshot = await _eventStore.GetDaySnapshotAsync(folder, dayId, cancellationToken);
        var days = snapshot.Days;
        var day = days.FirstOrDefault(d => d.Id == dayId);
        if (day is null)
            return new ResultProtocolData([]);

        var links = DistinctLinks(snapshot.Links);
        var participants = snapshot.Participants;
        var groups = snapshot.Groups;
        var settings = snapshot.GroupDaySettings;
        var regions = snapshot.Regions;
        var clubs = snapshot.Clubs;
        var dusshes = snapshot.Dusshes;
        var controlPoints = snapshot.ControlPoints;
        var info = snapshot.Info;

        var byParticipant = participants.ToDictionary(p => p.Id);
        var groupName = groups.ToDictionary(g => g.Id, g => g.Name);
        var regionName = regions.ToDictionary(r => r.Id, r => r.Name);
        var clubName = clubs.ToDictionary(c => c.Id, c => c.Name);
        var dusshName = dusshes.ToDictionary(d => d.Id, d => d.Name);
        var settingsByGroup = settings.ToDictionary(s => s.GroupId);

        // Start/finish codes, so a group's control count counts only the running controls in its course. The
        // day's disabled («проблемні») controls are folded in too, so the printed count matches what was
        // actually required after the broken КП were dropped.
        var startFinishCodes = new HashSet<string>(
            controlPoints
                .Where(c => c.Type is ControlPointType.Start or ControlPointType.Finish)
                .Select(c => c.Code.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var countExcluded = new HashSet<string>(startFinishCodes, StringComparer.OrdinalIgnoreCase);
        countExcluded.UnionWith(DisabledCodesOf(controlPoints));

        var rankCalcs = new Dictionary<Guid, GroupRankCalculation>();
        var teamResults = new Dictionary<Guid, TeamDayResult>();
        var results = await ComputeDayResultsAsync(
            folder, day, cancellationToken, rankCalcs, snapshot, teamResults: teamResults);

        // Additional field values per participant (the «field.*» extra columns, §10.2).
        var fieldsByParticipant = snapshot.FieldValues
            .GroupBy(v => v.ParticipantId)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, string>)g
                .GroupBy(v => v.Key, StringComparer.Ordinal)
                .ToDictionary(v => v.Key, v => v.First().Value, StringComparer.Ordinal));

        // Bucket each member's row under its group; a member with no group is skipped (a protocol is by group).
        var rowsByGroup = new Dictionary<Guid, List<ResultProtocolRow>>();
        var rowByLink = new Dictionary<Guid, ResultProtocolRow>();
        foreach (var link in links)
        {
            if (link.GroupId is not { } gid || !byParticipant.TryGetValue(link.ParticipantId, out var p))
                continue;

            var region = p.RegionId is { } rid && regionName.TryGetValue(rid, out var rn) ? rn : string.Empty;
            var club = p.ClubId is { } cid && clubName.TryGetValue(cid, out var cn) ? cn : string.Empty;
            var dussh = p.DusshId is { } did && dusshName.TryGetValue(did, out var dn) ? dn : string.Empty;
            var result = results.TryGetValue(link.Id, out var r) ? r : ParticipantDayResult.Empty;

            if (!rowsByGroup.TryGetValue(gid, out var bucket))
            {
                bucket = [];
                rowsByGroup[gid] = bucket;
            }
            var protocolRow = new ResultProtocolRow(
                result.LegBib ?? p.Number, p.FullName, p.BirthDate, club, region, dussh, p.Coach, p.Rank,
                p.Team.Trim(), result)
            {
                Fields = fieldsByParticipant.TryGetValue(p.Id, out var fv) ? fv : new Dictionary<string, string>(),
                ParticipantId = p.Id,
            };
            bucket.Add(protocolRow);
            rowByLink[link.Id] = protocolRow;
        }

        // One section per group that runs on the day (a group with no members still appears, empty),
        // in the day grid order. A group's discipline (its override, else the day default) decides whether
        // the section is teamed (rogaine), so the builder groups members by team.
        var sections = new List<ResultProtocolGroup>(settings.Count);
        foreach (var s in settings.OrderBy(s => s.Order))
        {
            if (!groupName.TryGetValue(s.GroupId, out var name))
                continue;

            var discipline = s.DisciplineOverride ?? day.DefaultDiscipline;
            var strategy = _strategies.For(discipline);
            var isTeam = strategy.Competitors == CompetitorModel.Team;

            // Control count: blank order ⇒ unknown (null). For the «mixed» pattern the count is what the
            // pattern requires (via the strategy), not a flat token count; other disciplines count the
            // running controls minus the day's start/finish codes.
            var controlCount = string.IsNullOrWhiteSpace(s.CourseOrder)
                ? (int?)null
                : strategy.UsesCoursePattern
                    ? strategy.ControlCount(s.CourseOrder)
                    : CountCourseControls(s.CourseOrder, countExcluded);
            var rows = rowsByGroup.TryGetValue(s.GroupId, out var b) ? b : [];
            // Course-setter: the group's per-day override wins; else fall back to the competition default.
            var (setter, setterCat) = ResolveCourseSetter(s, info);
            rankCalcs.TryGetValue(s.GroupId, out var rankCalc);
            var relayTeams = strategy.Competitors == CompetitorModel.TeamWithLegs
                ? RelayProtocolTeams(snapshot, s, teamResults, rowByLink, clubName, regionName)
                : null;
            sections.Add(new ResultProtocolGroup(
                name, s.Order, s.DistanceKm, controlCount, s.TimeLimitSeconds, isTeam, rows, setter, setterCat,
                rankCalc, relayTeams)
            {
                PluginColumnKeys = ExtraResultColumn.PluginKeys(strategy),
            });
        }

        var fieldDefs = await _eventStore.GetCustomFieldDefsAsync(folder, cancellationToken);
        var disciplines = new[] { day.DefaultDiscipline }
            .Concat(settings.Select(s => s.DisciplineOverride ?? day.DefaultDiscipline));
        return new ResultProtocolData(sections, OfficialsFrom(info))
        {
            ExtraColumns = ExtraResultColumn.ForDay(_strategies, disciplines, fieldDefs.Select(CustomField.From))
        };
    }

    // The relay group's teams for the start protocols (Р-36): each team with leg 1's start — the runner's drawn
    // start, else the group's mass start (Р-27) — and its runner rows by leg.
    private static List<StartProtocolTeam> StartProtocolTeams(
        EventDaySnapshot snapshot, GroupDaySettings settings, IReadOnlyDictionary<Guid, StartProtocolRow> rowByLink,
        IReadOnlyDictionary<Guid, string> clubName, IReadOnlyDictionary<Guid, string> regionName)
    {
        var legCount = Math.Max(1, settings.LegCount ?? 1);
        var linksByTeam = snapshot.Links
            .Where(l => l.RelayTeamId is not null && l.Leg is not null)
            .GroupBy(l => l.RelayTeamId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var teams = new List<StartProtocolTeam>();
        foreach (var team in snapshot.RelayTeams.Where(t => t.GroupId == settings.GroupId))
        {
            var members = linksByTeam.TryGetValue(team.Id, out var ms) ? ms : [];
            var maxLeg = Math.Max(legCount, members.Select(m => m.Leg!.Value).DefaultIfEmpty(0).Max());
            var legs = new List<StartProtocolLeg>(maxLeg);
            for (var leg = 1; leg <= maxLeg; leg++)
            {
                var link = members.FirstOrDefault(m => m.Leg == leg);
                legs.Add(new StartProtocolLeg(leg,
                    link is not null && rowByLink.TryGetValue(link.Id, out var row) ? row : null));
            }
            var first = members.FirstOrDefault(m => m.Leg == 1);
            teams.Add(new StartProtocolTeam(
                team.Number,
                team.Name,
                team.ClubId is { } cid && clubName.TryGetValue(cid, out var cn) ? cn : string.Empty,
                team.RegionId is { } rid && regionName.TryGetValue(rid, out var rn) ? rn : string.Empty,
                first?.StartTime ?? settings.MassStartTime,
                legs));
        }
        return teams;
    }

    // The relay group's teams for the results protocol (Р-23): each team with its computed result and its runner
    // rows by leg (1…LegCount, plus any leg assigned beyond it); a leg nobody runs gets an empty entry.
    private static List<RelayProtocolTeam> RelayProtocolTeams(
        EventDaySnapshot snapshot, GroupDaySettings settings, IReadOnlyDictionary<Guid, TeamDayResult> teamResults,
        IReadOnlyDictionary<Guid, ResultProtocolRow> rowByLink,
        IReadOnlyDictionary<Guid, string> clubName, IReadOnlyDictionary<Guid, string> regionName)
    {
        var legCount = Math.Max(1, settings.LegCount ?? 1);
        var linksByTeam = snapshot.Links
            .Where(l => l.RelayTeamId is not null && l.Leg is not null)
            .GroupBy(l => l.RelayTeamId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var teams = new List<RelayProtocolTeam>();
        foreach (var team in snapshot.RelayTeams.Where(t => t.GroupId == settings.GroupId))
        {
            var members = linksByTeam.TryGetValue(team.Id, out var ms) ? ms : [];
            var maxLeg = Math.Max(legCount, members.Select(m => m.Leg!.Value).DefaultIfEmpty(0).Max());
            var legs = new List<RelayProtocolLeg>(maxLeg);
            for (var leg = 1; leg <= maxLeg; leg++)
            {
                var link = members.FirstOrDefault(m => m.Leg == leg);
                legs.Add(new RelayProtocolLeg(leg,
                    link is not null && rowByLink.TryGetValue(link.Id, out var row) ? row : null));
            }
            teams.Add(new RelayProtocolTeam(
                team.Number,
                team.Name,
                team.ClubId is { } cid && clubName.TryGetValue(cid, out var cn) ? cn : string.Empty,
                team.RegionId is { } rid && regionName.TryGetValue(rid, out var rn) ? rn : string.Empty,
                teamResults.TryGetValue(team.Id, out var tr) ? tr : null,
                legs));
        }
        return teams;
    }

    // Defensive guard for the day's participant links: keeps the FIRST link per participant, dropping any
    // repeat. A participant can only run a day once, so a second link is always bad data — either a legacy
    // duplicate row written before the unique index existed, or (historically) rows mixed from two different
    // read snapshots. Without this a duplicate silently printed the runner twice on the protocol.
    private static List<ParticipantDay> DistinctLinks(IReadOnlyList<ParticipantDay> links)
    {
        var seen = new HashSet<Guid>(links.Count);
        var result = new List<ParticipantDay>(links.Count);
        foreach (var link in links)
            if (seen.Add(link.ParticipantId))
                result.Add(link);
        return result;
    }

    // The effective course-setter for a group on a day: the group's own override (when its name is non-blank),
    // else the competition-wide default. Returns (name, category).
    private static (string Name, string Category) ResolveCourseSetter(GroupDaySettings s, CompetitionInfo? info)
    {
        if (!string.IsNullOrWhiteSpace(s.CourseSetter))
            return (s.CourseSetter.Trim(), (s.CourseSetterCategory ?? string.Empty).Trim());
        return (
            (info?.CourseSetter ?? string.Empty).Trim(),
            (info?.CourseSetterCategory ?? string.Empty).Trim());
    }

    // The competition's chief judge / secretary / jury, as the raw officials the protocol builders fold into
    // the trailing signature block.
    private static ProtocolOfficialsData OfficialsFrom(CompetitionInfo? info) => info is null
        ? ProtocolOfficialsData.None
        : new ProtocolOfficialsData(
            info.ChiefJudge, info.ChiefJudgeCategory,
            info.ChiefSecretary, info.ChiefSecretaryCategory,
            info.Jury);

    public async Task<ResultProtocolSettings?> GetResultProtocolSettingsAsync(Guid dayId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return null;

        var json = await _eventStore.GetResultProtocolJsonAsync(FolderPath, dayId, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            return null; // the caller seeds from the app-level default for a day with no template yet

        try
        {
            var settings = System.Text.Json.JsonSerializer.Deserialize<ResultProtocolSettings>(json);
            if (settings is null)
                return null;
            // A column list that lost its way (empty after a bad round-trip) falls back to the default set.
            if (settings.Columns.Count == 0)
                settings.Columns = ResultProtocolSettings.DefaultColumns();
            return settings;
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // corrupt JSON ⇒ treat as "no template" and seed from the default
        }
    }

    public Task SaveResultProtocolSettingsAsync(Guid dayId, ResultProtocolSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_session.CurrentEvent is null)
            return Task.CompletedTask;

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        return _eventStore.SaveResultProtocolJsonAsync(FolderPath, dayId, json, cancellationToken);
    }

    public async Task<StatementData> GetStatementDataAsync(
        Guid? dayId, IReadOnlyList<Guid> participantIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(participantIds);
        if (_session.CurrentEvent is null || participantIds.Count == 0)
            return StatementData.Empty;

        var folder = FolderPath;
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var days = await _eventStore.GetDaysAsync(folder, cancellationToken);
        var groups = await _eventStore.GetGroupsAsync(folder, cancellationToken);
        var regions = await _eventStore.GetRegionsAsync(folder, cancellationToken);
        var clubs = await _eventStore.GetClubsAsync(folder, cancellationToken);
        var dusshes = await _eventStore.GetDusshesAsync(folder, cancellationToken);
        var rentalChips = await _eventStore.GetRentalChipsAsync(folder, cancellationToken);
        var fieldDefs = await _eventStore.GetCustomFieldDefsAsync(folder, cancellationToken);
        var fieldValues = await LoadFieldValuesAsync(folder, cancellationToken);

        var byParticipant = participants.ToDictionary(p => p.Id);
        var groupName = groups.ToDictionary(g => g.Id, g => g.Name);
        var regionName = regions.ToDictionary(r => r.Id, r => r.Name);
        var clubName = clubs.ToDictionary(c => c.Id, c => c.Name);
        var dusshName = dusshes.ToDictionary(d => d.Id, d => d.Name);
        // The rental-chip pool as trimmed numbers, so a chip counts as "own" (bold) only when it's NOT rented.
        var rentalNumbers = new HashSet<string>(
            rentalChips.Select(c => c.Number.Trim()).Where(n => n.Length > 0), TextKey.Comparer);

        // The per-day membership links for the requested scope: one day (day mode) or every day (roster mode).
        // In roster mode the distinct across-days group/chip values are joined with " / ", matching the roster.
        var links = dayId is { } id
            ? await _eventStore.GetParticipantDaysAsync(folder, id, cancellationToken)
            : await _eventStore.GetAllParticipantDaysAsync(folder, cancellationToken);

        // Index the links by participant so each row gathers its own membership(s), preserving day order.
        var dayOrder = days.OrderBy(d => d.Number).Select(d => d.Id).ToList();
        var linksByParticipant = links
            .GroupBy(l => l.ParticipantId)
            .ToDictionary(g => g.Key, g => g.OrderBy(l => dayOrder.IndexOf(l.EventDayId)).ToList());

        // The days in the «Старт» block, in day order: just the scoped day (day mode) or every day (roster mode).
        // Each row's StartTimes list is aligned to this so column i = day scopeDays[i]. Labels are "Д{number}".
        var orderedDays = days.OrderBy(d => d.Number).ToList();
        var scopeDays = dayId is { } scopedId
            ? orderedDays.Where(d => d.Id == scopedId).ToList()
            : orderedDays;
        var dayLabels = scopeDays
            .Select(d => string.Format(CultureInfo.InvariantCulture, "Д{0}", d.Number))
            .ToList();

        var rows = new List<StatementRow>(participantIds.Count);
        foreach (var pid in participantIds)
        {
            if (!byParticipant.TryGetValue(pid, out var p))
                continue;

            var region = p.RegionId is { } rid && regionName.TryGetValue(rid, out var rn) ? rn : string.Empty;
            var club = p.ClubId is { } cid && clubName.TryGetValue(cid, out var cn) ? cn : string.Empty;
            var dussh = p.DusshId is { } did && dusshName.TryGetValue(did, out var dn) ? dn : string.Empty;

            // Gather the per-day group/chip values (already in day order); distinct, non-blank, " / "-joined.
            var groupValues = new List<string>();
            var chipValues = new List<string>();
            // The per-day start time, keyed by day id, so the «Старт» block can align to scopeDays below.
            var startByDay = new Dictionary<Guid, string>();
            if (linksByParticipant.TryGetValue(pid, out var plinks))
            {
                foreach (var link in plinks)
                {
                    var gname = link.GroupId is { } gid && groupName.TryGetValue(gid, out var gn) ? gn : string.Empty;
                    if (gname.Length > 0)
                        groupValues.Add(gname);
                    var chip = link.Chip.Trim();
                    if (chip.Length > 0)
                        chipValues.Add(chip);
                    if (link.StartTime is { } st)
                        startByDay[link.EventDayId] = st.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
                }
            }

            // One start-time cell per day in the block, in scope-day order; blank when the participant doesn't
            // run that day or has no start time set.
            var startTimes = scopeDays
                .Select(d => startByDay.TryGetValue(d.Id, out var s) ? s : string.Empty)
                .ToList();

            var distinctChips = chipValues.Distinct(TextKey.Comparer).ToList();
            // Own (bold) when the participant holds any chip that is NOT in the rental pool. Sort key = the
            // smallest chip number they hold (numeric so "9" < "80"; a non-numeric chip sorts just before the
            // chip-less rows). Null when they hold no chip at all.
            var hasOwnChip = distinctChips.Any(c => !rentalNumbers.Contains(c));
            int? chipSortKey = distinctChips.Count == 0
                ? null
                : distinctChips
                    .Select(c => int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue - 1)
                    .Min();

            rows.Add(new StatementRow
            {
                ParticipantId = p.Id,
                Number = p.Number,
                FullName = p.FullName,
                BirthDate = p.BirthDate,
                Group = string.Join(" / ", groupValues.Distinct(TextKey.Comparer)),
                Chip = string.Join(" / ", distinctChips),
                StartTimes = startTimes,
                Region = region,
                Club = club,
                Dussh = dussh,
                Coach = p.Coach,
                Rank = p.Rank,
                Team = p.Team.Trim(),
                Representative = p.Representative,
                FsouCode = p.FsouCode,
                Note = p.Note,
                Fields = FieldsOf(fieldValues, p.Id),
                HasChip = distinctChips.Count > 0,
                HasOwnChip = hasOwnChip,
                ChipSortKey = chipSortKey
            });
        }

        return new StatementData(rows, dayLabels)
        {
            Fields = fieldDefs.Select(f => new KeyValuePair<string, string>(f.Key, f.Label)).ToList()
        };
    }

    public async Task<StatementSettings?> GetStatementSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return null;

        var json = await _eventStore.GetStatementJsonAsync(FolderPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            return null; // the caller seeds from the app-level default when the competition has no template yet

        try
        {
            var settings = System.Text.Json.JsonSerializer.Deserialize<StatementSettings>(json);
            if (settings is null)
                return null;
            if (settings.Columns.Count == 0)
                settings.Columns = StatementSettings.DefaultColumns();
            return settings;
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // corrupt JSON ⇒ treat as "no template" and seed from the default
        }
    }

    public Task SaveStatementSettingsAsync(StatementSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_session.CurrentEvent is null)
            return Task.CompletedTask;

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        return _eventStore.SaveStatementJsonAsync(FolderPath, json, cancellationToken);
    }

    public async Task<ChipScanSettings?> GetChipScanSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return null;

        var json = await _eventStore.GetChipScanJsonAsync(FolderPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<ChipScanSettings>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // a broken row reads as "never set up"; the dialog starts from its defaults
        }
    }

    public Task SaveChipScanSettingsAsync(ChipScanSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_session.CurrentEvent is null)
            return Task.CompletedTask;

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        return _eventStore.SaveChipScanJsonAsync(FolderPath, json, cancellationToken);
    }

    public async Task<SummaryProtocolData> GetSummaryProtocolDataAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return SummaryProtocolData.Empty;

        var folder = FolderPath;
        var days = await _eventStore.GetDaysAsync(folder, cancellationToken);
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var links = await _eventStore.GetAllParticipantDaysAsync(folder, cancellationToken);
        var groups = await _eventStore.GetGroupsAsync(folder, cancellationToken);
        var regions = await _eventStore.GetRegionsAsync(folder, cancellationToken);
        var clubs = await _eventStore.GetClubsAsync(folder, cancellationToken);
        var dusshes = await _eventStore.GetDusshesAsync(folder, cancellationToken);
        var info = await _eventStore.GetCompetitionInfoAsync(folder, cancellationToken);

        var byParticipant = participants.ToDictionary(p => p.Id);
        var groupName = groups.ToDictionary(g => g.Id, g => g.Name);
        var regionName = regions.ToDictionary(r => r.Id, r => r.Name);
        var clubName = clubs.ToDictionary(c => c.Id, c => c.Name);
        var dusshName = dusshes.ToDictionary(d => d.Id, d => d.Name);

        // Computed run results per day, keyed by participant-day link id (the summary spans all days).
        var resultsByDay = new Dictionary<Guid, IReadOnlyDictionary<Guid, ParticipantDayResult>>(days.Count);
        var appData = await LoadDayResultsAppDataAsync(withRanks: true, cancellationToken);
        foreach (var day in days)
            resultsByDay[day.Id] = await ComputeDayResultsAsync(folder, day, cancellationToken, appData: appData);

        // One summary entry per (participant, group): a participant who ran different groups on different days
        // is summed separately in each group, over only the days they ran in it. The per-day result is keyed by
        // day id. A link with no group is skipped. Look-alike duplicates («M12» / «М12») count as one group.
        var orderedDays = days.OrderBy(d => d.Number).ToList();
        var canonical = CanonicalGroups(groups);
        var linksByParticipantGroup = links
            .Where(l => l.GroupId is not null)
            .GroupBy(l => (l.ParticipantId, GroupId: CanonicalGroupId(canonical, l.GroupId!.Value)));

        // Group order: follow the group entity order (mirrors the day grid). Build a per-group member list.
        var groupOrder = groups.Select((g, i) => (g.Id, i)).ToDictionary(t => t.Id, t => t.i);
        var membersByGroup = new Dictionary<Guid, List<SummaryProtocolParticipant>>();

        foreach (var pg in linksByParticipantGroup)
        {
            if (!byParticipant.TryGetValue(pg.Key.ParticipantId, out var p))
                continue;
            var gid = pg.Key.GroupId;

            var perDay = new Dictionary<Guid, ParticipantDayResult>(pg.Count());
            foreach (var link in pg)
            {
                if (resultsByDay.TryGetValue(link.EventDayId, out var dr) && dr.TryGetValue(link.Id, out var r))
                    perDay[link.EventDayId] = r;
            }

            var region = p.RegionId is { } rid && regionName.TryGetValue(rid, out var rn) ? rn : string.Empty;
            var club = p.ClubId is { } cid && clubName.TryGetValue(cid, out var cn) ? cn : string.Empty;
            var dussh = p.DusshId is { } did && dusshName.TryGetValue(did, out var dn) ? dn : string.Empty;

            if (!membersByGroup.TryGetValue(gid, out var bucket))
            {
                bucket = [];
                membersByGroup[gid] = bucket;
            }
            bucket.Add(new SummaryProtocolParticipant(
                p.Id, p.Number, p.FullName, p.BirthDate, club, region,
                dussh, p.Coach?.Trim() ?? string.Empty, p.Rank?.Trim() ?? string.Empty, perDay));
        }

        var summaryGroups = new List<SummaryProtocolGroup>(membersByGroup.Count);
        foreach (var (gid, members) in membersByGroup)
        {
            if (!groupName.TryGetValue(gid, out var name))
                continue;
            var order = groupOrder.TryGetValue(gid, out var o) ? o : int.MaxValue;
            summaryGroups.Add(new SummaryProtocolGroup(gid, name, order, members));
        }

        var summaryDays = orderedDays
            .Select(d => new SummaryProtocolDay(d.Id, d.Number, d.Date))
            .ToList();

        return new SummaryProtocolData(summaryDays, summaryGroups, OfficialsFrom(info));
    }

    public async Task<SummaryProtocolSettings?> GetSummaryProtocolSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return null;

        var json = await _eventStore.GetSummaryProtocolJsonAsync(FolderPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<SummaryProtocolSettings>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public Task SaveSummaryProtocolSettingsAsync(SummaryProtocolSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_session.CurrentEvent is null)
            return Task.CompletedTask;

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        return _eventStore.SaveSummaryProtocolJsonAsync(FolderPath, json, cancellationToken);
    }

    public async Task<StartProtocolData> GetStartProtocolDataAsync(Guid dayId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return new StartProtocolData([]);

        var folder = FolderPath;
        // One transactional read: every list below is from the same snapshot (see GetDaySnapshotAsync).
        var snapshot = await _eventStore.GetDaySnapshotAsync(folder, dayId, cancellationToken);
        var links = DistinctLinks(snapshot.Links);
        var participants = snapshot.Participants;
        var groups = snapshot.Groups;
        var settings = snapshot.GroupDaySettings;
        var regions = snapshot.Regions;
        var clubs = snapshot.Clubs;
        var dusshes = snapshot.Dusshes;
        var info = snapshot.Info;

        var byParticipant = participants.ToDictionary(p => p.Id);
        var groupName = groups.ToDictionary(g => g.Id, g => g.Name);
        var regionName = regions.ToDictionary(r => r.Id, r => r.Name);
        var clubName = clubs.ToDictionary(c => c.Id, c => c.Name);
        var dusshName = dusshes.ToDictionary(d => d.Id, d => d.Name);
        var day = snapshot.Days.FirstOrDefault(d => d.Id == dayId);
        if (day is null)
            return new StartProtocolData([]);

        // Bucket each member's row under its group; a member with no group is skipped (a start protocol is
        // built per group / per minute, and a runner without a group has no section to land in). A relay runner
        // (on a team) is not a unit of their own — their row goes under the team (Р-36).
        var rowsByGroup = new Dictionary<Guid, List<StartProtocolRow>>();
        var relayRowByLink = new Dictionary<Guid, StartProtocolRow>();
        var teamNumberById = snapshot.RelayTeams.ToDictionary(t => t.Id, t => t.Number);
        foreach (var link in links)
        {
            if (link.GroupId is not { } gid || !byParticipant.TryGetValue(link.ParticipantId, out var p))
                continue;

            var region = p.RegionId is { } rid && regionName.TryGetValue(rid, out var rn) ? rn : string.Empty;
            var club = p.ClubId is { } cid && clubName.TryGetValue(cid, out var cn) ? cn : string.Empty;
            var dussh = p.DusshId is { } did && dusshName.TryGetValue(did, out var dn) ? dn : string.Empty;
            var gname = groupName.TryGetValue(gid, out var gn) ? gn : string.Empty;

            if (!rowsByGroup.TryGetValue(gid, out var bucket))
            {
                bucket = [];
                rowsByGroup[gid] = bucket;
            }
            if (link.RelayTeamId is { } tid && link.Leg is { } leg && teamNumberById.TryGetValue(tid, out var teamNo))
            {
                relayRowByLink[link.Id] = new StartProtocolRow(
                    link.StartTime, RelayTeamNumbers.Bib(teamNo, leg), p.FullName, p.BirthDate, club, region, dussh,
                    p.Coach, p.Rank, link.Chip.Trim(), gname, p.Team.Trim());
                continue;
            }
            bucket.Add(new StartProtocolRow(
                link.StartTime, p.Number, p.FullName, p.BirthDate, club, region, dussh, p.Coach, p.Rank,
                link.Chip.Trim(), gname, p.Team.Trim()));
        }

        // One section per group that runs on the day (in the day grid order), even when empty.
        var sections = new List<StartProtocolGroup>(settings.Count);
        foreach (var s in settings.OrderBy(s => s.Order))
        {
            if (!groupName.TryGetValue(s.GroupId, out var name))
                continue;
            var rows = rowsByGroup.TryGetValue(s.GroupId, out var b) ? b : [];
            var (setter, setterCat) = ResolveCourseSetter(s, info);
            var strategy = _strategies.For(s.DisciplineOverride ?? day.DefaultDiscipline);
            var relayTeams = strategy.Competitors == CompetitorModel.TeamWithLegs
                ? StartProtocolTeams(snapshot, s, relayRowByLink, clubName, regionName)
                : null;
            sections.Add(new StartProtocolGroup(name, s.Order, rows, setter, setterCat, relayTeams));
        }

        return new StartProtocolData(sections, OfficialsFrom(info));
    }

    public async Task<StartProtocolSettings?> GetStartProtocolSettingsAsync(Guid dayId, StartProtocolKind kind, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return null;

        var json = await _eventStore.GetStartProtocolJsonAsync(FolderPath, dayId, kind, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            return null; // the caller seeds from the kind's default for a (day, kind) with no template yet

        try
        {
            var settings = System.Text.Json.JsonSerializer.Deserialize<StartProtocolSettings>(json);
            if (settings is null)
                return null;
            if (settings.Columns.Count == 0)
                settings.Columns = StartProtocolSettings.Default(kind).Columns;
            return settings;
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // corrupt JSON ⇒ treat as "no template" and seed from the default
        }
    }

    public Task SaveStartProtocolSettingsAsync(Guid dayId, StartProtocolKind kind, StartProtocolSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_session.CurrentEvent is null)
            return Task.CompletedTask;

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        return _eventStore.SaveStartProtocolJsonAsync(FolderPath, dayId, kind, json, cancellationToken);
    }

    public async Task<DrawSettings?> GetDrawSettingsAsync(Guid dayId, DrawSettingsKind kind, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return null;

        var json = await _eventStore.GetDrawSettingsJsonAsync(FolderPath, dayId, kind, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            return null; // nothing stored for this (day, kind) yet — the page keeps its defaults

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<DrawSettings>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // corrupt JSON ⇒ treat as "nothing stored"
        }
    }

    public Task SaveDrawSettingsAsync(Guid dayId, DrawSettingsKind kind, DrawSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_session.CurrentEvent is null)
            return Task.CompletedTask;

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        return _eventStore.SaveDrawSettingsJsonAsync(FolderPath, dayId, kind, json, cancellationToken);
    }

    public async Task<SplitExportData> GetDaySplitsExportDataAsync(Guid dayId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return new SplitExportData([]);

        var folder = FolderPath;
        var days = await _eventStore.GetDaysAsync(folder, cancellationToken);
        var day = days.FirstOrDefault(d => d.Id == dayId);
        if (day is null)
            return new SplitExportData([]);

        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var groups = await _eventStore.GetGroupsAsync(folder, cancellationToken);
        var settings = await _eventStore.GetGroupDaySettingsAsync(folder, dayId, cancellationToken);
        var controlPoints = await _eventStore.GetControlPointsAsync(folder, dayId, cancellationToken);
        var readouts = await _eventStore.GetFinishReadoutsAsync(folder, dayId, cancellationToken);

        var byParticipant = participants.ToDictionary(p => p.Id);
        var teamByParticipant = participants.ToDictionary(p => p.Id, p => p.Team.Trim());
        var groupName = groups.ToDictionary(g => g.Id, g => g.Name);
        var settingsByGroup = settings.ToDictionary(s => s.GroupId);
        var dayDefault = day.DefaultDiscipline;

        // Shared per-day geometry/points/codes used to build every member's splits (mirrors the inputs the
        // single-readout GetFinishSplitsAsync assembles, but loaded once for the whole day).
        var inputs = BuildDaySplitInputs(controlPoints);
        var variantsByGroup = await LoadCourseVariantsAsync(
            folder, dayId, settingsByGroup, dayDefault, inputs.StartFinishCodes, inputs.DisabledCodes, cancellationToken);

        // Latest read-out per chip (the "last read-out" rule), so a re-read chip uses its newest punches.
        var latestByChip = new Dictionary<string, FinishReadout>(TextKey.Comparer);
        foreach (var r in readouts)
        {
            var key = r.ChipNumber.Trim();
            if (key.Length == 0)
                continue;
            if (!latestByChip.TryGetValue(key, out var cur) || r.Order > cur.Order)
                latestByChip[key] = r;
        }

        // The per-link results (status/time/score/place) — reused from the same pipeline the tables use.
        var results = await ComputeDayResultsAsync(folder, day, cancellationToken);

        // Rogaine team-common controls, computed once per (group, team): a control counts for the team only
        // when every member punched it. Members of a team share this set, so cache it by team key.
        var teamCommonCache = new Dictionary<(Guid GroupId, string Team), IReadOnlySet<string>?>();

        var rowsByGroup = new Dictionary<Guid, List<SplitExportDataRow>>();
        foreach (var link in links)
        {
            if (link.GroupId is not { } gid || !byParticipant.TryGetValue(link.ParticipantId, out var p))
                continue;

            var chip = link.Chip.Trim();
            if (chip.Length == 0 || !latestByChip.TryGetValue(chip, out var readout))
                continue; // no read-out ⇒ nothing to show in a splits export

            GroupDaySettings? gs = settingsByGroup.TryGetValue(gid, out var s) ? s : null;
            var discipline = gs?.DisciplineOverride ?? dayDefault;
            var strategy = _strategies.For(discipline);

            IReadOnlySet<string>? teamCommon = null;
            if (strategy.Competitors == CompetitorModel.Team)
            {
                var team = teamByParticipant.TryGetValue(link.ParticipantId, out var t) ? t : string.Empty;
                var cacheKey = (gid, team);
                if (!teamCommonCache.TryGetValue(cacheKey, out teamCommon))
                {
                    teamCommon = team.Length == 0
                        ? null
                        : ComputeTeamCommonControls(
                            links, gid, team, teamByParticipant, settingsByGroup, latestByChip,
                            inputs.StartFinishCodes, inputs.DisabledCodes);
                    teamCommonCache[cacheKey] = teamCommon;
                }
            }

            var scatterVariants = variantsByGroup.TryGetValue(gid, out var gv) ? gv : NoCourseVariants;
            var result = results.TryGetValue(link.Id, out var rr) ? rr : ParticipantDayResult.Empty;
            var splits = BuildSplitsForLink(inputs, gs, discipline, link, readout, teamCommon, scatterVariants, result);

            if (!rowsByGroup.TryGetValue(gid, out var bucket))
            {
                bucket = [];
                rowsByGroup[gid] = bucket;
            }
            // A relay runner is shown by their leg number «100.2» and their team's name (Р-23).
            var number = result.LegBib ?? p.Number;
            var teamName = result.Team is { } relayTeam ? relayTeam.Name : p.Team.Trim();
            bucket.Add(new SplitExportDataRow(number, p.FullName, teamName, result, splits));
        }

        // One section per group that runs on the day, in the day grid order. The layout is the discipline's:
        // a set course shows the ordered table; rogaine (scored) shows the per-runner passage.
        var startFinishCodes = inputs.StartFinishCodes;
        var disabledCodes = inputs.DisabledCodes;
        var sections = new List<SplitExportDataGroup>(settings.Count);
        foreach (var setting in settings.OrderBy(s => s.Order))
        {
            if (!groupName.TryGetValue(setting.GroupId, out var name))
                continue;
            if (!rowsByGroup.TryGetValue(setting.GroupId, out var rows) || rows.Count == 0)
                continue; // a group with no read-out members has no splits to export

            var discipline = setting.DisciplineOverride ?? dayDefault;
            var strategy = _strategies.For(discipline);
            var isScored = strategy.UsesControlPointPoints;
            // Scatter («розсіювання») has no single prescribed column set — every runner runs a different order —
            // so it uses the per-runner passage shape (the same rogaine-style «Scored» table, without points),
            // where each runner writes their own passing order into positional КП columns.
            var isScatter = strategy.UsesCourseVariants;
            // A relay group whose legs run named courses (Р-44) has no single column set either.
            var legCourses = strategy.Competitors == CompetitorModel.TeamWithLegs
                && rows.Any(r => r.Result.Leg?.CourseCode is not null);
            var layout = isScored || isScatter || legCourses ? SplitsLayout.Scored : SplitsLayout.Ordered;

            // Disabled («проблемні») controls are dropped from the export's column header / count too, so the
            // table matches what was actually required and scored.
            var controls = CourseControls(setting.CourseOrder, startFinishCodes, disabledCodes);

            sections.Add(new SplitExportDataGroup(
                name, setting.Order, layout, setting.DistanceKm,
                controls.Count, isScored, controls, rows));
        }

        return new SplitExportData(sections);
    }

    /// <summary>Per-day geometry/points/codes shared across every member's splits build.</summary>
    private readonly record struct DaySplitInputs(
        HashSet<string> StartFinishCodes,
        HashSet<string> DisabledCodes,
        Dictionary<string, int> PointsByCode,
        Dictionary<string, GeoPoint> CoordsByCode,
        Dictionary<string, MapPoint> MapByCode,
        int? MapScale,
        GeoPoint StartCoord,
        GeoPoint FinishCoord,
        MapPoint StartMap,
        MapPoint FinishMap);

    // Assembles the per-day inputs once (the same values GetFinishSplitsAsync builds for a single readout).
    private static DaySplitInputs BuildDaySplitInputs(IReadOnlyList<ControlPoint> controlPoints)
    {
        var startFinishCodes = new HashSet<string>(
            controlPoints
                .Where(c => c.Type is ControlPointType.Start or ControlPointType.Finish)
                .Select(c => c.Code.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var disabledCodes = DisabledCodesOf(controlPoints);

        var pointsByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var coordsByCode = new Dictionary<string, GeoPoint>(StringComparer.OrdinalIgnoreCase);
        var mapByCode = new Dictionary<string, MapPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var cp in controlPoints)
        {
            var code = cp.Code.Trim();
            if (cp.Points is { } pts)
                pointsByCode[code] = pts;
            coordsByCode[code] = new GeoPoint(cp.Latitude, cp.Longitude);
            mapByCode[code] = new MapPoint(cp.MapX, cp.MapY);
        }
        var mapScale = controlPoints.Select(c => c.MapScale).FirstOrDefault(s => s is > 0);

        var startCp = controlPoints.FirstOrDefault(c => c.Type is ControlPointType.Start);
        var finishCp = controlPoints.FirstOrDefault(c => c.Type is ControlPointType.Finish);

        return new DaySplitInputs(
            startFinishCodes, disabledCodes, pointsByCode, coordsByCode, mapByCode, mapScale,
            StartCoord: startCp is null ? default : new GeoPoint(startCp.Latitude, startCp.Longitude),
            FinishCoord: finishCp is null ? default : new GeoPoint(finishCp.Latitude, finishCp.Longitude),
            StartMap: startCp is null ? default : new MapPoint(startCp.MapX, startCp.MapY),
            FinishMap: finishCp is null ? default : new MapPoint(finishCp.MapX, finishCp.MapY));
    }

    // Builds one member's SplitsView from the pre-loaded day inputs and their group/readout (the same context
    // GetFinishSplitsAsync assembles for a single readout, factored to run per member without re-querying).
    private SplitsView BuildSplitsForLink(
        DaySplitInputs inputs, GroupDaySettings? gs, DisciplineId discipline,
        ParticipantDay link, FinishReadout readout, IReadOnlySet<string>? teamCommon,
        IReadOnlyList<CourseVariantData> scatterVariants, ParticipantDayResult? result = null)
    {
        // §11.4: a relay leg is judged against its own named course (Р-44), when it has one.
        var legCourse = result?.Leg?.CourseCode is { } code
            ? scatterVariants.FirstOrDefault(v => TextKey.Same(v.Code.Trim(), code.Trim()))
            : null;
        var courseOrder = legCourse?.CourseOrderText ?? gs?.CourseOrder;
        var expected = legCourse is not null
            ? legCourse.Controls.ToList()
            : CourseControls(gs?.CourseOrder, inputs.StartFinishCodes, inputs.DisabledCodes);
        var disabledInCourse = DisabledInCourse(courseOrder, inputs.StartFinishCodes, inputs.DisabledCodes);
        var punches = DecodePunchTimes(readout.PunchTimes)
            .Where(p => !inputs.StartFinishCodes.Contains(p.ControlCode.Trim()))
            .ToList();
        var start = _strategies.For(discipline).ResolveStart(new StartContext
        {
            ChipStart = readout.StartTime,
            AssignedStart = link.StartTime,
            FinishTime = readout.FinishTime
        });
        // A relay leg runs from its start in the sequence — the previous leg's finish or the restart (Р-28, Р-29).
        if (result?.Leg is { } leg && result.Team is not null)
            start = leg.Start;

        var context = new SplitsContext
        {
            ExpectedControls = expected,
            CourseOrderText = courseOrder ?? string.Empty,
            DisabledControls = disabledInCourse,
            Punches = punches,
            PointsByCode = inputs.PointsByCode,
            CoordsByCode = inputs.CoordsByCode,
            MapByCode = inputs.MapByCode,
            MapScale = inputs.MapScale,
            StartCoord = inputs.StartCoord,
            FinishCoord = inputs.FinishCoord,
            StartMap = inputs.StartMap,
            FinishMap = inputs.FinishMap,
            StartTime = start,
            FinishTime = readout.FinishTime,
            TimeLimit = gs?.TimeLimitSeconds is { } secs ? TimeSpan.FromSeconds(secs) : null,
            PenaltyPerMinute = gs?.PenaltyPerMinute,
            TeamCommonControls = teamCommon,
            CourseVariants = scatterVariants
        };

        var view = _strategies.For(discipline).BuildSplits(context);
        return legCourse is null ? view : view with { VariantCode = legCourse.Code };
    }

    // The rogaine team's common controls (every member punched them) for one (group, team) on the day —
    // the synchronous counterpart to TeamCommonControlsAsync, over already-loaded links/readouts.
    private IReadOnlySet<string>? ComputeTeamCommonControls(
        IReadOnlyList<ParticipantDay> links, Guid groupId, string teamKey,
        IReadOnlyDictionary<Guid, string> teamByParticipant,
        IReadOnlyDictionary<Guid, GroupDaySettings> settingsByGroup,
        IReadOnlyDictionary<string, FinishReadout> latestByChip,
        HashSet<string> startFinishCodes, HashSet<string> disabledCodes)
    {
        var mates = links.Where(l =>
            l.GroupId == groupId &&
            TextKey.Same(TeamKeyFor(l, teamByParticipant), teamKey)).ToList();

        HashSet<string>? common = null;
        foreach (var mate in mates)
        {
            GroupDaySettings? gs = mate.GroupId is { } gid && settingsByGroup.TryGetValue(gid, out var s) ? s : null;
            var allowed = new HashSet<string>(
                CourseControls(gs?.CourseOrder, startFinishCodes, disabledCodes),
                StringComparer.OrdinalIgnoreCase);

            var punched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (latestByChip.TryGetValue(mate.Chip.Trim(), out var readout))
                foreach (var p in DecodePunchTimes(readout.PunchTimes))
                {
                    var code = p.ControlCode.Trim();
                    if (code.Length > 0 && allowed.Contains(code))
                        punched.Add(code);
                }

            if (common is null)
                common = punched;
            else
                common.IntersectWith(punched);
        }
        return common;
    }

    // Counts the running controls in a free-form course order ("S1 31 32 33 F"), excluding the day's
    // start/finish codes. Null when the course order is blank (unknown count), so the protocol omits it.
    private static int? CountCourseControls(string courseOrder, HashSet<string> startFinishCodes)
    {
        var codes = (courseOrder ?? string.Empty)
            .Split([' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (codes.Length == 0)
            return null;
        return codes.Count(c => !startFinishCodes.Contains(c));
    }

    /// <summary>
    /// Computes each participant's result on one day, keyed by the participant-day link id — see
    /// <see cref="DayResultsCalculator"/>. Reads the day as ONE snapshot (links, read-outs, settings, control
    /// points, variants) unless the caller already holds one, so the result rows always agree with the rows the
    /// caller builds from the same snapshot.
    /// </summary>
    /// <param name="rankCalcs">When non-null, receives the per-group rank-award derivation built by the ranks
    /// pass — used only by the results protocol to print its explanatory line. Null on every other caller.</param>
    /// <param name="snapshot">The day's snapshot when the caller already read it; null = read it here.</param>
    /// <param name="appData">The app-level inputs when the caller already read them (multi-day views).</param>
    private async Task<IReadOnlyDictionary<Guid, ParticipantDayResult>> ComputeDayResultsAsync(
        string folder, EventDay day, CancellationToken cancellationToken,
        Dictionary<Guid, GroupRankCalculation>? rankCalcs = null,
        EventDaySnapshot? snapshot = null,
        DayResultsAppData? appData = null,
        Dictionary<Guid, TeamDayResult>? teamResults = null)
    {
        snapshot ??= await _eventStore.GetDaySnapshotAsync(folder, day.Id, cancellationToken);
        if (snapshot.Links.Count == 0)
            return new Dictionary<Guid, ParticipantDayResult>();

        var needRanks = snapshot.GroupDaySettings.Any(s => s.RankLevel != GroupRankLevel.None);
        if (appData is null || (needRanks && !appData.HasRankData))
            appData = await LoadDayResultsAppDataAsync(needRanks, cancellationToken);

#if DEBUG
        var watch = System.Diagnostics.Stopwatch.StartNew();
#endif
        var results = _calculator.Compute(day, snapshot, appData, rankCalcs, teamResults);
#if DEBUG
        System.Diagnostics.Debug.WriteLine(
            $"[Results] day {day.Number}: {snapshot.Links.Count} links in {watch.ElapsedMilliseconds} ms");
#endif
        return results;
    }

    // The app-database inputs of the results computation. The rank tables are read only when some group
    // awards ranks, so a day without ranks never touches them (as before the calculator existed).
    private async Task<DayResultsAppData> LoadDayResultsAppDataAsync(bool withRanks, CancellationToken cancellationToken)
    {
        // Through IResultsAppData, never IAppStore directly: on a station these are the HOST's tables, so both
        // machines compute the same points and ranks.
        var rules = await _resultsAppData.GetPointsRulesAsync(cancellationToken);
        if (!withRanks)
            return new DayResultsAppData { PointsRules = rules };

        return new DayResultsAppData
        {
            PointsRules = rules,
            HasRankData = true,
            RankQualification = await _resultsAppData.GetRankQualificationAsync(cancellationToken),
            RankConditions = await _resultsAppData.GetRankConditionsAsync(cancellationToken) ?? (3, 8, 12),
            Ranks = await _resultsAppData.GetRanksAsync(cancellationToken)
        };
    }

    // Groups whose names share a TextKey («M12» / «М12») are one group entered twice — typically typed in another
    // keyboard layout on another day. Cross-day views (the summary) treat them as one: each id maps to the first
    // such group in group order. A blank-named group maps to itself.
    private static Dictionary<Guid, Group> CanonicalGroups(IReadOnlyList<Group> groups)
    {
        var firstByKey = new Dictionary<string, Group>(StringComparer.Ordinal);
        var result = new Dictionary<Guid, Group>(groups.Count);
        foreach (var g in groups)
        {
            var key = TextKey.Of(g.Name);
            if (key.Length > 0 && !firstByKey.TryAdd(key, g))
                result[g.Id] = firstByKey[key];
            else
                result[g.Id] = g;
        }
        return result;
    }

    private static Guid CanonicalGroupId(IReadOnlyDictionary<Guid, Group> canonical, Guid groupId) =>
        canonical.TryGetValue(groupId, out var g) ? g.Id : groupId;

    // Total «Бали» (points) for a read-out on a point-scoring day: the scored splits tally over the
    // group's allowed controls (see DayResultsCalculator.ScoreDetail).
    private int ScoreFor(
        FinishReadout readout,
        GroupDaySettings? gs,
        HashSet<string> startFinishCodes,
        HashSet<string> disabledCodes,
        IReadOnlyDictionary<string, int> pointsByCode,
        DisciplineId discipline,
        DateTimeOffset? resolvedStart)
        => DayResultsCalculator.ScoreDetail(
            _strategies.For(discipline), readout, gs, startFinishCodes, disabledCodes, pointsByCode, resolvedStart).Net;

    /// <summary>Cross-day key of an individual draw unit (see <see cref="DrawParticipant.HistoryKey"/>).</summary>
    private static string DrawHistoryKeyOf(Guid participantId) => participantId.ToString("N");

    /// <summary>Cross-day key of a relay team: the group and the team number (its runners may change); a team without
    /// a number yet is keyed by itself (it has no history on other days).</summary>
    private static string DrawHistoryKeyOf(Guid groupId, int? teamNumber, Guid teamId)
        => teamNumber is { } n
            ? $"team:{groupId:N}:{n.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : $"team:{teamId:N}";

    /// <summary>
    /// Builds every draw unit's start positions on the days before <paramref name="day"/> (by day number):
    /// for each earlier day with start times, the links of each group are ordered by start time and each one
    /// gets its 0-based position, the group size and the unit that started right before it. A relay team
    /// (leg-1 link with a team) is one unit keyed by group + team number. Individuals are keyed by participant
    /// id, in whatever group they ran that day. Days with no start times contribute nothing.
    /// </summary>
    private async Task<Dictionary<string, List<DrawHistoryEntry>>> LoadDrawHistoryAsync(
        string folder,
        EventDay day,
        IReadOnlyList<EventDay> days,
        IReadOnlyDictionary<Guid, Participant> byParticipant,
        CancellationToken cancellationToken)
    {
        var history = new Dictionary<string, List<DrawHistoryEntry>>(StringComparer.Ordinal);
        var earlierDays = days.Where(d => d.Number < day.Number).OrderBy(d => d.Number).ToList();
        if (earlierDays.Count == 0)
            return history;

        var allLinks = await _eventStore.GetAllParticipantDaysAsync(folder, cancellationToken);
        foreach (var prev in earlierDays)
        {
            var prevLinks = allLinks
                .Where(l => l.EventDayId == prev.Id && l.GroupId is not null && l.StartTime is not null)
                .ToList();
            if (prevLinks.Count == 0)
                continue;

            var teamNumber = new Dictionary<Guid, int?>();
            if (prevLinks.Any(l => l.RelayTeamId is not null))
            {
                var teams = await _eventStore.GetRelayTeamsAsync(folder, prev.Id, cancellationToken);
                foreach (var t in teams)
                    teamNumber[t.Id] = t.Number;
            }

            var units = new List<(Guid GroupId, TimeSpan Start, string Number, string Key)>(prevLinks.Count);
            foreach (var l in prevLinks)
            {
                var gid = l.GroupId!.Value;
                if (l.RelayTeamId is { } tid)
                {
                    // The team is the unit; only its leg-1 link carries the drawn start.
                    if (l.Leg != 1 || !teamNumber.TryGetValue(tid, out var n))
                        continue;
                    units.Add((gid, l.StartTime!.Value, n?.ToString("D6") ?? string.Empty, DrawHistoryKeyOf(gid, n, tid)));
                }
                else
                {
                    var number = byParticipant.TryGetValue(l.ParticipantId, out var p) ? p.Number : string.Empty;
                    units.Add((gid, l.StartTime!.Value, number, DrawHistoryKeyOf(l.ParticipantId)));
                }
            }

            foreach (var group in units.GroupBy(u => u.GroupId))
            {
                var ordered = group
                    .OrderBy(u => u.Start)
                    .ThenBy(u => u.Number, StringComparer.Ordinal)
                    .ThenBy(u => u.Key, StringComparer.Ordinal)
                    .ToList();
                for (var i = 0; i < ordered.Count; i++)
                {
                    var entry = new DrawHistoryEntry(prev.Number, i, ordered.Count, i > 0 ? ordered[i - 1].Key : null);
                    if (!history.TryGetValue(ordered[i].Key, out var list))
                        history[ordered[i].Key] = list = [];
                    list.Add(entry);
                }
            }
        }

        return history;
    }

    public async Task<DrawPrepData> GetDrawPrepDataAsync(Guid dayId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return new DrawPrepData([]);

        var folder = FolderPath;
        var days = await _eventStore.GetDaysAsync(folder, cancellationToken);
        var day = days.FirstOrDefault(d => d.Id == dayId);
        if (day is null)
            return new DrawPrepData([]);

        var settings = await _eventStore.GetGroupDaySettingsAsync(folder, dayId, cancellationToken);
        var settingsByGroup = settings.ToDictionary(s => s.GroupId);
        var groups = await _eventStore.GetGroupsAsync(folder, cancellationToken);
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var regions = await _eventStore.GetRegionsAsync(folder, cancellationToken);
        var clubs = await _eventStore.GetClubsAsync(folder, cancellationToken);

        // Start/finish and disabled controls are dropped from every parsed course so the "first control" is a
        // real distance control — an OCAD-style opening marker like "S1" (which carries a digit) is not mistaken
        // for КП 1. Same set used to load the scatter variants, reduced to their required controls.
        var controlPoints = await _eventStore.GetControlPointsAsync(folder, dayId, cancellationToken);
        var startFinishCodes = new HashSet<string>(
            controlPoints
                .Where(c => c.Type is ControlPointType.Start or ControlPointType.Finish)
                .Select(c => c.Code.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var disabledCodes = DisabledCodesOf(controlPoints);
        var dayDefault = day.DefaultDiscipline;
        var variantsByGroup = await LoadCourseVariantsAsync(
            folder, dayId, settingsByGroup, dayDefault, startFinishCodes, disabledCodes, cancellationToken);

        var groupName = groups.ToDictionary(g => g.Id, g => g.Name);
        var byParticipant = participants.ToDictionary(p => p.Id);
        var regionName = regions.ToDictionary(r => r.Id, r => r.Name);
        var clubName = clubs.ToDictionary(c => c.Id, c => c.Name);
        var history = await LoadDrawHistoryAsync(folder, day, days, byParticipant, cancellationToken);

        // Members per group on this day (a link with no group is left out — the draw is per group).
        var membersByGroup = new Dictionary<Guid, List<DrawParticipant>>();
        foreach (var link in links)
        {
            if (link.GroupId is not { } gid)
                continue;
            if (!byParticipant.TryGetValue(link.ParticipantId, out var p))
                continue;

            var region = p.RegionId is { } rid && regionName.TryGetValue(rid, out var rn) ? rn : string.Empty;
            var club = p.ClubId is { } cid && clubName.TryGetValue(cid, out var cn) ? cn : string.Empty;

            if (!membersByGroup.TryGetValue(gid, out var list))
                membersByGroup[gid] = list = [];
            var key = DrawHistoryKeyOf(p.Id);
            list.Add(new DrawParticipant(
                link.Id, p.Id, p.FullName, p.Number, region, club, p.Team, key, history.GetValueOrDefault(key)));
        }

        // Relay groups (Р-36): the draw unit is the team, separated by the team's club/region; the drawn minute is
        // written onto the leg-1 runner's link, so a team without a leg-1 runner is left out of the draw.
        if (settings.Any(s => _strategies.For(s.DisciplineOverride ?? day.DefaultDiscipline).Competitors
                == CompetitorModel.TeamWithLegs))
        {
            var teams = await _eventStore.GetRelayTeamsAsync(folder, dayId, cancellationToken);
            var leg1ByTeam = links
                .Where(l => l.RelayTeamId is not null && l.Leg == 1)
                .GroupBy(l => l.RelayTeamId!.Value)
                .ToDictionary(g => g.Key, g => g.First());
            foreach (var s in settings.Where(s =>
                         _strategies.For(s.DisciplineOverride ?? day.DefaultDiscipline).Competitors
                         == CompetitorModel.TeamWithLegs))
            {
                var units = new List<DrawParticipant>();
                foreach (var team in teams.Where(t => t.GroupId == s.GroupId).OrderBy(t => RelayTeamNumbers.SortKey(t.Number)))
                {
                    if (!leg1ByTeam.TryGetValue(team.Id, out var leg1))
                        continue;
                    var number = RelayTeamNumbers.Text(team.Number);
                    var key = DrawHistoryKeyOf(team.GroupId, team.Number, team.Id);
                    units.Add(new DrawParticipant(
                        leg1.Id, leg1.ParticipantId,
                        team.Name.Length > 0 ? team.Name : number,
                        number,
                        team.RegionId is { } rid && regionName.TryGetValue(rid, out var rn) ? rn : string.Empty,
                        team.ClubId is { } cid && clubName.TryGetValue(cid, out var cn) ? cn : string.Empty,
                        team.Name,
                        key,
                        history.GetValueOrDefault(key)));
                }
                membersByGroup[s.GroupId] = units;
            }
        }

        // One DrawGroup per group on the day, in the day grid order, carrying the clash-check data the draw
        // page needs: whether the discipline runs a prescribed order at all (free-order formats opt out), the
        // set of opening controls to test for a shared-first-control clash, and — for scatter — every variant's
        // full order (so two scatter groups running the identical variant set flag as a same-course clash).
        var drawGroups = new List<DrawGroup>(settings.Count);
        foreach (var s in settings)
        {
            if (!groupName.TryGetValue(s.GroupId, out var name))
                continue;
            var members = membersByGroup.TryGetValue(s.GroupId, out var m) ? m : [];

            var discipline = s.DisciplineOverride ?? dayDefault;
            var checksClash = _strategies.For(discipline).ChecksStartControlClash;

            if (!checksClash)
            {
                // Free-order format (за вибором / рогейн / score-by-time): no prescribed order, so no clash
                // check. Keep the raw controls only for the display label.
                var raw = CourseControls(s.CourseOrder, startFinishCodes, disabledCodes);
                drawGroups.Add(new DrawGroup(
                    s.GroupId, name, raw.Count > 0 ? raw[0] : string.Empty, [], members));
                continue;
            }

            if (_strategies.For(discipline).UsesCourseVariants
                && variantsByGroup.TryGetValue(s.GroupId, out var variants) && variants.Count > 0)
            {
                // Scatter: several valid orders. The opening controls are the first control of each variant
                // (deduplicated); the whole variant set is carried for the same-course comparison. The
                // representative first control (for lane clustering / the label) is the first variant's first.
                var variantOrders = variants.Select(v => v.Controls).Where(c => c.Count > 0).ToList();
                var firstControls = variantOrders
                    .Select(c => c[0])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                drawGroups.Add(new DrawGroup(
                    s.GroupId, name,
                    firstControls.Count > 0 ? firstControls[0] : string.Empty,
                    [], members,
                    ChecksClash: true,
                    FirstControls: firstControls,
                    Variants: variantOrders));
                continue;
            }

            // Fixed order (set course / mixed, or a scatter group with no variants configured): one course,
            // one opening control.
            var controls = CourseControls(s.CourseOrder, startFinishCodes, disabledCodes);
            var first = controls.Count > 0 ? controls[0] : string.Empty;
            drawGroups.Add(new DrawGroup(
                s.GroupId, name, first, controls, members,
                ChecksClash: true,
                FirstControls: first.Length > 0 ? [first] : []));
        }

        return new DrawPrepData(drawGroups);
    }

    public async Task<StartOrderData> GetStartOrderDataAsync(Guid dayId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return new StartOrderData([]);

        var folder = FolderPath;
        // One transactional read: every list below is from the same snapshot (see GetDaySnapshotAsync).
        var snapshot = await _eventStore.GetDaySnapshotAsync(folder, dayId, cancellationToken);
        var settings = snapshot.GroupDaySettings;
        var groups = snapshot.Groups;
        var links = DistinctLinks(snapshot.Links);
        var participants = snapshot.Participants;
        var regions = snapshot.Regions;
        var clubs = snapshot.Clubs;

        var groupName = groups.ToDictionary(g => g.Id, g => g.Name);
        var byParticipant = participants.ToDictionary(p => p.Id);
        var regionName = regions.ToDictionary(r => r.Id, r => r.Name);
        var clubName = clubs.ToDictionary(c => c.Id, c => c.Name);

        // Members per group on this day, carrying each member's current start time (a link with no group is
        // left out — start order is edited per group).
        var membersByGroup = new Dictionary<Guid, List<StartOrderMember>>();
        foreach (var link in links)
        {
            if (link.GroupId is not { } gid)
                continue;
            if (!byParticipant.TryGetValue(link.ParticipantId, out var p))
                continue;

            var region = p.RegionId is { } rid && regionName.TryGetValue(rid, out var rn) ? rn : string.Empty;
            var club = p.ClubId is { } cid && clubName.TryGetValue(cid, out var cn) ? cn : string.Empty;

            if (!membersByGroup.TryGetValue(gid, out var list))
                membersByGroup[gid] = list = [];
            list.Add(new StartOrderMember(link.Id, link.StartTime, p.Number, p.FullName, region, club));
        }

        // One group per group on the day, in the day grid order; members ordered by start time (unset last).
        var orderGroups = new List<StartOrderGroup>(settings.Count);
        foreach (var s in settings)
        {
            if (!groupName.TryGetValue(s.GroupId, out var name))
                continue;
            var members = membersByGroup.TryGetValue(s.GroupId, out var m) ? m : [];
            var ordered = members
                .OrderBy(x => x.StartTime is null)
                .ThenBy(x => x.StartTime ?? TimeSpan.Zero)
                .ToList();
            orderGroups.Add(new StartOrderGroup(s.GroupId, name, ordered));
        }

        return new StartOrderData(orderGroups);
    }

    public async Task<QuickWithdrawalData> GetQuickWithdrawalDataAsync(Guid dayId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return new QuickWithdrawalData([]);

        var folder = FolderPath;
        var days = await _eventStore.GetDaysAsync(folder, cancellationToken);
        var day = days.FirstOrDefault(d => d.Id == dayId);
        if (day is null)
            return new QuickWithdrawalData([]);

        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var byParticipant = participants.ToDictionary(p => p.Id);

        // Compute the day's results (keyed by link) so each member carries whether their chip was already
        // read — the flag that forbids marking DNS on someone who clearly started.
        var byLink = await ComputeDayResultsAsync(folder, day, cancellationToken);

        var members = new List<QuickWithdrawalMember>(links.Count);
        foreach (var link in links)
        {
            if (!byParticipant.TryGetValue(link.ParticipantId, out var p))
                continue;
            var hasReadout = byLink.TryGetValue(link.Id, out var r) && r.HasReadout;
            members.Add(new QuickWithdrawalMember(
                link.ParticipantId, p.Number, p.FullName, hasReadout, link.ResultStatusOverride)
            {
                LegBib = r?.LegBib
            });
        }

        // Order by numeric start number (unnumbered last), then by name — the natural search order.
        var ordered = members
            .OrderBy(m => !int.TryParse(m.Number, out _))
            .ThenBy(m => int.TryParse(m.Number, out var n) ? n : int.MaxValue)
            .ThenBy(m => m.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new QuickWithdrawalData(ordered);
    }

    public async Task<int> SaveDrawStartTimesAsync(
        IReadOnlyList<DrawStartAssignment> assignments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        if (_session.CurrentEvent is null || assignments.Count == 0)
            return 0;

        await EnsureDayEditableAsync(CurrentDayId, cancellationToken);

        var batch = assignments.Select(a => (a.LinkId, a.StartTime)).ToList();
        return await _eventStore.SetParticipantDayStartTimesBatchAsync(FolderPath, batch, cancellationToken);
    }

    public async Task<IReadOnlyList<FinishReadoutRow>> GetFinishReadoutRowsAsync(CancellationToken cancellationToken = default, Guid? forDayId = null)
        => (await GetFinishReadoutLogAsync(cancellationToken, forDayId)).Rows;

    public async Task<FinishReadoutLog> GetFinishReadoutLogAsync(CancellationToken cancellationToken = default, Guid? forDayId = null)
    {
        var (knownDay, otherDay) = await ResolveDayAsync(forDayId, cancellationToken);
        if (!knownDay)
            return new FinishReadoutLog([], FinishReadoutCounts.Empty);
        if (otherDay is not null)
            _dayOverride.Value = otherDay;
        if (_session.CurrentEvent is null || CurrentDay is null)
            return new FinishReadoutLog([], FinishReadoutCounts.Empty);

        var folder = FolderPath;
        var dayId = CurrentDayId;
        // The read-out log itself stays a separate read: the background poller appends to it continuously,
        // and showing the newest punches matters more here than pinning it to the snapshot below.
        var readouts = await _eventStore.GetFinishReadoutsAsync(folder, dayId, cancellationToken);

        // Everything the log resolves against comes from ONE transactional read, so participants, groups,
        // day settings and control points can't disagree with each other (see GetDaySnapshotAsync).
        var snapshot = await _eventStore.GetDaySnapshotAsync(folder, dayId, cancellationToken);

        // Resolve each chip against the day's participants (chip → number/name/group), case-insensitive.
        var links = DistinctLinks(snapshot.Links);
        var participants = snapshot.Participants;
        var groups = snapshot.Groups;
        var byId = participants.ToDictionary(p => p.Id);
        var groupName = groups.ToDictionary(g => g.Id, g => g.Name);

        // For the order-check: each group's prescribed course + settings, and the day's start/finish
        // control codes (so they're dropped from both the expected course and the punched sequence).
        var settings = snapshot.GroupDaySettings;
        var settingsByGroup = settings.ToDictionary(s => s.GroupId);
        var controlPoints = snapshot.ControlPoints;
        var startFinishCodes = new HashSet<string>(
            controlPoints
                .Where(c => c.Type is ControlPointType.Start or ControlPointType.Finish)
                .Select(c => c.Code.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var disabledCodes = DisabledCodesOf(controlPoints);
        var dayDefault = CurrentDayDefaultDiscipline;
        var variantsByGroup = await LoadCourseVariantsAsync(
            folder, dayId, settingsByGroup, dayDefault, startFinishCodes, disabledCodes, cancellationToken);

        // Per-control point values — needed only to tally the «Бали» column on point-scoring days.
        var pointsByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cp in controlPoints)
            if (cp.Points is { } pts)
                pointsByCode[cp.Code.Trim()] = pts;

        // A chip on a day is unique (enforced when assigning), so first match by trimmed chip wins.
        var holderByChip = new Dictionary<string, ParticipantDay>(TextKey.Comparer);
        foreach (var link in links)
        {
            var key = link.Chip.Trim();
            if (key.Length > 0)
                holderByChip.TryAdd(key, link);
        }

        // Rental-chip "collect now" flag: a rental chip should be handed back on the last day its holder
        // uses it. For each (participant, chip) we find the highest day number they run with that chip; on
        // that day the chip is collected. So a chip on the current day is collectable iff (a) it's a rental
        // chip and (b) the holder holds no *later* day (higher day number) with the same chip. This makes
        // the highlight always fire on the runner's final day with the chip, and earlier only when they
        // don't reappear with it. Non-rental (own) chips are never collected.
        var rentalChipSet = new HashSet<string>(
            (await _eventStore.GetRentalChipsAsync(folder, cancellationToken)).Select(c => c.Number.Trim()),
            TextKey.Comparer);
        var currentDayNumber = CurrentDay.Number;
        var allDays = await _eventStore.GetDaysAsync(folder, cancellationToken);
        var dayNumberById = allDays.ToDictionary(d => d.Id, d => d.Number);
        var allLinks = await _eventStore.GetAllParticipantDaysAsync(folder, cancellationToken);
        // The highest day number on which each (participant, chip) pair appears, so we know the last day
        // the runner uses that chip.
        var lastDayForHolderChip = new Dictionary<(Guid, string), int>();
        foreach (var link in allLinks)
        {
            var chip = link.Chip.Trim();
            if (chip.Length == 0 || !dayNumberById.TryGetValue(link.EventDayId, out var num))
                continue;
            var key = (link.ParticipantId, chip.ToUpperInvariant());
            if (!lastDayForHolderChip.TryGetValue(key, out var existing) || num > existing)
                lastDayForHolderChip[key] = num;
        }

        // Computed per-group placement (keyed by link id) so each known row can show the runner's place in
        // their group; the same ranking the participant tables use. A place is only assigned to an OK,
        // in-competition result — null otherwise.
        var placeByLink = await ComputeDayResultsAsync(folder, CurrentDay, cancellationToken, snapshot: snapshot);

        // Each group's leader time (the place-1 OK result), so a placed runner's «Відставання» is their
        // result time minus it. Only time-based results carry a ResultTime; a scoring day leaves it null,
        // so no gap is shown there (the same loss-to-leader convention the protocols use).
        var groupByLink = links.ToDictionary(l => l.Id, l => l.GroupId);
        var leaderTimeByGroup = new Dictionary<Guid, TimeSpan>();
        foreach (var (linkId, res) in placeByLink)
        {
            if (res.Place == 1 && res.RankingTime is { } lt
                && groupByLink.TryGetValue(linkId, out var g) && g is { } gid)
                leaderTimeByGroup[gid] = lt;
        }

        // The latest read-out of each chip — the one the results (and so a relay leg's sequence) are built from.
        var latestOrderByChip = new Dictionary<string, int>(TextKey.Comparer);
        foreach (var lr in readouts)
        {
            var key = lr.ChipNumber.Trim();
            if (!latestOrderByChip.TryGetValue(key, out var o) || lr.Order > o)
                latestOrderByChip[key] = lr.Order;
        }

        var rows = new List<FinishReadoutRow>(readouts.Count);
        // The head count under the log: each runner who read (once, however often), each unrecognised chip.
        var readHolders = new HashSet<Guid>();
        var unknownChips = new HashSet<string>(TextKey.Comparer);
        foreach (var r in readouts)
        {
            if (holderByChip.TryGetValue(r.ChipNumber.Trim(), out var link) && byId.TryGetValue(link.ParticipantId, out var p))
            {
                readHolders.Add(p.Id);
                var group = link.GroupId is { } gid && groupName.TryGetValue(gid, out var gn) ? gn : string.Empty;
                var (status, detail, detailKind, resolvedStart) = EvaluateFinish(r, link, settingsByGroup, startFinishCodes, disabledCodes, dayDefault, variantsByGroup,
                    s => DisciplineSettings.Resolve(s, snapshot.DisciplineSettings, dayId));
                // A judge's manual status override wins over the computed one (and clears its detail).
                if (r.ManualStatus is { } manual)
                {
                    status = manual;
                    detail = string.Empty;
                    detailKind = FinishDetailKind.MissingControl;
                }
                var elapsed = resolvedStart is { } s && r.FinishTime is { } f ? f - s : (TimeSpan?)null;
                // «Бали»: tally the scored splits when this row's discipline scores points (rogaine); null otherwise.
                GroupDaySettings? gs = link.GroupId is { } sgid && settingsByGroup.TryGetValue(sgid, out var sset) ? sset : null;
                var discipline = gs?.DisciplineOverride ?? dayDefault;
                int? score = _strategies.For(discipline).UsesControlPointPoints
                    ? ScoreFor(r, gs, startFinishCodes, disabledCodes, pointsByCode, discipline, resolvedStart)
                    : null;
                var result = placeByLink.TryGetValue(link.Id, out var pr) ? pr : null;
                var place = result?.Place;
                // A relay leg is judged in its sequence (start = previous leg's finish / restart, Р-28…Р-30), so the
                // latest read-out of the chip shows the calculator's leg result instead of a lone evaluation.
                var relayLeg = result?.Leg is { } rl && result.Team is not null
                    && latestOrderByChip.TryGetValue(r.ChipNumber.Trim(), out var latestOrder) && latestOrder == r.Order ? rl : null;
                if (relayLeg is not null && r.ManualStatus is null)
                {
                    status = relayLeg.Pending ? FinishStatus.None : result!.Computed;
                    // The leg's own course decides the detail (Р-41), not the group's course order.
                    detail = status == FinishStatus.Mp ? result!.StatusDetail : string.Empty;
                    detailKind = result!.StatusDetailKind;
                    resolvedStart = relayLeg.Start;
                    elapsed = !relayLeg.Pending && r.FinishTime is { } lf && relayLeg.Start is { } ls ? lf - ls : null;
                }
                // «Відставання»: loss to the group leader, for a placed OK result whose time exceeds the
                // leader's (the leader themselves and non-placed rows show none).
                TimeSpan? gap = place is not null && result?.RankingTime is { } rt
                    && link.GroupId is { } ggid && leaderTimeByGroup.TryGetValue(ggid, out var lead) && rt > lead
                    ? rt - lead
                    : null;
                // Collect the rental chip on the holder's last day with it: rental chip, and no later day
                // (higher number) has this participant running with the same chip.
                var chipKey = r.ChipNumber.Trim();
                var isRental = rentalChipSet.Contains(chipKey);
                var collect = isRental
                    && lastDayForHolderChip.TryGetValue((link.ParticipantId, chipKey.ToUpperInvariant()), out var lastNum)
                    && currentDayNumber >= lastNum;
                rows.Add(new FinishReadoutRow(r.Id, r.Order, r.ChipNumber, r.StartTime, r.FinishTime,
                    IsKnown: true, result?.LegBib ?? p.Number, p.FullName, group, status, detail, resolvedStart, elapsed, score, place, gap,
                    CollectRentalChip: collect, IsManualStatus: r.ManualStatus is not null,
                    StatusDetailKind: detailKind)
                {
                    RelayTeamNumber = result?.Team?.Number,
                    RelayTeamName = result?.Team?.Name ?? string.Empty,
                    RelayLeg = result?.Leg?.Leg,
                    RelayRestarted = relayLeg?.Restarted ?? false,
                    RelayPending = relayLeg?.Pending ?? false,
                    RelayCourseCode = result?.Leg?.CourseCode
                });
            }
            else
            {
                unknownChips.Add(r.ChipNumber.Trim());
                // An unrecognised chip has no group/course to judge against — no computed status — but a
                // manual override still shows (a judge may rule on an unknown chip).
                rows.Add(new FinishReadoutRow(r.Id, r.Order, r.ChipNumber, r.StartTime, r.FinishTime,
                    IsKnown: false, ParticipantNumber: string.Empty, FullName: string.Empty, GroupName: string.Empty,
                    Status: r.ManualStatus ?? FinishStatus.None, StatusDetail: string.Empty, ResolvedStartTime: null, Elapsed: null,
                    IsManualStatus: r.ManualStatus is not null));
            }
        }

        // Still out: the dashboard's «На дистанції» test over the same results, minus anyone who has read (a read
        // with no start/finish punch would otherwise count as both). Orphaned links are skipped like everywhere.
        var onCourse = links.Count(l => byId.ContainsKey(l.ParticipantId)
            && !readHolders.Contains(l.ParticipantId)
            && IsOnCourse(placeByLink.TryGetValue(l.Id, out var res) ? res : ParticipantDayResult.Empty));

        return new FinishReadoutLog(rows, new FinishReadoutCounts(readHolders.Count, onCourse, unknownChips.Count));
    }

    // A day member still on course: no actual (chip) start, no finish and a blank status — the same three-field
    // test the «На дистанції» participants filter uses, shared by the dashboard tile and the finish-read count.
    private static bool IsOnCourse(ParticipantDayResult result) =>
        result.Status == FinishStatus.None && result.ActualStart is null && result.FinishTime is null;

    public async Task<bool> CurrentDayUsesScoreAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || CurrentDay is null)
            return false;

        var dayDefault = CurrentDayDefaultDiscipline;
        if (_strategies.For(dayDefault).UsesControlPointPoints)
            return true;

        var settings = await _eventStore.GetGroupDaySettingsAsync(FolderPath, CurrentDayId, cancellationToken);
        return settings.Any(s => _strategies.For(s.DisciplineOverride ?? dayDefault).UsesControlPointPoints);
    }

    // The controls every member of the given link's rogaine team punched (the team's common set), so the
    // splits panel/printout can mark which controls count toward the team. The team is the day members
    // sharing the same group + the same non-empty participant team. Null when the runner has no team (then
    // there is no team annotation), or when only that one member's set is available. Each teammate's set is
    // built from their latest read-out, the same way ScoreDetailFor does for the day results.
    private async Task<IReadOnlySet<string>?> TeamCommonControlsAsync(
        string folder, Guid dayId, ParticipantDay link, IReadOnlyList<ParticipantDay> links,
        IReadOnlyDictionary<Guid, GroupDaySettings> settingsByGroup, HashSet<string> startFinishCodes,
        HashSet<string> disabledCodes, CancellationToken cancellationToken)
    {
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var teamByParticipant = participants.ToDictionary(p => p.Id, p => p.Team.Trim());
        var teamKey = TeamKeyFor(link, teamByParticipant);
        if (teamKey.Length == 0)
            return null; // teamless runner — поза конкурсом, nothing to annotate

        var mates = links.Where(l =>
            l.GroupId == link.GroupId &&
            TextKey.Same(TeamKeyFor(l, teamByParticipant), teamKey)).ToList();

        // A поза конкурсом member's punches don't count toward the team total, so they don't shape the
        // common-controls annotation either. Drop them unless the whole team is поза конкурсом (then there
        // is nothing scoring to intersect, so fall back to all mates so the annotation still has a basis).
        var scoring = mates.Where(m => !m.OutOfCompetition).ToList();
        if (scoring.Count > 0)
            mates = scoring;

        var readouts = await _eventStore.GetFinishReadoutsAsync(folder, dayId, cancellationToken);
        var latestByChip = new Dictionary<string, FinishReadout>(TextKey.Comparer);
        foreach (var r in readouts)
        {
            var key = r.ChipNumber.Trim();
            if (key.Length == 0)
                continue;
            if (!latestByChip.TryGetValue(key, out var cur) || r.Order > cur.Order)
                latestByChip[key] = r;
        }

        HashSet<string>? common = null;
        foreach (var mate in mates)
        {
            GroupDaySettings? gs = mate.GroupId is { } gid && settingsByGroup.TryGetValue(gid, out var s) ? s : null;
            var allowed = new HashSet<string>(
                CourseControls(gs?.CourseOrder, startFinishCodes, disabledCodes),
                StringComparer.OrdinalIgnoreCase);

            var punched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (latestByChip.TryGetValue(mate.Chip.Trim(), out var readout))
                foreach (var p in DecodePunchTimes(readout.PunchTimes))
                {
                    var code = p.ControlCode.Trim();
                    if (code.Length > 0 && allowed.Contains(code))
                        punched.Add(code);
                }

            if (common is null)
                common = punched;
            else
                common.IntersectWith(punched);
        }

        return common;
    }

    public async Task<SplitsView?> GetFinishSplitsAsync(Guid readoutId, CancellationToken cancellationToken = default, Guid? forDayId = null)
    {
        var (knownDay, otherDay) = await ResolveDayAsync(forDayId, cancellationToken);
        if (!knownDay)
            return null;
        if (otherDay is not null)
            _dayOverride.Value = otherDay;
        if (_session.CurrentEvent is null || CurrentDay is null)
            return null;

        var folder = FolderPath;
        var dayId = CurrentDayId;

        var readouts = await _eventStore.GetFinishReadoutsAsync(folder, dayId, cancellationToken);
        var readout = readouts.FirstOrDefault(r => r.Id == readoutId);
        if (readout is null)
            return null;

        // Resolve the chip's holder on the day. An unrecognised chip (held by nobody on the day) has no
        // prescribed course to judge against, but we still show its raw passage — every punch in chip
        // order, all flagged off-course — so the operator can read the route an unknown chip took.
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var link = links.FirstOrDefault(l =>
            TextKey.Same(l.Chip.Trim(), readout.ChipNumber.Trim()));

        var settings = await _eventStore.GetGroupDaySettingsAsync(folder, dayId, cancellationToken);
        var settingsByGroup = settings.ToDictionary(s => s.GroupId);
        var controlPoints = await _eventStore.GetControlPointsAsync(folder, dayId, cancellationToken);
        var startFinishCodes = new HashSet<string>(
            controlPoints
                .Where(c => c.Type is ControlPointType.Start or ControlPointType.Finish)
                .Select(c => c.Code.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var disabledCodes = DisabledCodesOf(controlPoints);

        // Per-control point values (score formats); last write wins on a duplicate code.
        var pointsByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cp in controlPoints)
            if (cp.Points is { } pts)
                pointsByCode[cp.Code.Trim()] = pts;

        // Per-control positions, so the ordered splits can show each leg's straight-line distance/pace.
        // The paper-map position (mm) is preferred over the geographic one — see ComputeDistance: the
        // Web Mercator coordinates are stretched by 1/cos(latitude), the map mm × scale are not.
        var coordsByCode = new Dictionary<string, GeoPoint>(StringComparer.OrdinalIgnoreCase);
        var mapByCode = new Dictionary<string, MapPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var cp in controlPoints)
        {
            coordsByCode[cp.Code.Trim()] = new GeoPoint(cp.Latitude, cp.Longitude);
            mapByCode[cp.Code.Trim()] = new MapPoint(cp.MapX, cp.MapY);
        }
        // Scale is uniform across a day's controls (captured per control at import); take the first stated.
        var mapScale = controlPoints.Select(c => c.MapScale).FirstOrDefault(s => s is > 0);

        // Start/finish positions for the first (start → first control) and last (last control → finish) legs.
        var startCp = controlPoints.FirstOrDefault(c => c.Type is ControlPointType.Start);
        var finishCp = controlPoints.FirstOrDefault(c => c.Type is ControlPointType.Finish);
        var startCoord = startCp is null ? default : new GeoPoint(startCp.Latitude, startCp.Longitude);
        var finishCoord = finishCp is null ? default : new GeoPoint(finishCp.Latitude, finishCp.Longitude);
        var startMap = startCp is null ? default : new MapPoint(startCp.MapX, startCp.MapY);
        var finishMap = finishCp is null ? default : new MapPoint(finishCp.MapX, finishCp.MapY);

        GroupDaySettings? gs = link?.GroupId is { } gid && settingsByGroup.TryGetValue(gid, out var s) ? s : null;
        var discipline = gs?.DisciplineOverride ?? CurrentDayDefaultDiscipline;

        // Rogaine team annotation: when this chip's holder runs a rogaine team, work out which controls
        // the whole team punched so the passage/course can mark the ones that count for the team. Null for
        // a non-rogaine row, an unknown chip or a teamless runner (no annotation shown).
        IReadOnlySet<string>? teamCommon = null;
        if (link is not null && _strategies.For(discipline).Competitors == CompetitorModel.Team)
            teamCommon = await TeamCommonControlsAsync(
                folder, dayId, link, links, settingsByGroup, startFinishCodes, disabledCodes, cancellationToken);

        // Scatter variants for the runner's group, so the strategy can auto-detect which order they ran.
        // Empty for a non-scatter group or an unknown chip (no group).
        var variantsByGroup = await LoadCourseVariantsAsync(
            folder, dayId, settingsByGroup, CurrentDayDefaultDiscipline, startFinishCodes, disabledCodes, cancellationToken);
        var scatterVariants = link?.GroupId is { } svgid && variantsByGroup.TryGetValue(svgid, out var svs)
            ? svs
            : NoCourseVariants;

        // Expected = course controls minus start/finish; punches = read punches (with times) minus the
        // same. An unknown chip (no link) has no prescribed course, so the expected list is empty and
        // every punch reads off-course. Start = chip start when present, else the assigned start (only
        // when a holder is known) paired with the finish's date.
        var expected = CourseControls(gs?.CourseOrder, startFinishCodes, disabledCodes);
        var disabledInCourse = DisabledInCourse(gs?.CourseOrder, startFinishCodes, disabledCodes);
        var punches = DecodePunchTimes(readout.PunchTimes)
            .Where(p => !startFinishCodes.Contains(p.ControlCode.Trim()))
            .ToList();
        var start = (link is null ? _strategies.For(DisciplineId.SetCourse) : _strategies.For(discipline))
            .ResolveStart(new StartContext
            {
                ChipStart = readout.StartTime,
                AssignedStart = link?.StartTime,
                FinishTime = readout.FinishTime
            });
        // A relay leg's splits run from the leg's start in the sequence — the previous leg's finish or the
        // restart (Р-28, Р-29) — for the chip's latest read-out, which the results are built from.
        CourseVariantData? legCourse = null;
        if (link?.RelayTeamId is not null && _strategies.For(discipline).Competitors == CompetitorModel.TeamWithLegs
            && !readouts.Any(o => o.Order > readout.Order && TextKey.Same(o.ChipNumber.Trim(), readout.ChipNumber.Trim())))
        {
            var dayResults = await ComputeDayResultsAsync(folder, CurrentDay, cancellationToken);
            if (dayResults.TryGetValue(link.Id, out var legResult) && legResult.Leg is { } leg)
            {
                start = leg.Start;
                // The leg's own named course (Р-44): the splits are judged against it, not the group's order.
                legCourse = leg.CourseCode is { } code
                    ? scatterVariants.FirstOrDefault(v => TextKey.Same(v.Code.Trim(), code.Trim()))
                    : null;
            }
        }
        if (legCourse is not null)
        {
            expected = legCourse.Controls.ToList();
            disabledInCourse = DisabledInCourse(legCourse.CourseOrderText, startFinishCodes, disabledCodes);
        }

        var context = new SplitsContext
        {
            ExpectedControls = expected,
            CourseOrderText = legCourse?.CourseOrderText ?? gs?.CourseOrder ?? string.Empty,
            DisabledControls = disabledInCourse,
            Punches = punches,
            PointsByCode = pointsByCode,
            CoordsByCode = coordsByCode,
            MapByCode = mapByCode,
            MapScale = mapScale,
            StartCoord = startCoord,
            FinishCoord = finishCoord,
            StartMap = startMap,
            FinishMap = finishMap,
            StartTime = start,
            FinishTime = readout.FinishTime,
            // Over-time penalty inputs (rogaine): the group's time limit and penalty rate. A null rate lets
            // the strategy apply its default (rogaine = 1 бал/min); an unknown chip (no group) has neither.
            TimeLimit = gs?.TimeLimitSeconds is { } secs ? TimeSpan.FromSeconds(secs) : null,
            PenaltyPerMinute = gs?.PenaltyPerMinute,
            TeamCommonControls = teamCommon,
            CourseVariants = scatterVariants
        };

        // A known holder gets their group's discipline shape. An unknown chip has no group/course, so we
        // always use the ordered (set-course) layout: with no prescribed controls it degrades to a flat
        // list of every punch in chip order — the raw passage, which is what's wanted for an unknown chip
        // (the scored layout would instead drop every punch as "not an allowed control" and show nothing).
        var strategy = link is null ? _strategies.For(DisciplineId.SetCourse) : _strategies.For(discipline);
        var view = strategy.BuildSplits(context);
        return legCourse is null ? view : view with { VariantCode = legCourse.Code };
    }

    public async Task<SplitPrintDocument?> GetSplitPrintDocumentAsync(Guid readoutId, bool reprint = false, CancellationToken cancellationToken = default, Guid? forDayId = null)
    {
        var (knownDay, otherDay) = await ResolveDayAsync(forDayId, cancellationToken);
        if (!knownDay)
            return null;
        if (otherDay is not null)
            _dayOverride.Value = otherDay;
        // The splits view (passage in order) is the printout's body; the resolved row carries the header
        // metadata (name/number/group/result/status). Both already resolve the chip's holder on the day,
        // so we just zip them by id rather than duplicating the resolution.
        var view = await GetFinishSplitsAsync(readoutId, cancellationToken);
        if (view is null)
            return null;

        var row = (await GetFinishReadoutRowsAsync(cancellationToken)).FirstOrDefault(r => r.Id == readoutId);
        if (row is null)
            return null;

        // Rental-chip flag: the printout says "орендований чіп" for a chip in the competition's rental set.
        var rentalChips = await _eventStore.GetRentalChipsAsync(FolderPath, cancellationToken);
        var isRental = rentalChips.Any(c =>
            TextKey.Same(c.Number.Trim(), row.ChipNumber.Trim()));

        // Manual bonus («Бонус») the judge set on this runner's day record, if any: it corrects the score the
        // same way the result columns do, so the slip prints the summed total and spells the bonus out.
        var links = await _eventStore.GetParticipantDaysAsync(FolderPath, CurrentDayId, cancellationToken);
        var bonus = links.FirstOrDefault(l =>
            TextKey.Same(l.Chip.Trim(), row.ChipNumber.Trim()))?.Bonus;

        var status = row.Status switch
        {
            FinishStatus.Ok => "OK",
            FinishStatus.Mp => "MP",
            FinishStatus.Ovt => "OVT",
            FinishStatus.Dnf => "DNF",
            FinishStatus.Dns => "DNS",
            FinishStatus.Dsq => "DSQ",
            _ => string.Empty
        };

        var rows = view.Layout == SplitsLayout.Ordered
            ? BuildPassageRows(view.Passage, view.HasPoints)
            : BuildScoredRows(view.Entries, view.HasPoints);

        // Start/finish wall-clock and course length come from the ordered passage (start marker → finish
        // marker). The average pace is the result time over the total straight-line distance.
        var startClock = string.Empty;
        var finishClock = string.Empty;
        decimal totalKm = 0m;
        if (view.Layout == SplitsLayout.Ordered)
        {
            foreach (var p in view.Passage)
            {
                // Rendered in local time — punch stamps round-trip through UTC ticks, so formatting
                // them raw would print the wall clock shifted by the local UTC offset.
                if (p.Kind == PassageKind.Start && p.Time is { } st)
                    startClock = st.ToLocalTime().ToString("HH\\:mm\\:ss", CultureInfo.InvariantCulture);
                else if (p.Kind == PassageKind.Finish && p.Time is { } ft)
                    finishClock = ft.ToLocalTime().ToString("HH\\:mm\\:ss\\.f", CultureInfo.InvariantCulture);
                if (p.LegKm is { } km && km > 0m)
                    totalKm += km;
            }
        }

        var resultElapsed = row.Elapsed is { } e && e >= TimeSpan.Zero ? e : (TimeSpan?)null;

        var (place, readOutCount) = await CurrentPlaceAsync(row, reprint, cancellationToken);

        // When a set course broke the order (MP), append the prescribed (correct) order so the slip shows
        // what should have been done, with the first untaken control flagged as where the route went wrong.
        var correctOrder = view.Layout == SplitsLayout.Ordered && row.Status == FinishStatus.Mp
            ? BuildCorrectOrderRows(view.Expected)
            : [];

        // The result columns add the manual bonus on top of the discipline's net points; the slip mirrors that
        // so the printed «Сума балів» matches the protocol. A bonus only applies to a point-scoring view —
        // and only a discipline that values its controls is one: «за вибором по кількості КП» is a Scored
        // layout that scores nothing, so it prints no points line at all.
        var isScored = view.HasPoints;
        var appliedBonus = isScored ? (bonus ?? 0) : 0;
        var finalPoints = view.TotalPoints + appliedBonus;
        // A breakdown ("X − Y + B = Z") is spelled out whenever there is a penalty OR a bonus to account for;
        // with neither, the line is just "Сума балів: Z".
        var hasBreakdown = isScored && (view.Penalty > 0 || appliedBonus != 0);

        return new SplitPrintDocument
        {
            FullName = row.FullName,
            Number = row.ParticipantNumber,
            ChipNumber = row.ChipNumber,
            IsRentalChip = isRental,
            GroupName = row.GroupName,
            VariantCode = view.VariantCode,
            ResultText = resultElapsed is { } re ? re.ToString("h\\:mm\\:ss") : string.Empty,
            StatusText = status,
            StatusDetail = row.StatusDetail,
            StatusDetailKind = row.StatusDetailKind,
            // Points line for any point-scoring view (rogaine is Ordered+HasPoints, score is Scored). The final
            // result (net + bonus) prints as "Сума балів: Z"; when a penalty or bonus applies the renderer
            // instead spells out the breakdown "X − Y + B = Z" from the gross/penalty/bonus values below.
            TotalPointsText = isScored
                ? finalPoints.ToString(CultureInfo.InvariantCulture)
                : string.Empty,
            // The breakdown's "X" is the gross collected before the penalty; without a penalty it equals the
            // net, which is the starting point the bonus is applied to.
            GrossPointsText = hasBreakdown
                ? (view.Penalty > 0 ? view.GrossPoints : view.TotalPoints).ToString(CultureInfo.InvariantCulture)
                : string.Empty,
            PenaltyText = view.Penalty > 0 ? view.Penalty.ToString(CultureInfo.InvariantCulture) : string.Empty,
            BonusText = appliedBonus != 0
                ? appliedBonus.ToString("+0;-0", CultureInfo.InvariantCulture)
                : string.Empty,
            StartClock = startClock,
            FinishClock = finishClock,
            TotalDistanceText = FormatDistanceMetres(totalKm),
            AvgPaceText = FormatPace(AvgPaceSecondsPerKm(resultElapsed, totalKm)),
            Place = place,
            ReadOutCount = readOutCount,
            Rows = rows,
            // Rogaine (Ordered + points) keeps the full geometry columns plus a бал column; the geometry-less
            // choice/score formats (Scored layout) use the compact layout instead.
            HasGeometry = view.Layout == SplitsLayout.Ordered && view.HasPoints,
            CorrectOrder = correctOrder
        };
    }

    // The runner's current place in their group and how many of the group have read out. The first slip (printed
    // as the chip reads out) ranks the day on the read-outs logged up to this one, in log order; a re-print counts
    // every read-out logged so far, later ones too, so a slip asked for again an hour later shows the standing
    // then. Either way this chip's own later read-outs are left out, so the place belongs to the passage printed.
    // The "з N" counts every competitor of the group with a read-out in that set — withdrawn (MP, DNF, DSQ…) and
    // поза конкурсом included — and a team (relay, rogaine) once, not per member. The discipline's own ranking
    // applies (time, score, teams).
    private async Task<(int? Place, int? ReadOutCount)> CurrentPlaceAsync(
        FinishReadoutRow row, bool reprint, CancellationToken cancellationToken)
    {
        if (!row.IsKnown || CurrentDay is null)
            return (null, null);

        var snapshot = await _eventStore.GetDaySnapshotAsync(FolderPath, CurrentDayId, cancellationToken);
        var chip = row.ChipNumber.Trim();
        var link = DistinctLinks(snapshot.Links).FirstOrDefault(l => TextKey.Same(l.Chip.Trim(), chip));
        if (link?.GroupId is not { } groupId)
            return (null, null);

        var readSoFar = snapshot with
        {
            FinishReadouts = snapshot.FinishReadouts
                .Where(r => r.Order <= row.Order || (reprint && !TextKey.Same(r.ChipNumber.Trim(), chip)))
                .ToList()
        };
        var results = await ComputeDayResultsAsync(FolderPath, CurrentDay, cancellationToken, snapshot: readSoFar);
        if (!results.TryGetValue(link.Id, out var mine) || mine.Place is not { } place)
            return (null, null);

        // A rogaine team is the participants of the group sharing a team name (the calculator's team key); a
        // relay team comes with the result.
        var settings = readSoFar.GroupDaySettings.FirstOrDefault(s => s.GroupId == groupId);
        var teamFormat = _strategies.For(settings?.DisciplineOverride ?? CurrentDayDefaultDiscipline).Competitors
            == CompetitorModel.Team;
        var teamOf = readSoFar.Participants.ToDictionary(p => p.Id, p => p.Team.Trim());

        var readOut = DistinctLinks(readSoFar.Links)
            .Where(l => l.GroupId == groupId)
            .Select(l => (Link: l, Result: results.GetValueOrDefault(l.Id)))
            .Where(x => x.Result?.HasReadout == true)
            .Select(x => x.Result!.Team is { } relayTeam ? "relay:" + relayTeam.TeamId
                : teamFormat && teamOf.TryGetValue(x.Link.ParticipantId, out var team) && team.Length > 0 ? "team:" + team
                : x.Link.Id.ToString())
            .Distinct(StringComparer.Ordinal)
            .Count();
        return (place, readOut);
    }

    // Maps the prescribed course (set-course layout) to the compact correct-order line. Each control keeps
    // its taken/missing flag so the renderer can bold the controls the runner did not visit in order.
    private static IReadOnlyList<SplitPrintCorrectRow> BuildCorrectOrderRows(IReadOnlyList<ExpectedControl> expected)
    {
        var list = new List<SplitPrintCorrectRow>(expected.Count);
        foreach (var c in expected)
            list.Add(new SplitPrintCorrectRow(c.Code, c.Taken));
        return list;
    }

    // Maps the ordered passage (set-course / rogaine layout) to printable rows. The start marker is dropped
    // (the table opens at control 1); the finish marker becomes an "F" row with a blank №. Each row carries
    // the leg distance in km, the cumulative (elapsed) and leg times and the leg pace — as the panel shows
    // them. When <paramref name="hasPoints"/> (rogaine) each scoring control prints its "+N" and the finish
    // its "−Y" over-time penalty in the бал column, so the slip reads the points in passage order.
    private static IReadOnlyList<SplitPrintRow> BuildPassageRows(IReadOnlyList<PassagePunch> passage, bool hasPoints)
    {
        var list = new List<SplitPrintRow>(passage.Count);
        foreach (var p in passage)
        {
            if (p.Kind == PassageKind.Start)
                continue;

            var code = p.Kind == PassageKind.Finish ? "F" : p.Code;
            // Points text (rogaine only): "+N" for a scoring control, "−Y" for the finish penalty (its
            // Points is already negative). Null for a non-scoring view (keeps the бал column off the slip).
            string? pointsText = null;
            if (hasPoints && p.Points is { } pts && pts != 0)
                pointsText = pts > 0 ? $"+{pts}" : pts.ToString(CultureInfo.InvariantCulture);

            list.Add(new SplitPrintRow(
                Index: p.Kind == PassageKind.Control ? p.Index.ToString(CultureInfo.InvariantCulture) : string.Empty,
                Code: code,
                // Row-to-row leg/distance/pace (set course) so the slip shows час перегону + довжина for every
                // row, extras included; falls back to the course leg for views that don't fill the display leg.
                DistanceText: FormatDistanceMetres(p.DisplayLegKm ?? p.LegKm),
                LegText: FormatDuration(p.DisplayLeg ?? p.Leg),
                ElapsedText: FormatDuration(p.Elapsed),
                PaceText: FormatPace(p.DisplayPace ?? p.PaceSecondsPerKm),
                PointsText: pointsText,
                OnCourse: p.Kind != PassageKind.Control || p.OnCourse,
                CountsForTeam: p.CountsForTeam));
        }
        return list;
    }

    // Maps the scored layout (score/choice/rogaine) to printable rows. The passage order is preserved. A
    // points value is carried (non-null) only when the discipline actually values its controls, since a
    // non-null PointsText is what turns the renderer's бал column on: «за вибором по кількості КП» scores
    // nothing, so its slip keeps the plain №ПП КП ЧАС layout.
    private static IReadOnlyList<SplitPrintRow> BuildScoredRows(IReadOnlyList<ScoreEntry> entries, bool hasPoints)
    {
        var list = new List<SplitPrintRow>(entries.Count);
        var index = 0;
        foreach (var entry in entries)
        {
            index++;
            list.Add(new SplitPrintRow(
                Index: entry.Visited ? index.ToString(CultureInfo.InvariantCulture) : string.Empty,
                Code: entry.Code,
                DistanceText: string.Empty,
                LegText: string.Empty,
                ElapsedText: FormatDuration(entry.Elapsed),
                PaceText: string.Empty,
                // Points earned at this control print in the БАЛ column ("+3"); only a visited control earns
                // them, so an unvisited allowed control shows blank (it didn't add to the total).
                PointsText: hasPoints
                    ? (entry.Visited && entry.Points != 0 ? $"+{entry.Points}" : string.Empty)
                    : null,
                OnCourse: entry.Visited));
        }
        return list;
    }

    private static string FormatDuration(TimeSpan? span) => TimeFormat.Duration(span);

    // Distance in whole metres (unit lives in the header); blank for null/zero. Used for both the per-leg
    // column and the course total on the header line.
    private static string FormatDistanceMetres(decimal? km) => SplitPrintFormat.DistanceMetres(km);

    // Pace "m:ss" from seconds-per-km (unit /км lives in the header); blank for null/non-positive.
    private static string FormatPace(double? secondsPerKm) => SplitPrintFormat.Pace(secondsPerKm);

    // Average pace (seconds per km) over the whole result: result time ÷ total course distance.
    private static double? AvgPaceSecondsPerKm(TimeSpan? result, decimal totalKm) =>
        SplitPrintFormat.SecondsPerKm(result, totalKm);

    // Judges one read-out against its holder's group (see FinishEvaluator) and returns the status parts plus
    // the resolved start so the caller can derive the finish − start elapsed duration.
    private (FinishStatus Status, string Detail, FinishDetailKind DetailKind, DateTimeOffset? ResolvedStartTime) EvaluateFinish(
        FinishReadout readout,
        ParticipantDay link,
        IReadOnlyDictionary<Guid, GroupDaySettings> settingsByGroup,
        HashSet<string> startFinishCodes,
        HashSet<string> disabledCodes,
        DisciplineId dayDefault,
        IReadOnlyDictionary<Guid, IReadOnlyList<CourseVariantData>> variantsByGroup,
        Func<IDisciplineStrategy, SettingValues>? settingsOf = null)
    {
        var (result, start) = FinishEvaluator.Evaluate(
            _strategies, readout, link, settingsByGroup, startFinishCodes, disabledCodes, dayDefault, variantsByGroup,
            settingsOf);
        return (result.Status, result.Detail, result.DetailKind, start);
    }

    // Loads the day's scatter («розсіювання») variants, keyed by group id, ready for the discipline strategy
    // (see FinishEvaluator.BuildCourseVariants). Empty for a day with no variant group, so the other paths
    // pay no query.
    private async Task<Dictionary<Guid, IReadOnlyList<CourseVariantData>>> LoadCourseVariantsAsync(
        string folder, Guid dayId,
        IReadOnlyDictionary<Guid, GroupDaySettings> settingsByGroup,
        DisciplineId dayDefault,
        IReadOnlySet<string> startFinishCodes,
        IReadOnlySet<string> disabledCodes,
        CancellationToken cancellationToken)
    {
        if (!FinishEvaluator.AnyUsesCourseVariants(_strategies, settingsByGroup, dayDefault))
            return [];

        var rows = await _eventStore.GetCourseVariantsAsync(folder, dayId, cancellationToken);
        return FinishEvaluator.BuildCourseVariants(
            _strategies, rows, settingsByGroup, dayDefault, startFinishCodes, disabledCodes);
    }

    public async Task<FinishReadoutImportResult> ImportFinishReadoutsAsync(ChipReadData data, CancellationToken cancellationToken = default, Guid? forDayId = null)
    {
        var (knownDay, otherDay) = await ResolveDayAsync(forDayId, cancellationToken);
        if (!knownDay)
            return default;
        if (otherDay is not null)
            _dayOverride.Value = otherDay;
        ArgumentNullException.ThrowIfNull(data);
        if (_session.CurrentEvent is null || CurrentDay is null)
            return default;

        var folder = FolderPath;
        var dayId = CurrentDayId;
        await EnsureDayEditableAsync(dayId, cancellationToken);

        var existing = await _eventStore.GetFinishReadoutsAsync(folder, dayId, cancellationToken);

        // Seed the dedup set with what's already logged (content keys), and continue the sequence. The
        // map lets a skipped record name the row it repeats, so the caller can re-print that slip.
        var loggedByKey = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var row in existing)
            loggedByKey.TryAdd(row.ContentKey, row.Id);
        var seen = new HashSet<string>(loggedByKey.Keys, StringComparer.Ordinal);
        var nextOrder = existing.Count == 0 ? 1 : existing.Max(r => r.Order) + 1;

        var toAdd = new List<FinishReadout>();
        var addedMarks = new List<string>();
        var duplicates = new List<FinishReadoutDuplicate>();
        var skipped = 0;
        foreach (var record in data.Records)
        {
            var chip = record.ChipNumber.Trim();
            if (chip.Length == 0)
                continue;

            var key = ContentKeyFor(record);
            // Skip rows already logged (identical content) — re-reading the same file is idempotent.
            // Two identical rows within one file collapse to one for the same reason.
            if (!seen.Add(key))
            {
                skipped++;
                // Report it against the logged row it repeats (a within-file twin has no row of its own).
                // The read mark rides along: two physical read-outs of the same chip produce the same
                // content key but different marks, which is what tells a repeat read from a re-read file.
                if (loggedByKey.TryGetValue(key, out var loggedId))
                    duplicates.Add(new FinishReadoutDuplicate(loggedId, key, record.ReadMark));
                continue;
            }

            toAdd.Add(new FinishReadout
            {
                EventDayId = dayId,
                Order = nextOrder++,
                ChipNumber = chip,
                StartTime = record.StartTime,
                FinishTime = record.FinishTime,
                Punches = string.Join(' ', record.Punches.Select(p => p.ControlCode.Trim()).Where(c => c.Length > 0)),
                PunchTimes = EncodePunchTimes(record.Punches),
                ContentKey = key
            });
            addedMarks.Add(record.ReadMark);
        }

        await _eventStore.AddFinishReadoutsAsync(folder, toAdd, cancellationToken);

        // A participant physically finished (their chip was read at the finish), so a DNS ("did not
        // start") judge override on them for this day is now contradicted by reality — clear it and let
        // automatic evaluation take over. Any other manual status (DSQ, OVT, a forced OK, …) is left
        // untouched; only DNS is undone by the act of finishing. Keyed on the chips actually added this
        // pass, resolved to the day's members the same way the log resolution does.
        if (toAdd.Count > 0)
        {
            var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
            var addedChips = new HashSet<string>(
                toAdd.Select(r => r.ChipNumber.Trim()), TextKey.Comparer);
            foreach (var link in links)
            {
                if (link.ResultStatusOverride != FinishStatus.Dns)
                    continue;
                if (link.Chip.Trim().Length == 0 || !addedChips.Contains(link.Chip.Trim()))
                    continue;
                await _eventStore.SetParticipantDayResultStatusAsync(folder, link.Id, null, cancellationToken);
            }
        }

        return new FinishReadoutImportResult(
            Added: toAdd.Count,
            Skipped: skipped,
            AddedIds: toAdd.Select(r => r.Id).ToList(),
            Duplicates: duplicates,
            AddedReads: toAdd.Select((r, i) => new FinishReadoutDuplicate(r.Id, r.ContentKey, addedMarks[i])).ToList());
    }

    public async Task<int> ClearFinishReadoutsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || CurrentDay is null)
            return 0;

        var dayId = CurrentDayId;
        await EnsureDayEditableAsync(dayId, cancellationToken);
        return await _eventStore.DeleteFinishReadoutsForDayAsync(FolderPath, dayId, cancellationToken);
    }

    public async Task<FinishReadoutEditData?> GetFinishReadoutEditAsync(Guid readoutId, CancellationToken cancellationToken = default, Guid? forDayId = null)
    {
        var (knownDay, otherDay) = await ResolveDayAsync(forDayId, cancellationToken);
        if (!knownDay)
            return null;
        if (otherDay is not null)
            _dayOverride.Value = otherDay;
        if (_session.CurrentEvent is null || CurrentDay is null)
            return null;

        var folder = FolderPath;
        var dayId = CurrentDayId;

        var readouts = await _eventStore.GetFinishReadoutsAsync(folder, dayId, cancellationToken);
        var readout = readouts.FirstOrDefault(r => r.Id == readoutId);
        if (readout is null)
            return null;

        // The effective status is the manual override when set, else the discipline's computed status —
        // the modal opens on whatever is currently shown in the log.
        var row = (await GetFinishReadoutRowsAsync(cancellationToken)).FirstOrDefault(r => r.Id == readoutId);
        var effectiveStatus = row?.Status ?? FinishStatus.None;

        // The day's members, each a reassign target (bib + ПІБ + group), and the chip's current holder so
        // the dropdown opens on them. Built from the same participant/group lookups the log resolution uses.
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var groups = await _eventStore.GetGroupsAsync(folder, cancellationToken);
        var byId = participants.ToDictionary(p => p.Id);
        var groupName = groups.ToDictionary(g => g.Id, g => g.Name);

        // A relay runner is offered under their leg number «100.2» (Р-24), so the judge picks the team and leg of
        // an unknown chip by typing it.
        var teamNumbers = (await _eventStore.GetRelayTeamsAsync(folder, dayId, cancellationToken)).ToDictionary(t => t.Id, t => t.Number);
        var options = new List<FinishReadoutParticipantOption>(links.Count);
        foreach (var link in links)
        {
            if (!byId.TryGetValue(link.ParticipantId, out var p))
                continue;
            var group = link.GroupId is { } gid && groupName.TryGetValue(gid, out var gn) ? gn : string.Empty;
            var number = link.RelayTeamId is { } tid && link.Leg is { } leg && teamNumbers.TryGetValue(tid, out var tn)
                         && RelayTeamNumbers.Bib(tn, leg) is { Length: > 0 } bib
                ? bib
                : p.Number;
            options.Add(new FinishReadoutParticipantOption(p.Id, number, p.FullName, group, link.GroupId, link.StartTime));
        }
        options = options
            .OrderBy(o => o.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var holder = links.FirstOrDefault(l =>
            TextKey.Same(l.Chip.Trim(), readout.ChipNumber.Trim()));

        // Lookups for the modal's "create a new participant" form: the day's groups (the one mandatory
        // field besides ПІБ) plus the competition's regions / clubs / ДЮСШ and the app-level rank names.
        var dayGroups = await GetGroupsForDayAsync(dayId, cancellationToken);
        var regions = await _eventStore.GetRegionsAsync(folder, cancellationToken);
        var clubs = await _eventStore.GetClubsAsync(folder, cancellationToken);
        var dusshes = await _eventStore.GetDusshesAsync(folder, cancellationToken);
        var ranks = await _resultsAppData.GetRanksAsync(cancellationToken);

        return new FinishReadoutEditData
        {
            Id = readout.Id,
            ChipNumber = readout.ChipNumber,
            StartTime = readout.StartTime,
            FinishTime = readout.FinishTime,
            Punches = DecodePunchTimes(readout.PunchTimes),
            Status = effectiveStatus,
            HasManualStatus = readout.ManualStatus is not null,
            Participants = options,
            CurrentHolderId = holder?.ParticipantId,
            Groups = dayGroups
                .Select(g => new FinishReadoutLookupOption(g.GroupId, g.Name))
                .ToList(),
            Regions = regions
                .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(r => new FinishReadoutLookupOption(r.Id, r.Name))
                .ToList(),
            Clubs = clubs
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(c => new FinishReadoutLookupOption(c.Id, c.Name))
                .ToList(),
            Dusshes = dusshes
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(d => new FinishReadoutLookupOption(d.Id, d.Name))
                .ToList(),
            Ranks = ranks
                .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                .Select(r => r.Name)
                .ToList(),
            // Every bib already handed out in the competition (numbers are competition-level), so the
            // new-participant form can flag a duplicate as it is typed and name who holds it.
            TakenNumbers = participants
                .Where(p => p.Number.Trim().Length > 0)
                .GroupBy(p => p.Number.Trim(), TextKey.Comparer)
                .ToDictionary(g => g.Key, g => g.First().FullName, TextKey.Comparer)
        };
    }

    public async Task UpdateFinishReadoutAsync(FinishReadoutEdit edit, CancellationToken cancellationToken = default, Guid? forDayId = null)
    {
        var (knownDay, otherDay) = await ResolveDayAsync(forDayId, cancellationToken);
        if (!knownDay)
            return;
        if (otherDay is not null)
            _dayOverride.Value = otherDay;
        ArgumentNullException.ThrowIfNull(edit);
        if (_session.CurrentEvent is null || CurrentDay is null)
            return;

        var folder = FolderPath;
        var dayId = CurrentDayId;
        await EnsureDayEditableAsync(dayId, cancellationToken);

        var readouts = await _eventStore.GetFinishReadoutsAsync(folder, dayId, cancellationToken);
        var readout = readouts.FirstOrDefault(r => r.Id == edit.Id);
        if (readout is null)
            return;

        readout.ChipNumber = (edit.ChipNumber ?? string.Empty).Trim();
        readout.StartTime = edit.StartTime;
        readout.FinishTime = edit.FinishTime;
        readout.Punches = string.Join(' ', edit.Punches.Select(p => p.ControlCode.Trim()).Where(c => c.Length > 0));
        readout.PunchTimes = EncodePunchTimes(edit.Punches);
        readout.ManualStatus = edit.ManualStatus;
        await _eventStore.UpdateFinishReadoutAsync(folder, readout, cancellationToken);

        // The operator filled in the "new participant" form: create the competitor, add them to the day in
        // the chosen group, and treat them as the reassign target below — so the chip lands on them exactly
        // like on an existing runner.
        var targetParticipantId = edit.ReassignToParticipantId;
        if (edit.NewParticipant is { } newcomer)
            targetParticipantId = await CreateParticipantForDayAsync(folder, dayId, newcomer, cancellationToken);

        // (Re)assign this read-out's chip to the chosen participant on the day, taking it from any previous
        // holder so the chip stays unique per day. The caller has already confirmed the reassignment.
        if (targetParticipantId is { } targetId)
            await ReassignParticipantDayChipAsync(targetId, dayId, readout.ChipNumber, cancellationToken);

        // Propagate the chosen status onto the participant who now holds this chip on the day, so the manual
        // status overrides that runner's day result — not just the log row. Going through
        // SetParticipantDayResultStatusAsync means an "OK" recomputes their place and gap (via
        // ComputeDayResultsAsync' ResultStatusOverride) immediately, and a null clears the override back to
        // the computed status. Resolved AFTER any reassignment so it lands on the final holder.
        var holders = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var holder = holders.FirstOrDefault(l =>
            TextKey.Same(l.Chip.Trim(), readout.ChipNumber));
        if (holder is null)
            return;

        // The operator picked another group for the holder (a runner who ran the wrong course / was entered
        // in the wrong group): move them on this day only. A new participant already got the form's group.
        if (edit.NewParticipant is null && edit.GroupId is { } groupId && holder.GroupId != groupId)
            await _eventStore.SetParticipantDayGroupAsync(folder, holder.ParticipantId, dayId, groupId, cancellationToken);

        await SetParticipantDayResultStatusAsync(holder.ParticipantId, dayId, edit.ManualStatus, cancellationToken);
    }

    // Creates a competitor from the unknown-chip modal's "new participant" form and adds them to the day
    // in the chosen group. Only ПІБ + group are required; the rest are the optional identity fields that
    // show up in the protocols. A bib number already taken by someone else is dropped (left blank) rather
    // than duplicated — same rule as the participant grid. Returns the new participant's id.
    private async Task<Guid> CreateParticipantForDayAsync(
        string folder,
        Guid dayId,
        NewParticipantData data,
        CancellationToken cancellationToken)
    {
        var existing = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var number = (data.Number ?? string.Empty).Trim();
        if (number.Length > 0 && existing.Any(p =>
                TextKey.Same(p.Number.Trim(), number)))
            number = string.Empty;

        var participant = new Participant
        {
            FullName = (data.FullName ?? string.Empty).Trim(),
            Number = number,
            Rank = (data.Rank ?? string.Empty).Trim(),
            Coach = (data.Coach ?? string.Empty).Trim(),
            BirthDate = data.BirthDate,
            RegionId = data.RegionId,
            ClubId = data.ClubId,
            DusshId = data.DusshId
        };
        await _eventStore.AddParticipantAsync(folder, participant, cancellationToken);

        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        await _eventStore.AddParticipantDayAsync(folder, new ParticipantDay
        {
            EventDayId = dayId,
            ParticipantId = participant.Id,
            Order = links.Count == 0 ? 1 : links.Max(l => l.Order) + 1,
            GroupId = data.GroupId
        }, cancellationToken);

        return participant.Id;
    }

    // Stable signature of a read-out record's content, so re-reading the same physical row is detected
    // as already-logged while genuinely different reads of the same chip remain distinct.
    private static string ContentKeyFor(ChipReadRecord record)
    {
        return string.Join("|",
            record.ChipNumber.Trim(),
            record.StartTime?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "-",
            record.FinishTime?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "-",
            EncodePunchTimes(record.Punches));
    }

    public async Task<string?> FindChipHolderAsync(Guid dayId, string chip, Guid excludeParticipantId, CancellationToken cancellationToken = default)
    {
        var trimmed = (chip ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return null;

        var folder = FolderPath;
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var holder = links.FirstOrDefault(l =>
            l.ParticipantId != excludeParticipantId &&
            TextKey.Same(l.Chip.Trim(), trimmed));
        if (holder is null)
            return null;

        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var name = participants.FirstOrDefault(p => p.Id == holder.ParticipantId)?.FullName ?? string.Empty;
        return name;
    }

    public async Task<Guid?> ReassignParticipantDayChipAsync(Guid participantId, Guid dayId, string chip, CancellationToken cancellationToken = default)
    {
        await EnsureDayEditableAsync(dayId, cancellationToken);
        var folder = FolderPath;
        var trimmed = (chip ?? string.Empty).Trim();

        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var target = links.FirstOrDefault(l => l.ParticipantId == participantId);
        // The chip is per-day and only meaningful for a member; nothing to do otherwise.
        if (target is null)
            return null;

        // Take the chip away from any OTHER participant who holds it on this day, so it ends up unique.
        Guid? previousHolder = null;
        if (trimmed.Length > 0)
        {
            var other = links.FirstOrDefault(l =>
                l.Id != target.Id &&
                TextKey.Same(l.Chip.Trim(), trimmed));
            if (other is not null)
            {
                await _eventStore.UpdateParticipantDayAsync(folder, new ParticipantDay
                {
                    Id = other.Id,
                    EventDayId = dayId,
                    ParticipantId = other.ParticipantId,
                    Order = other.Order,
                    GroupId = other.GroupId,
                    Chip = string.Empty,
                    StartTime = other.StartTime,
                    OutOfCompetition = other.OutOfCompetition
                }, cancellationToken);
                previousHolder = other.ParticipantId;
            }
        }

        await _eventStore.UpdateParticipantDayAsync(folder, new ParticipantDay
        {
            Id = target.Id,
            EventDayId = dayId,
            ParticipantId = participantId,
            Order = target.Order,
            GroupId = target.GroupId,
            Chip = trimmed,
            StartTime = target.StartTime,
            OutOfCompetition = target.OutOfCompetition
        }, cancellationToken);

        return previousHolder;
    }

    public async Task<Guid?> SwapParticipantDayChipsAsync(Guid participantId, Guid dayId, string chip, string previousChip, CancellationToken cancellationToken = default)
    {
        await EnsureDayEditableAsync(dayId, cancellationToken);
        var folder = FolderPath;
        var trimmed = (chip ?? string.Empty).Trim();
        var handBack = (previousChip ?? string.Empty).Trim();
        if (trimmed.Length == 0 || handBack.Length == 0)
            return null;

        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var target = links.FirstOrDefault(l => l.ParticipantId == participantId);
        if (target is null)
            return null;

        var other = links.FirstOrDefault(l =>
            l.Id != target.Id &&
            TextKey.Same(l.Chip.Trim(), trimmed));
        if (other is null)
            return null;

        // Give the other holder the chip this participant is giving up, then take theirs. Order does
        // not matter here — both rows are written explicitly, so no intermediate state is persisted.
        await _eventStore.UpdateParticipantDayAsync(folder, WithChip(other, dayId, handBack), cancellationToken);
        await _eventStore.UpdateParticipantDayAsync(folder, WithChip(target, dayId, trimmed), cancellationToken);
        return other.ParticipantId;
    }

    private static ParticipantDay WithChip(ParticipantDay source, Guid dayId, string chip) => new()
    {
        Id = source.Id,
        EventDayId = dayId,
        ParticipantId = source.ParticipantId,
        Order = source.Order,
        GroupId = source.GroupId,
        Chip = chip,
        StartTime = source.StartTime,
        OutOfCompetition = source.OutOfCompetition
    };

    public async Task<bool> ToggleRentalChipAsync(string number, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return false;

        var trimmed = (number ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return false;

        var folder = FolderPath;
        var chips = await _eventStore.GetRentalChipsAsync(folder, cancellationToken);
        var existing = chips.FirstOrDefault(c =>
            TextKey.Same(c.Number.Trim(), trimmed));

        if (existing is not null)
        {
            await _eventStore.DeleteRentalChipAsync(folder, existing.Id, cancellationToken);
            return false;
        }

        await _eventStore.AddRentalChipAsync(folder, new RentalChip { Number = trimmed }, cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<GroupDayRow>> GetGroupsForDayAsync(Guid dayId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return [];

        var folder = FolderPath;
        var settings = await _eventStore.GetGroupDaySettingsAsync(folder, dayId, cancellationToken);
        var groups = await _eventStore.GetGroupsAsync(folder, cancellationToken);
        var byId = groups.ToDictionary(g => g.Id);

        var rows = new List<GroupDayRow>(settings.Count);
        foreach (var s in settings)
        {
            if (byId.TryGetValue(s.GroupId, out var group))
                rows.Add(ToRow(s, group));
        }
        return rows;
    }

    public async Task UpdateParticipantIdentityAsync(ParticipantRosterRow row, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        var folder = FolderPath;
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);

        // A number colliding with a different participant is dropped, keeping the stored number.
        var number = (row.Number ?? string.Empty).Trim();
        var collides = number.Length > 0 && participants.Any(p =>
            p.Id != row.ParticipantId && TextKey.Same(p.Number.Trim(), number));
        if (collides)
            number = participants.FirstOrDefault(p => p.Id == row.ParticipantId)?.Number ?? number;

        await _eventStore.UpdateParticipantAsync(folder, new Participant
        {
            Id = row.ParticipantId,
            FullName = (row.FullName ?? string.Empty).Trim(),
            Number = number,
            Rank = (row.Rank ?? string.Empty).Trim(),
            Coach = (row.Coach ?? string.Empty).Trim(),
            BirthDate = row.BirthDate,
            RegionId = row.RegionId,
            ClubId = row.ClubId,
            DusshId = row.DusshId,
            Representative = (row.Representative ?? string.Empty).Trim(),
            FsouCode = (row.FsouCode ?? string.Empty).Trim(),
            IsFsouMember = row.IsFsouMember,
            Payment = (row.Payment ?? string.Empty).Trim(),
            Note = (row.Note ?? string.Empty).Trim(),
            Team = (row.Team ?? string.Empty).Trim()
        }, cancellationToken);
    }

    public async Task<ParticipantImportResult> ImportParticipantsAsync(
        UofParticipantData data,
        bool clearFirst,
        ParticipantImportScope scope,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(scope);
        if (_session.CurrentEvent is null)
            return default;

        // An import rewrites the roster of whole days at once, so every day it touches must be open:
        // the current one when importing into a single day, all existing ones otherwise.
        if (scope.Mode == ParticipantImportMode.AllDays)
        {
            foreach (var existingDay in await _eventStore.GetDaysAsync(FolderPath, cancellationToken))
            {
                if (existingDay.IsLocked)
                    throw new DayLockedException(existingDay.Number);
            }
        }
        else
        {
            await EnsureDayEditableAsync(CurrentDayId, cancellationToken);
        }

        // Current-day-only import targets a single existing day — never create days from the file's info.
        var daysCreated = 0;
        if (scope.Mode == ParticipantImportMode.AllDays)
        {
            // Ensure every day referenced by a ProgEvent exists; create the missing ones (numbered up to
            // the highest reference) here, because AddDayAsync also makes each day's files folder (I/O).
            // The actual roster write then happens in one transaction inside the store (fast for big files).
            var maxDay = data.Participants
                .SelectMany(p => p.DayNumbers)
                .DefaultIfEmpty(0)
                .Max();
            var days = await _eventStore.GetDaysAsync(FolderPath, cancellationToken);
            var highestExisting = days.Count == 0 ? 0 : days.Max(d => d.Number);
            for (var n = highestExisting + 1; n <= maxDay; n++)
            {
                var day = await AddDayAsync(cancellationToken);
                daysCreated++;
                // A new day every runner of the file runs as their relay («3Е») is a relay day.
                var runners = data.Participants.Where(p => p.DayNumbers.Contains(day.Number)).ToList();
                if (runners.Count > 0 && runners.All(p => p.RelayDayNumbers.Contains(day.Number)))
                {
                    day.DefaultDiscipline = DisciplineId.Relay;
                    await _eventStore.UpdateDayAsync(FolderPath, day, cancellationToken);
                }
            }
        }

        // The days that run a relay group: a numbered row's team/leg is applied there (Р-20).
        var relayDays = data.Participants.Any(p => p.RelayTeamNumber is not null)
            ? await RelayDayNumbersAsync(cancellationToken)
            : [];
        var relayGroupsToMark = await RelayGroupsToMarkAsync(data, scope, cancellationToken);

        return await _eventStore.ImportParticipantsBatchAsync(
            FolderPath, data, clearFirst, daysCreated, scope, progress, cancellationToken, relayDays, relayGroupsToMark);
    }

    // The relay groups of the file's relay days («3Е» + GrupRel) that run no relay yet — neither the day's default
    // discipline nor the group's own on the day is a relay one: the import makes them relay groups.
    private async Task<List<RelayGroupDay>> RelayGroupsToMarkAsync(
        UofParticipantData data, ParticipantImportScope scope, CancellationToken cancellationToken)
    {
        var wanted = data.Participants
            .SelectMany(p => p.RelayDayNumbers
                .Where(d => scope.Mode == ParticipantImportMode.AllDays || d == scope.TargetDayNumber)
                .Select(d => (Day: d, Group: p.RelayGroup.Trim())))
            .Where(x => x.Group.Length > 0)
            .DistinctBy(x => (x.Day, TextKey.Of(x.Group)))
            .ToList();
        if (wanted.Count == 0)
            return [];

        var folder = FolderPath;
        var days = (await _eventStore.GetDaysAsync(folder, cancellationToken)).ToDictionary(d => d.Number);
        var groups = await _eventStore.GetGroupsAsync(folder, cancellationToken);
        var settingsByDay = new Dictionary<Guid, IReadOnlyList<GroupDaySettings>>();
        var marks = new List<RelayGroupDay>();
        foreach (var (dayNumber, groupName) in wanted)
        {
            if (!days.TryGetValue(dayNumber, out var day) || IsRelay(day.DefaultDiscipline))
                continue;
            if (!settingsByDay.TryGetValue(day.Id, out var settings))
                settingsByDay[day.Id] = settings = await _eventStore.GetGroupDaySettingsAsync(folder, day.Id, cancellationToken);
            var group = groups.FirstOrDefault(g => TextKey.Same(g.Name, groupName));
            var own = group is null ? null : settings.FirstOrDefault(s => s.GroupId == group.Id);
            if (own?.DisciplineOverride is { } overridden && IsRelay(overridden))
                continue;
            marks.Add(new RelayGroupDay(dayNumber, groupName));
        }
        return marks;
    }

    public async Task<ImportPreview> PreviewParticipantImportAsync(
        UofParticipantData data,
        ParticipantImportScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(scope);
        if (_session.CurrentEvent is null || scope.Mode != ParticipantImportMode.AllDays)
            return ImportPreview.Empty;

        var folder = FolderPath;
        var days = (await _eventStore.GetDaysAsync(folder, cancellationToken)).OrderBy(d => d.Number).ToList();
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var links = await _eventStore.GetAllParticipantDaysAsync(folder, cancellationToken);
        var groups = (await _eventStore.GetGroupsAsync(folder, cancellationToken)).ToDictionary(g => g.Id, g => g.Name);
        var clubs = (await _eventStore.GetClubsAsync(folder, cancellationToken)).ToDictionary(c => c.Id, c => c.Name);
        var regions = (await _eventStore.GetRegionsAsync(folder, cancellationToken)).ToDictionary(r => r.Id, r => r.Name);
        var dusshes = (await _eventStore.GetDusshesAsync(folder, cancellationToken)).ToDictionary(d => d.Id, d => d.Name);
        var fieldDefs = (await _eventStore.GetCustomFieldDefsAsync(folder, cancellationToken))
            .ToDictionary(d => d.Key, CustomField.From);
        var fieldValues = (await _eventStore.GetParticipantFieldValuesAsync(folder, cancellationToken))
            .ToDictionary(v => (v.ParticipantId, v.Key), v => v.Value);
        var dayById = days.ToDictionary(d => d.Id);
        var allDayNumbers = days.Select(d => d.Number).ToList();
        var linksByParticipant = links.ToLookup(l => l.ParticipantId);

        // The import matches an existing athlete by FOU code only (all-days mode).
        var byCode = participants
            .Where(p => !string.IsNullOrWhiteSpace(p.FsouCode))
            .GroupBy(p => p.FsouCode.Trim(), TextKey.Comparer)
            .ToDictionary(g => g.Key, g => g.First(), TextKey.Comparer);

        static string Name<T>(Guid? id, Dictionary<Guid, string> names, Func<T, string>? _ = null) =>
            id is { } key && names.TryGetValue(key, out var n) ? n.Trim() : string.Empty;
        static bool Differs(string stored, string incoming) => !TextKey.Same(stored.Trim(), incoming.Trim());

        var added = new List<ImportPreviewAdded>();
        var updated = new List<ImportPreviewUpdated>();
        var seen = new HashSet<Guid>();
        for (var i = 0; i < data.Participants.Count; i++)
        {
            var src = data.Participants[i];
            var rowDays = src.DayNumbers.Count > 0 ? src.DayNumbers : allDayNumbers;
            var code = src.FsouCode.Trim();
            if (code.Length == 0 || !byCode.TryGetValue(code, out var existing))
            {
                added.Add(new ImportPreviewAdded(i, src.FullName.Trim(), src.Group.Trim(), src.BirthDate?.Year,
                    src.Club.Trim(), src.Region.Trim(), rowDays));
                continue;
            }

            seen.Add(existing.Id);
            var changes = new List<ImportPreviewChange>();
            void Change(string field, string before, string after, int? day = null)
            {
                if (Differs(before, after))
                    changes.Add(new ImportPreviewChange(field, day, before.Trim(), after.Trim()));
            }

            // Participant-level fields, with the import's own rules (EventStore.ImportParticipants): a field changes
            // only when the file has a value for it — a blank never clears a stored value; membership can only be set.
            Change("FullName", existing.FullName, src.FullName);
            if (src.Number.Trim() is { Length: > 0 } and not "0") Change("Number", existing.Number, src.Number);
            if (src.Team.Trim().Length > 0) Change("Team", existing.Team, src.Team);
            if (src.Rank.Trim().Length > 0) Change("Rank", existing.Rank, src.Rank);
            if (src.Coach.Trim().Length > 0) Change("Coach", existing.Coach, src.Coach);
            if (src.BirthDate is not null && existing.BirthDate?.Date != src.BirthDate?.Date)
                changes.Add(new ImportPreviewChange("BirthDate", null,
                    existing.BirthDate?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? string.Empty,
                    src.BirthDate?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? string.Empty));
            if (src.Region.Trim().Length > 0) Change("Region", Name<Region>(existing.RegionId, regions), src.Region);
            if (src.Club.Trim().Length > 0) Change("Club", Name<Club>(existing.ClubId, clubs), src.Club);
            if (src.Dussh.Trim().Length > 0) Change("Dussh", Name<Dussh>(existing.DusshId, dusshes), src.Dussh);
            if (src.Representative.Trim().Length > 0) Change("Representative", existing.Representative, src.Representative);
            if (src.IsFsouMember && !existing.IsFsouMember)
                changes.Add(new ImportPreviewChange("IsFsouMember", null, "−", "+"));
            if (src.Payment.Trim().Length > 0) Change("Payment", existing.Payment, src.Payment);

            // Additional fields: a blank cell never clears a stored value.
            foreach (var (key, raw) in src.Fields)
            {
                if (!fieldDefs.TryGetValue(key, out var def))
                    continue;
                var value = def.Normalize(raw);
                if (value.Length == 0)
                    continue;
                var stored = fieldValues.TryGetValue((existing.Id, key), out var v) ? v : string.Empty;
                Change(CustomField.ColumnPrefix + key, stored, value);
            }

            // Per day: the group always follows the file; the chip only when the file has one; a day the athlete
            // is not on yet is a new entry.
            var mine = linksByParticipant[existing.Id].ToList();
            foreach (var dayNumber in rowDays)
            {
                var link = mine.FirstOrDefault(l => dayById.TryGetValue(l.EventDayId, out var d) && d.Number == dayNumber);
                if (link is null)
                {
                    changes.Add(new ImportPreviewChange("Day", dayNumber, string.Empty, string.Empty));
                    continue;
                }
                Change("Group", Name<Group>(link.GroupId, groups), src.GroupOn(dayNumber), dayNumber);
                var chip = src.Chip.Trim();
                if (chip.Length > 0 && chip != "0")
                    Change("Chip", link.Chip, chip, dayNumber);
            }

            updated.Add(new ImportPreviewUpdated(i, existing.Id, src.FullName.Trim(), src.Group.Trim(), changes));
        }

        var missing = participants
            .Where(p => !seen.Contains(p.Id))
            .Select(p =>
            {
                var mine = linksByParticipant[p.Id]
                    .Where(l => dayById.ContainsKey(l.EventDayId))
                    .OrderBy(l => dayById[l.EventDayId].Number)
                    .ToList();
                var groupNames = mine
                    .Select(l => Name<Group>(l.GroupId, groups))
                    .Where(g => g.Length > 0)
                    .Distinct(TextKey.Comparer)
                    .ToList();
                return new ImportPreviewMissing(p.Id, p.FullName.Trim(), string.Join(" / ", groupNames), p.BirthDate?.Year,
                    Name<Club>(p.ClubId, clubs), Name<Region>(p.RegionId, regions),
                    mine.Select(l => dayById[l.EventDayId].Number).ToList());
            })
            .OrderBy(m => m.FullName, StringComparer.CurrentCulture)
            .ToList();

        return new ImportPreview(added, updated, missing);
    }

    public async Task<IReadOnlyList<ImportDuplicateCase>> FindImportDuplicatesAsync(
        UofParticipantData data,
        bool clearFirst,
        ParticipantImportScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(scope);
        if (_session.CurrentEvent is null || scope.Mode != ParticipantImportMode.CurrentDayOnly)
            return [];

        var folder = FolderPath;
        var days = await _eventStore.GetDaysAsync(folder, cancellationToken);
        var targetDay = days.FirstOrDefault(d => d.Number == scope.TargetDayNumber);
        if (targetDay is null)
            return [];

        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var links = await _eventStore.GetAllParticipantDaysAsync(folder, cancellationToken);
        var groups = await _eventStore.GetGroupsAsync(folder, cancellationToken);
        var clubs = (await _eventStore.GetClubsAsync(folder, cancellationToken)).ToDictionary(c => c.Id, c => c.Name);
        var regions = (await _eventStore.GetRegionsAsync(folder, cancellationToken)).ToDictionary(r => r.Id, r => r.Name);
        var dayNumberById = days.ToDictionary(d => d.Id, d => d.Number);

        var byName = participants
            .Where(p => ParticipantNameKey.Of(p.FullName).Length > 0)
            .ToLookup(p => ParticipantNameKey.Of(p.FullName), TextKey.Comparer);
        var byCode = participants
            .Where(p => !string.IsNullOrWhiteSpace(p.FsouCode))
            .GroupBy(p => p.FsouCode.Trim(), TextKey.Comparer)
            .ToDictionary(g => g.Key, g => g.First(), TextKey.Comparer);
        var linksByParticipant = links.ToLookup(l => l.ParticipantId);
        var matchByName = scope.LinkField == ParticipantLinkField.FullName;

        // Which rows the link field already attaches to an existing athlete (mirrors the store's matching),
        // and which athletes are taken that way — those are never offered as a merge target for another row.
        var matchedRows = new HashSet<int>();
        var claimed = new HashSet<Guid>();
        for (var i = 0; i < data.Participants.Count; i++)
        {
            var src = data.Participants[i];
            Participant? match = null;
            if (matchByName)
                match = byName[ParticipantNameKey.Of(src.FullName)].FirstOrDefault();
            else if (src.FsouCode.Trim() is { Length: > 0 } code)
                match = byCode.GetValueOrDefault(code);
            if (match is null)
                continue;
            matchedRows.Add(i);
            claimed.Add(match.Id);
        }

        var cases = new List<ImportDuplicateCase>();
        for (var i = 0; i < data.Participants.Count; i++)
        {
            if (matchedRows.Contains(i))
                continue;
            var src = data.Participants[i];
            var group = groups.FirstOrDefault(g =>
                TextKey.Same(g.Name, src.Group.Trim()));
            var nameKey = ParticipantNameKey.Of(src.FullName);
            if (group is null || nameKey.Length == 0)
                continue; // a group the competition doesn't have yet can't have been run before

            var candidates = new List<ImportDuplicateCandidate>();
            foreach (var p in byName[nameKey])
            {
                if (claimed.Contains(p.Id))
                    continue;
                // Other days count always; the target day only when this import keeps its current roster.
                var dayNumbers = linksByParticipant[p.Id]
                    .Where(l => l.GroupId == group.Id && (l.EventDayId != targetDay.Id || !clearFirst))
                    .Select(l => dayNumberById.GetValueOrDefault(l.EventDayId))
                    .Where(n => n > 0)
                    .Distinct()
                    .Order()
                    .ToList();
                if (dayNumbers.Count == 0)
                    continue;
                candidates.Add(new ImportDuplicateCandidate(
                    p.Id,
                    p.FullName,
                    p.BirthDate?.Year,
                    p.ClubId is { } c ? clubs.GetValueOrDefault(c, string.Empty) : string.Empty,
                    p.RegionId is { } r ? regions.GetValueOrDefault(r, string.Empty) : string.Empty,
                    p.FsouCode.Trim(),
                    dayNumbers));
            }

            if (candidates.Count > 0)
            {
                cases.Add(new ImportDuplicateCase(i,
                    new ImportDuplicateRow(src.FullName.Trim(), group.Name, src.BirthDate?.Year,
                        src.Club.Trim(), src.Region.Trim(), src.FsouCode.Trim()),
                    candidates));
            }
        }

        return cases;
    }

    // ── Discipline (plugin) settings (Р-11)

    public async Task<DisciplineSettingsData?> GetDisciplineSettingsAsync(
        Guid dayId, DisciplineId disciplineId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return null;

        var strategy = _strategies.For(disciplineId);
        if (strategy.Settings.Count == 0)
            return new DisciplineSettingsData(disciplineId, strategy.PluginId, [], new Dictionary<string, object?>());

        var rows = await _eventStore.GetDisciplineSettingsAsync(FolderPath, cancellationToken);
        var values = DisciplineSettings.Resolve(strategy, rows, dayId);
        return new DisciplineSettingsData(disciplineId, strategy.PluginId, strategy.Settings,
            strategy.Settings.ToDictionary(d => d.Key, d => values[d.Key]));
    }

    public async Task<IReadOnlyList<DisciplineId>> GetDayDisciplinesAsync(Guid dayId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return [];

        var folder = FolderPath;
        var day = (await _eventStore.GetDaysAsync(folder, cancellationToken)).FirstOrDefault(d => d.Id == dayId);
        if (day is null)
            return [];

        var overrides = (await _eventStore.GetGroupDaySettingsAsync(folder, dayId, cancellationToken))
            .Select(s => s.DisciplineOverride)
            .OfType<DisciplineId>();
        return new[] { day.DefaultDiscipline }.Concat(overrides).Distinct().ToList();
    }

    public async Task SaveDisciplineSettingsAsync(
        Guid dayId, DisciplineId disciplineId, IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (_session.CurrentEvent is null)
            return;

        var defs = _strategies.For(disciplineId).Settings;
        var coerced = defs.ToDictionary(d => d.Key, d => values.TryGetValue(d.Key, out var v) ? DisciplineSettings.Coerce(v, d) : null);

        // Day-scoped values go to the day's row, competition-scoped ones to the row without a day (Р-11); the
        // results are computed on read, so every page that reloads sees the new values.
        var daySettings = defs.Where(d => d.Scope == SettingScope.Day).ToList();
        var competitionSettings = defs.Where(d => d.Scope == SettingScope.Competition).ToList();
        if (daySettings.Count > 0)
            await _eventStore.SaveDisciplineSettingsAsync(FolderPath, dayId, disciplineId,
                DisciplineSettings.Serialize(coerced, daySettings), cancellationToken);
        if (competitionSettings.Count > 0)
            await _eventStore.SaveDisciplineSettingsAsync(FolderPath, null, disciplineId,
                DisciplineSettings.Serialize(coerced, competitionSettings), cancellationToken);
    }

    // ── Online live-results publishing

    public async Task<OnlinePublishSettings?> GetOnlinePublishSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return null;

        var folder = FolderPath;
        var info = await _eventStore.GetCompetitionInfoAsync(folder, cancellationToken);
        var defaults = DefaultOnlinePublishSettings(info, _session.CurrentEvent);

        var json = await _eventStore.GetOnlinePublishJsonAsync(folder, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            return defaults; // no row yet — seed from the competition metadata

        try
        {
            var saved = System.Text.Json.JsonSerializer.Deserialize<OnlinePublishSettings>(json);
            if (saved is null)
                return defaults;
            // A blank slug/title fell out of an old/empty save — backfill from the metadata defaults.
            return saved with
            {
                Slug = string.IsNullOrWhiteSpace(saved.Slug) ? defaults.Slug : saved.Slug,
                Title = string.IsNullOrWhiteSpace(saved.Title) ? defaults.Title : saved.Title,
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return defaults;
        }
    }

    public Task SaveOnlinePublishSettingsAsync(OnlinePublishSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_session.CurrentEvent is null)
            return Task.CompletedTask;

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        return _eventStore.SaveOnlinePublishJsonAsync(FolderPath, json, cancellationToken);
    }

    // Default publish options for a competition that has never been configured: slug from its folder
    // identifier (URL-safe), title from its name, subtitle from the date range.
    private static OnlinePublishSettings DefaultOnlinePublishSettings(CompetitionInfo? info, EventSummary ev)
    {
        var slug = Slugify(!string.IsNullOrWhiteSpace(info?.Identifier) ? info.Identifier : ev.Name);
        var title = !string.IsNullOrWhiteSpace(info?.Name) ? info.Name : ev.Name;
        var subtitle = FormatDateRange(info);
        return OnlinePublishSettings.Default(slug, title, subtitle);
    }

    // Folder-identifier → URL slug: lower-case, keep latin letters/digits, collapse the rest to dashes.
    private static string Slugify(string source)
    {
        var chars = (source ?? string.Empty).Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '-')
            .ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");
        return slug;
    }

    private static string FormatDateRange(CompetitionInfo? info)
    {
        if (info?.StartDate is not { } start)
            return string.Empty;
        return info.EndDate is { } end && end.Date != start.Date
            ? $"{start:dd.MM.yyyy} – {end:dd.MM.yyyy}"
            : start.ToString("dd.MM.yyyy");
    }

    public async Task<OnlineResultsSnapshot> GetOnlineResultsSnapshotAsync(Guid dayId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return OnlineResultsSnapshot.Empty;

        var folder = FolderPath;
        var days = await _eventStore.GetDaysAsync(folder, cancellationToken);
        var day = days.FirstOrDefault(d => d.Id == dayId);
        if (day is null)
            return OnlineResultsSnapshot.Empty;

        // The day list for the frontend's day switcher (label = the day's date, blank when unset).
        var onlineDays = days
            .OrderBy(d => d.Number)
            .Select(d => new OnlineDay(d.Number, d.Date is { } dt ? dt.ToString("dd.MM.yyyy") : string.Empty))
            .ToList();

        // Reuse the protocol data: it already computes one section per group with each member's result.
        var data = await GetResultProtocolDataAsync(dayId, cancellationToken);

        // The frontend sums days («Сума») by group NAME, so look-alike duplicates («M12» on one day, «М12» on
        // another) are published under one canonical name — the first such group in group order.
        var allGroups = await _eventStore.GetGroupsAsync(folder, cancellationToken);
        var canonicalName = new Dictionary<string, string>(TextKey.Comparer);
        foreach (var g in allGroups)
            canonicalName.TryAdd(g.Name, g.Name.Trim());
        string OnlineGroupName(string name) => canonicalName.TryGetValue(name, out var n) ? n : name;

        var groups = data.Groups
            .Select(g => new OnlineGroup(OnlineGroupName(g.Name), g.DistanceKm, g.ControlCount, g.Order))
            .DistinctBy(g => g.Name, TextKey.Comparer)
            .ToList();

        // The frontend keys results by (event, bib, day) with bib an integer. A runner with no (positive integer)
        // start number goes under a stand-in negative key from the participant id, which the site doesn't show —
        // so a competition run without numbers publishes too.
        var rows = new List<OnlineResultRow>();
        var unnumbered = 0;
        // bib -> every holder's name. Two participants with one number (e.g. after merging people) would put two
        // rows with the same key into one upsert, which the server rejects wholesale — so only the first keeps it.
        var holders = new Dictionary<int, List<string>>();
        var standIns = new HashSet<int>();
        foreach (var g in data.Groups)
        {
            // A relay group is published as teams (below): its runners' leg numbers («100.2») are not bibs.
            if (g.RelayTeams is not null)
                continue;

            foreach (var r in g.Rows)
            {
                var trimmed = (r.Number ?? string.Empty).Trim();
                var hasNumber = int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bib)
                                && bib > 0;
                var keepsNumber = hasNumber && !holders.ContainsKey(bib);
                if (hasNumber && !keepsNumber)
                    holders[bib].Add(r.FullName);
                else if (!hasNumber)
                    unnumbered++;

                int key;
                if (keepsNumber)
                {
                    holders[bib] = [r.FullName];
                    key = bib;
                }
                else
                {
                    key = OnlineResultRow.UnnumberedKey(r.ParticipantId);
                    while (!standIns.Add(key))
                        key = key == -int.MaxValue ? -1 : key - 1;
                }

                var res = r.Result;
                rows.Add(new OnlineResultRow(
                    Key: key,
                    GroupName: OnlineGroupName(g.Name),
                    FullName: r.FullName,
                    Team: r.Team,
                    Club: r.ClubName,
                    Region: r.RegionName,
                    Birth: r.BirthDate is { } bd ? bd.ToString("dd.MM.yyyy") : string.Empty,
                    Qual: r.Rank,
                    Place: res.Place,
                    ResultTime: res.RankingTime,
                    StartTime: res.ActualStart?.ToLocalTime().TimeOfDay,
                    FinishTime: res.FinishTime?.ToLocalTime().TimeOfDay,
                    Score: res.Score,
                    Points: res.Points,
                    Status: res.Status,
                    HasReadout: res.HasReadout,
                    OutOfCompetition: res.OutOfCompetition)
                {
                    Extra = OnlineExtras(data.ExtraColumns, res, r.Fields),
                });
            }
        }

        var duplicates = holders
            .Where(h => h.Value.Count > 1)
            .OrderBy(h => h.Key)
            .Select(h => $"{h.Key} ({string.Join(", ", h.Value)})")
            .ToList();

        return new OnlineResultsSnapshot(onlineDays, day.Number, groups, rows, unnumbered, duplicates)
        {
            Teams = OnlineTeams(data, OnlineGroupName),
            ExtraColumns = data.ExtraColumns
                .Select(c => new OnlineExtraColumn(c.Key, c.LabelKey, IsPlugin: c.Plugin is not null))
                .ToList(),
        };
    }

    // A result's extra column values (plugin columns + additional fields, §10.2), formatted; blanks left out.
    private static Dictionary<string, string> OnlineExtras(
        IReadOnlyList<ExtraResultColumn> columns, ParticipantDayResult result, IReadOnlyDictionary<string, string>? fields) =>
        columns
            .Select(c => (c.Key, Value: c.Format(result, fields)))
            .Where(x => x.Value.Length > 0)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    // §11.1: one row per relay team of the day, each with its legs (number «100.N», runner, course, times, status),
    // in standing order (RelayTeamOrder; the spectator site sorts the same way on its own).
    private static List<OnlineTeamRow> OnlineTeams(ResultProtocolData data, Func<string, string> groupName)
    {
        var teams = new List<OnlineTeamRow>();
        foreach (var g in data.Groups)
        {
            if (g.RelayTeams is null)
                continue;
            // The site keys a team by its number (relay_teams primary key), so a team without one is not published yet.
            foreach (var t in RelayTeamOrder.Sort(g.RelayTeams.Where(x => x.Number is not null), x => x.Result, x => x.Number))
            {
                var result = t.Result;
                var number = t.Number!.Value;
                var legs = t.Legs.Select(l =>
                {
                    var runner = l.Runner;
                    var leg = runner?.Result.Leg ?? result?.Legs.FirstOrDefault(x => x.Leg == l.Leg);
                    var extra = runner is null
                        ? new Dictionary<string, string>()
                        : OnlineExtras(data.ExtraColumns, runner.Result, runner.Fields);
                    return new OnlineLegRow(
                        Leg: l.Leg,
                        Bib: RelayBib.Format(number, l.Leg),
                        FullName: runner?.FullName ?? string.Empty,
                        Birth: runner?.BirthDate is { } bd ? bd.ToString("dd.MM.yyyy") : string.Empty,
                        Course: leg?.CourseCode,
                        LegTime: leg?.LegTime,
                        Cumulative: leg?.Cumulative,
                        Status: leg?.Status ?? FinishStatus.None,
                        Pending: leg?.Pending ?? false,
                        Restarted: leg?.Restarted ?? false,
                        Extra: extra)
                    {
                        Result = leg?.AdjustedTime ?? leg?.LegTime,
                        StartTime = leg?.Start?.ToLocalTime().TimeOfDay,
                        Qual = runner?.Rank ?? string.Empty,
                        Club = runner?.ClubName ?? string.Empty,
                        Region = runner?.RegionName ?? string.Empty,
                    };
                }).ToList();

                teams.Add(new OnlineTeamRow(
                    GroupName: groupName(g.Name),
                    Number: number,
                    Name: t.Name,
                    Club: t.ClubName,
                    Region: t.RegionName,
                    Place: result?.Place,
                    Time: result?.Time,
                    Status: result?.Status ?? FinishStatus.None,
                    OutOfCompetition: result?.OutOfCompetition ?? false,
                    Restarted: result?.Restarted ?? false,
                    Points: result?.Points,
                    Legs: legs));
            }
        }
        return teams;
    }

    public async Task<MonitorSettings?> GetMonitorSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return null;

        var json = await _eventStore.GetMonitorJsonAsync(FolderPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            return MonitorSettings.Empty;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<MonitorSettings>(json) ?? MonitorSettings.Empty;
        }
        catch (System.Text.Json.JsonException)
        {
            return MonitorSettings.Empty;
        }
    }

    public Task SaveMonitorSettingsAsync(MonitorSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_session.CurrentEvent is null)
            return Task.CompletedTask;

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        return _eventStore.SaveMonitorJsonAsync(FolderPath, json, cancellationToken);
    }

    public string? GetMonitorOutputDirectory() =>
        _session.CurrentEvent is not { } ev || CurrentDay is not { } day
            ? null
            : _files.MonitorOutputDirectory(ev.FolderPath, ev.Identifier, day.Number);

    public string? ResolveMonitorFilePath(string fileName)
    {
        if (GetMonitorOutputDirectory() is not { } dir || string.IsNullOrWhiteSpace(fileName))
            return null;
        // Defend against a stored value that is a full path or has stray separators — use the leaf name only.
        var leaf = System.IO.Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(leaf))
            return null;
        return System.IO.Path.Combine(dir, leaf);
    }

    public async Task<IReadOnlyList<MonitorFileDocument>> BuildMonitorDocumentsAsync(
        Guid dayId, MonitorLabels labels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (_session.CurrentEvent is null)
            return [];

        var settings = await GetMonitorSettingsAsync(cancellationToken);
        var files = settings?.ActiveFiles;
        if (files is null || files.Count == 0)
            return [];

        // Resolve the built day's folder — the screens are written there (the day whose results they show).
        var days = await _eventStore.GetDaysAsync(FolderPath, cancellationToken);
        var builtDay = days.FirstOrDefault(d => d.Id == dayId);
        if (builtDay is null)
            return [];
        var dayFolder = _files.MonitorOutputDirectory(FolderPath, _session.CurrentEvent.Identifier, builtDay.Number);

        // Reuse the same computed data the protocols and online publish use — one section per group, each
        // member already carrying their result. Group rows are kept in the protocol order (placed finishers
        // first, then the rest), which is exactly the monitor order.
        var data = await GetResultProtocolDataAsync(dayId, cancellationToken);
        var info = await _eventStore.GetCompetitionInfoAsync(FolderPath, cancellationToken);
        var subtitle = !string.IsNullOrWhiteSpace(info?.Name) ? info!.Name : _session.CurrentEvent.Name;

        // Pre-compute, per group, the leader's clean result so the «Відставання» (gap) column can be filled.
        var groupsByName = data.Groups.ToDictionary(g => g.Name, TextKey.Comparer);

        var columns = settings!.EffectiveColumns; // one shared column layout for every file
        var documents = new List<MonitorFileDocument>(files.Count);
        foreach (var file in files)
        {
            // Files are addressed by name only; write them into the built day's folder.
            var leaf = System.IO.Path.GetFileName(file.Path.Trim());
            documents.Add(new MonitorFileDocument(
                System.IO.Path.Combine(dayFolder, leaf),
                BuildMonitorDocument(file, columns, data, groupsByName, subtitle, labels)));
        }

        return documents;
    }

    public async Task<MonitorDocument?> BuildMonitorPreviewAsync(
        Guid dayId, MonitorFile file, ResultColumnSelection columns, MonitorLabels labels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(labels);
        if (_session.CurrentEvent is null)
            return null;

        var data = await GetResultProtocolDataAsync(dayId, cancellationToken);
        var info = await _eventStore.GetCompetitionInfoAsync(FolderPath, cancellationToken);
        var subtitle = !string.IsNullOrWhiteSpace(info?.Name) ? info!.Name : _session.CurrentEvent.Name;
        var groupsByName = data.Groups.ToDictionary(g => g.Name, TextKey.Comparer);

        return BuildMonitorDocument(file, columns, data, groupsByName, subtitle, labels);
    }

    public async Task<MonitorPreviewSource?> GetMonitorPreviewSourceAsync(
        Guid dayId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return null;

        var data = await GetResultProtocolDataAsync(dayId, cancellationToken);
        var info = await _eventStore.GetCompetitionInfoAsync(FolderPath, cancellationToken);
        var subtitle = !string.IsNullOrWhiteSpace(info?.Name) ? info!.Name : _session.CurrentEvent.Name;
        return new MonitorPreviewSource(data, subtitle);
    }

    public MonitorDocument BuildMonitorPreview(
        MonitorFile file, ResultColumnSelection columns, MonitorPreviewSource source, MonitorLabels labels)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(labels);
        var groupsByName = source.Data.Groups.ToDictionary(g => g.Name, TextKey.Comparer);
        return BuildMonitorDocument(file, columns, source.Data, groupsByName, source.Subtitle, labels);
    }

    // Builds one monitor document from a file (groups + title + timing) and the SHARED column selection against
    // the day's computed result data. Shared by the batch generation (BuildMonitorDocumentsAsync) and the page's
    // single-file live preview (BuildMonitorPreviewAsync). Columns are the same for every file.
    private static MonitorDocument BuildMonitorDocument(
        MonitorFile file, ResultColumnSelection columnSelection, ResultProtocolData data,
        IReadOnlyDictionary<string, ResultProtocolGroup> groupsByName, string subtitle, MonitorLabels labels)
    {
        // The chosen groups in day order; an empty selection means "all groups of the day".
        var chosen = file.GroupNames.Count == 0
            ? data.Groups
            : file.GroupNames
                .Select(n => groupsByName.GetValueOrDefault(n))
                .Where(g => g is not null)
                .Select(g => g!)
                .ToList();

        // The chosen columns; an extra column (plugin / field, §10.2) shows when the day has it, «Наростаючий» only
        // on a screen with a relay group (it is blank on every other row).
        var hasRelay = chosen.Any(g => g.RelayTeams is not null);
        var extras = data.ExtraColumns.ToDictionary(e => e.Key, StringComparer.Ordinal);
        var columns = columnSelection.Resolve(data.ExtraColumns.Select(ResultColumnInfo.ForExtra))
            .Where(d => d.Column != ResultColumn.Cumulative || hasRelay)
            .Select(d => d.Column == ResultColumn.Extra
                ? new MonitorColumn(d.Column, labels.Translate?.Invoke(d.LabelKey) ?? d.LabelKey)
                    { Key = d.Key, Extra = extras[d.Key] }
                : new MonitorColumn(d.Column, labels.ColumnHeaders.GetValueOrDefault(d.Column, d.Key)) { Key = d.Key })
            .ToList();

        var monitorGroups = new List<MonitorGroup>(chosen.Count);
        foreach (var g in chosen.OrderBy(g => g.Name, StringComparer.CurrentCulture))
        {
            // Another discipline's plugin columns («K», «T·K» of a weighted relay) stay out of this group's table.
            var hidden = columns
                .Select((c, i) => (c, i))
                .Where(x => x.c.Extra is { } e && !g.Shows(e))
                .Select(x => x.i)
                .ToList();

            if (g.RelayTeams is not null)
            {
                monitorGroups.Add(new MonitorGroup(g.Name, BuildGroupCaption(g, labels), BuildRelayMonitorRows(g, columns, labels))
                {
                    HiddenColumns = hidden,
                });
                continue;
            }

            var leader = g.Rows
                .Where(r => r.Result.Place is not null && r.Result.RankingTime is not null)
                .OrderBy(r => r.Result.RankingTime!.Value)
                .Select(r => r.Result.RankingTime)
                .FirstOrDefault();

            // Sort by result: placed finishers first by ascending place, then everyone still unplaced
            // (DNS/MP/running/поза конкурсом) after them — the same order the printed protocol uses.
            var rows = g.Rows
                .OrderBy(r => r.Result.Place ?? int.MaxValue)
                .ThenBy(r => r.Result.RankingTime ?? TimeSpan.MaxValue)
                .ThenBy(r => r.FullName, StringComparer.CurrentCultureIgnoreCase)
                .Select(r => BuildMonitorRow(r, columns, leader, labels))
                .ToList();

            monitorGroups.Add(new MonitorGroup(g.Name, BuildGroupCaption(g, labels), rows) { HiddenColumns = hidden });
        }

        return new MonitorDocument(
            Title: string.IsNullOrWhiteSpace(file.Title) ? subtitle : file.Title,
            Subtitle: subtitle,
            RefreshSeconds: Math.Max(MonitorFile.MinRefreshSeconds, file.RefreshSeconds),
            ScrollSpeed: Math.Max(0, file.ScrollSpeed),
            Columns: columns,
            Groups: monitorGroups);
    }

    // The caption under a group's name on the monitor: distance + control count, when known.
    private static string BuildGroupCaption(ResultProtocolGroup g, MonitorLabels labels)
    {
        var parts = new List<string>(2);
        if (g.DistanceKm is { } km && km > 0)
            parts.Add(string.Format(labels.DistanceLabel, km.ToString("0.#", CultureInfo.InvariantCulture)));
        if (g.ControlCount is { } cc && cc > 0)
            parts.Add(string.Format(labels.ControlCountLabel, cc));
        return string.Join("  ·  ", parts);
    }

    // Formats one participant's row into the page's chosen columns, parallel to the column list.
    private static MonitorRow BuildMonitorRow(
        ResultProtocolRow r, IReadOnlyList<MonitorColumn> columns, TimeSpan? leader, MonitorLabels labels)
    {
        var res = r.Result;
        var placed = res.Place is not null && !res.OutOfCompetition;
        var values = new List<string>(columns.Count);

        foreach (var col in columns)
            values.Add(col.Column switch
            {
                ResultColumn.Place => res.OutOfCompetition ? "—" : res.Place?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ResultColumn.FullName => r.FullName,
                ResultColumn.Bib => r.Number,
                ResultColumn.Birth => r.BirthDate is { } bd ? bd.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : string.Empty,
                ResultColumn.Qual => r.Rank,
                ResultColumn.Team => r.Team,
                ResultColumn.Club => r.ClubName,
                ResultColumn.Region => r.RegionName,
                ResultColumn.StartTime => FormatClock(res.ActualStart?.ToLocalTime().TimeOfDay),
                ResultColumn.ResultTime => res.Status == FinishStatus.Ok ? FormatSpan(res.RankingTime) : string.Empty,
                ResultColumn.Gap => placed && leader is { } l && res.RankingTime is { } rt && rt > l
                    ? "+" + FormatSpan(rt - l)
                    : string.Empty,
                ResultColumn.Status => StatusText(res, labels),
                ResultColumn.Points => res.Points?.ToString("0.##", CultureInfo.InvariantCulture)
                    ?? res.Score?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ResultColumn.Extra => col.Extra?.Format(res, r.Fields) ?? string.Empty,
                _ => string.Empty,
            });

        // Grey out a row that has no place yet (DNS / MP / running / out-of-competition), like the legacy monitor.
        return new MonitorRow(values, Unplaced: res.Place is null || res.OutOfCompetition);
    }

    // The short status code shown for an unplaced run; blank for a clean finish (its time is in the result column).
    private static string StatusText(ParticipantDayResult res, MonitorLabels labels) =>
        StatusCode(res.Status, res.HasReadout, labels);

    private static string StatusCode(FinishStatus status, bool hasReadout, MonitorLabels labels) => status switch
    {
        FinishStatus.Dns => labels.StatusDns,
        FinishStatus.Mp => labels.StatusMp,
        FinishStatus.Ovt => labels.StatusOvt,
        FinishStatus.Dnf => labels.StatusDnf,
        FinishStatus.Dsq => labels.StatusDsq,
        FinishStatus.Ok => string.Empty,
        _ => hasReadout ? string.Empty : labels.StatusRunning,
    };

    // §11.4: a relay group on the monitor — one bold team row (place, №, name, club, team time, gap, status, points)
    // with its legs under it (100.N, runner + course, leg start, leg result, «Наростаючий», status, plugin columns
    // and fields). Teams in standing order (RelayTeamOrder), like the protocol.
    private static List<MonitorRow> BuildRelayMonitorRows(
        ResultProtocolGroup g, IReadOnlyList<MonitorColumn> columns, MonitorLabels labels)
    {
        static bool Placed(TeamDayResult? r) => r?.Place is not null && !r.OutOfCompetition;

        var teams = RelayTeamOrder.Sort(g.RelayTeams!, t => t.Result, t => t.Number).ToList();
        var leader = teams
            .Where(t => Placed(t.Result) && t.Result!.Time is not null)
            .Select(t => t.Result!.Time)
            .Min();

        var rows = new List<MonitorRow>();
        foreach (var t in teams)
        {
            var res = t.Result;
            var placed = Placed(res);
            var teamValues = columns.Select(col => col.Column switch
            {
                ResultColumn.Place => res?.OutOfCompetition == true ? "—" : res?.Place?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ResultColumn.FullName => t.Name,
                ResultColumn.Bib => RelayTeamNumbers.Text(t.Number),
                ResultColumn.Team => t.Name,
                ResultColumn.Club => t.ClubName,
                ResultColumn.Region => t.RegionName,
                ResultColumn.ResultTime => res?.Status == FinishStatus.Ok ? FormatSpan(res.Time) : string.Empty,
                ResultColumn.Gap => placed && leader is { } l && res!.Time is { } tt && tt > l ? "+" + FormatSpan(tt - l) : string.Empty,
                ResultColumn.Status => res is null
                    ? labels.StatusRunning
                    : StatusCode(res.Status, res.Legs.Any(x => x.Status != FinishStatus.None), labels),
                ResultColumn.Points => res?.Points?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty,
                _ => string.Empty,
            }).ToList();
            rows.Add(new MonitorRow(teamValues, Unplaced: !placed) { Kind = MonitorRowKind.Team });

            foreach (var leg in t.Legs)
            {
                var runner = leg.Runner;
                var legResult = runner?.Result.Leg ?? res?.Legs.FirstOrDefault(x => x.Leg == leg.Leg);
                var name = runner?.FullName ?? string.Empty;
                if (legResult?.CourseCode is { Length: > 0 } course)
                    name = name.Length > 0 ? $"{name} ({course})" : course;
                var legValues = columns.Select(col => col.Column switch
                {
                    ResultColumn.FullName => name,
                    ResultColumn.Bib => RelayTeamNumbers.Bib(t.Number, leg.Leg),
                    ResultColumn.Birth => runner?.BirthDate is { } bd ? bd.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : string.Empty,
                    ResultColumn.Qual => runner?.Rank ?? string.Empty,
                    ResultColumn.Club => runner?.ClubName ?? string.Empty,
                    ResultColumn.Region => runner?.RegionName ?? string.Empty,
                    ResultColumn.StartTime => FormatClock(legResult?.Start?.ToLocalTime().TimeOfDay),
                    // The leg's counted time — adjusted by the discipline (weighted relay: T·K), as in the protocol.
                    ResultColumn.ResultTime => legResult?.Status == FinishStatus.Ok
                        ? FormatSpan(legResult.AdjustedTime ?? legResult.LegTime)
                        : string.Empty,
                    ResultColumn.Cumulative => legResult?.Cumulative is { } cum ? FormatSpan(cum) : string.Empty,
                    ResultColumn.Status => legResult is null || legResult.Pending
                        ? string.Empty
                        : StatusCode(legResult.Status, legResult.Status != FinishStatus.None, labels),
                    ResultColumn.Extra => runner is null ? string.Empty : col.Extra?.Format(runner.Result, runner.Fields) ?? string.Empty,
                    _ => string.Empty,
                }).ToList();
                rows.Add(new MonitorRow(legValues, Unplaced: !placed) { Kind = MonitorRowKind.Leg });
            }
        }
        return rows;
    }

    private static string FormatSpan(TimeSpan? t) => TimeFormat.Span(t);

    private static string FormatClock(TimeSpan? t) => TimeFormat.Clock(t);

    public Task<IReadOnlyList<ChipPriceOverride>> GetChipPriceOverridesAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return Task.FromResult<IReadOnlyList<ChipPriceOverride>>([]);

        return _eventStore.GetChipPriceOverridesAsync(FolderPath, cancellationToken);
    }

    public async Task<ChipPriceOverride> AddChipPriceOverrideRowAsync(CancellationToken cancellationToken = default)
    {
        var priceOverride = new ChipPriceOverride();
        await _eventStore.AddChipPriceOverrideAsync(FolderPath, priceOverride, cancellationToken);
        return priceOverride;
    }

    public Task UpdateChipPriceOverrideAsync(ChipPriceOverride priceOverride, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(priceOverride);
        return _eventStore.UpdateChipPriceOverrideAsync(FolderPath, priceOverride, cancellationToken);
    }

    public Task DeleteChipPriceOverrideAsync(Guid overrideId, CancellationToken cancellationToken = default) =>
        _eventStore.DeleteChipPriceOverrideAsync(FolderPath, overrideId, cancellationToken);

    public Task<IReadOnlyList<EntryFeeDiscount>> GetEntryFeeDiscountsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return Task.FromResult<IReadOnlyList<EntryFeeDiscount>>([]);

        return _eventStore.GetEntryFeeDiscountsAsync(FolderPath, cancellationToken);
    }

    public async Task<EntryFeeDiscount> AddEntryFeeDiscountRowAsync(CancellationToken cancellationToken = default)
    {
        var discount = new EntryFeeDiscount();
        await _eventStore.AddEntryFeeDiscountAsync(FolderPath, discount, cancellationToken);
        return discount;
    }

    public Task UpdateEntryFeeDiscountAsync(EntryFeeDiscount discount, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discount);
        return _eventStore.UpdateEntryFeeDiscountAsync(FolderPath, discount, cancellationToken);
    }

    public Task DeleteEntryFeeDiscountAsync(Guid discountId, CancellationToken cancellationToken = default) =>
        _eventStore.DeleteEntryFeeDiscountAsync(FolderPath, discountId, cancellationToken);

    public Task SetParticipantPaysRaisedFeeAsync(Guid participantId, bool paysRaisedFee, CancellationToken cancellationToken = default) =>
        _eventStore.SetParticipantPaysRaisedFeeAsync(FolderPath, participantId, paysRaisedFee, cancellationToken);

    public Task<int> SetParticipantsCheckedInAsync(IReadOnlyList<Guid> participantIds, bool checkedIn, CancellationToken cancellationToken = default) =>
        participantIds.Count == 0
            ? Task.FromResult(0)
            : _eventStore.SetParticipantsCheckedInAsync(FolderPath, participantIds, checkedIn, cancellationToken);

    public Task SetParticipantDiscountAsync(Guid participantId, Guid discountId, bool on, CancellationToken cancellationToken = default) =>
        _eventStore.SetParticipantDiscountAsync(FolderPath, participantId, discountId, on, cancellationToken);

    // ── Fee computation
    // Loads the competition-level fee inputs once (info, group fees, chip-price overrides, discounts,
    // per-participant discount links) so the roster/day-row builders can compute each participant's
    // total in a single pass without re-querying. Returns a reusable resolver bound to that snapshot.
    private async Task<FeeSnapshot> LoadFeeContextAsync(string folder, CancellationToken cancellationToken)
    {
        var info = await _eventStore.GetCompetitionInfoAsync(folder, cancellationToken);
        var groups = await _eventStore.GetGroupsAsync(folder, cancellationToken);
        var chipPrices = await _eventStore.GetChipPriceOverridesAsync(folder, cancellationToken);
        var discounts = await _eventStore.GetEntryFeeDiscountsAsync(folder, cancellationToken);
        var participantDiscounts = await _eventStore.GetParticipantDiscountsAsync(folder, cancellationToken);
        var rentalChips = await _eventStore.GetRentalChipsAsync(folder, cancellationToken);

        var context = new EntryFeeContext(_entryFees, info, groups, chipPrices, discounts, rentalChips);
        var validDiscountIds = discounts.Where(d => !d.IsFsouMemberDiscount).Select(d => d.Id).ToHashSet();
        var discountsByParticipant = participantDiscounts
            .GroupBy(p => p.ParticipantId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(x => x.DiscountId).Where(validDiscountIds.Contains).ToList());

        return new FeeSnapshot(context, discountsByParticipant);
    }

    // Pairs the shared fee context with the per-participant selected-discount lookup, so the row
    // builders get both the precomputed total and the ids needed to seed each row's checkboxes.
    private sealed class FeeSnapshot(EntryFeeContext context, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> discountsByParticipant)
    {
        public IReadOnlyList<Guid> SelectedDiscountIds(Guid participantId) =>
            discountsByParticipant.TryGetValue(participantId, out var ids) ? ids : [];

        public decimal Total(Participant participant, IEnumerable<EntryFeeDay> memberDays) =>
            context.Total(participant.PaysRaisedFee, participant.IsFsouMember, SelectedDiscountIds(participant.Id), memberDays);

        /// <summary>The structured breakdown behind <see cref="Total"/> — the caller needs it to ask what one
        /// day of the participant's fee costs (per-day payment redistribution).</summary>
        public EntryFeeBreakdown Describe(Participant participant, IEnumerable<EntryFeeDay> memberDays) =>
            context.Describe(participant.PaysRaisedFee, participant.IsFsouMember, SelectedDiscountIds(participant.Id), memberDays);
    }

    private ParticipantDayRow ToRow(
        ParticipantDay link,
        Participant p,
        IReadOnlyDictionary<Guid, string> groupName,
        IReadOnlyDictionary<Guid, string> regionName,
        IReadOnlyDictionary<Guid, string> clubName,
        IReadOnlyDictionary<Guid, string> dusshName,
        bool paysRaisedFee = false,
        IReadOnlyList<Guid>? selectedDiscountIds = null,
        decimal totalEntryFee = 0m,
        IReadOnlyList<ParticipantFeeDay>? otherDays = null,
        ParticipantDayResult? result = null,
        bool paymentPerDay = false)
    {
        var name = link.GroupId is { } gid && groupName.TryGetValue(gid, out var n) ? n : string.Empty;
        var region = p.RegionId is { } rid && regionName.TryGetValue(rid, out var rn) ? rn : string.Empty;
        var club = p.ClubId is { } cid && clubName.TryGetValue(cid, out var cn) ? cn : string.Empty;
        var dussh = p.DusshId is { } did && dusshName.TryGetValue(did, out var dn) ? dn : string.Empty;
        return new ParticipantDayRow(
            LinkId: link.Id,
            DayId: link.EventDayId,
            ParticipantId: p.Id,
            Order: link.Order,
            FullName: p.FullName,
            Number: p.Number,
            Rank: p.Rank,
            Coach: p.Coach,
            BirthDate: p.BirthDate,
            RegionId: p.RegionId,
            RegionName: region,
            ClubId: p.ClubId,
            ClubName: club,
            DusshId: p.DusshId,
            DusshName: dussh,
            Representative: p.Representative,
            FsouCode: p.FsouCode,
            IsFsouMember: p.IsFsouMember,
            Payment: p.Payment,
            DayPayment: link.Payment,
            PaymentPerDay: paymentPerDay,
            Note: p.Note,
            PaysRaisedFee: paysRaisedFee,
            SelectedDiscountIds: selectedDiscountIds ?? [],
            TotalEntryFee: totalEntryFee,
            OtherDays: otherDays ?? [],
            GroupId: link.GroupId,
            GroupName: name,
            Chip: link.Chip,
            Team: p.Team,
            StartTime: link.StartTime,
            OutOfCompetition: link.OutOfCompetition,
            Bonus: link.Bonus,
            DayDefaultDiscipline: CurrentDayDefaultDiscipline,
            Result: result ?? ParticipantDayResult.Empty);
    }

    public async Task<MapCountsData> GetMapCountsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return MapCountsData.Empty;

        var folder = FolderPath;
        var days = (await _eventStore.GetDaysAsync(folder, cancellationToken)).OrderBy(d => d.Number).ToList();
        var groups = (await _eventStore.GetGroupsAsync(folder, cancellationToken)).ToDictionary(g => g.Id);
        // Join to existing participants so an orphaned per-day link (participant deleted, link left behind)
        // does not inflate a map count — the same guard the dashboard applies.
        var participantIds = (await _eventStore.GetParticipantsAsync(folder, cancellationToken))
            .Select(p => p.Id)
            .ToHashSet();

        // Groups in the order they first appear on a day (day order, then the day's group order), so the sheet
        // reads like the groups page of the first day; a group added only on a later day follows.
        var groupOrder = new List<Guid>();
        var countsByGroup = new Dictionary<Guid, int?[]>();
        var noGroup = new int?[days.Count];
        var anyWithoutGroup = false;

        for (var i = 0; i < days.Count; i++)
        {
            var settings = await _eventStore.GetGroupDaySettingsAsync(folder, days[i].Id, cancellationToken);
            foreach (var s in settings.OrderBy(s => s.Order))
            {
                if (!groups.ContainsKey(s.GroupId))
                    continue;
                if (!countsByGroup.TryGetValue(s.GroupId, out var counts))
                {
                    counts = new int?[days.Count];
                    countsByGroup[s.GroupId] = counts;
                    groupOrder.Add(s.GroupId);
                }
                counts[i] = 0; // on the day: a real zero, not "not on this day"
            }

            var links = await _eventStore.GetParticipantDaysAsync(folder, days[i].Id, cancellationToken);
            foreach (var link in links)
            {
                if (!participantIds.Contains(link.ParticipantId))
                    continue;
                if (link.GroupId is { } groupId && groups.ContainsKey(groupId))
                {
                    // A member of a group the day does not list (stale link) still runs a course — count it.
                    if (!countsByGroup.TryGetValue(groupId, out var counts))
                    {
                        counts = new int?[days.Count];
                        countsByGroup[groupId] = counts;
                        groupOrder.Add(groupId);
                    }
                    counts[i] = (counts[i] ?? 0) + 1;
                }
                else
                {
                    noGroup[i] = (noGroup[i] ?? 0) + 1;
                    anyWithoutGroup = true;
                }
            }
        }

        var rows = new List<MapCountRow>(groupOrder.Count + 1);
        foreach (var id in groupOrder)
            rows.Add(new MapCountRow(id, groups[id].Name, countsByGroup[id]));
        if (anyWithoutGroup)
            rows.Add(new MapCountRow(null, string.Empty, noGroup));
        return new MapCountsData(days, rows);
    }

    private GroupDayRow ToRow(GroupDaySettings s, Group group, int participantCount = 0, int scatterVariantCount = 0, int scatterMaxControlCount = 0) => new(
        SettingsId: s.Id,
        GroupId: s.GroupId,
        Order: s.Order,
        Name: group.Name,
        CourseOrder: s.CourseOrder,
        DistanceKm: s.DistanceKm,
        DisciplineOverride: s.DisciplineOverride,
        DayDefaultDiscipline: CurrentDayDefaultDiscipline,
        TimeLimitSeconds: s.TimeLimitSeconds,
        RequiredControlCount: s.RequiredControlCount,
        PenaltyPerMinute: s.PenaltyPerMinute,
        CourseSetter: s.CourseSetter,
        CourseSetterCategory: s.CourseSetterCategory,
        PointsRuleId: s.PointsRuleId,
        RankLevel: s.RankLevel,
        MasterCount: s.MasterCount,
        // Age window lives on the group itself (shared across days), so it comes from the group, not the
        // per-day settings.
        MinBirthYear: group.MinBirthYear,
        MaxBirthYear: group.MaxBirthYear,
        ParticipantCount: participantCount,
        ScatterVariantCount: scatterVariantCount,
        ScatterMaxControlCount: scatterMaxControlCount,
        LegCount: Math.Max(1, s.LegCount ?? 1),
        CourseMode: s.CourseAssignmentMode);
}
