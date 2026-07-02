using System.Globalization;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Enums;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>
/// Default <see cref="ICompetitionEditorService"/>: resolves the current competition's folder
/// from the session and delegates persistence to <see cref="IEventStore"/>.
/// </summary>
public sealed class CompetitionEditorService : ICompetitionEditorService
{
    private readonly ISessionService _session;
    private readonly IEventStore _eventStore;
    private readonly IAppStore _appStore;
    private readonly ICourseDistanceCalculator _distance;
    private readonly Disciplines.IDisciplineStrategyProvider _strategies;
    private readonly IEntryFeeCalculator _entryFees;

    public CompetitionEditorService(
        ISessionService session,
        IEventStore eventStore,
        IAppStore appStore,
        ICourseDistanceCalculator distance,
        Disciplines.IDisciplineStrategyProvider strategies,
        IEntryFeeCalculator entryFees)
    {
        _session = session;
        _eventStore = eventStore;
        _appStore = appStore;
        _distance = distance;
        _strategies = strategies;
        _entryFees = entryFees;
    }

    private string FolderPath =>
        _session.CurrentEvent?.FolderPath
        ?? throw new InvalidOperationException("No competition is currently selected.");

    private Guid CurrentDayId =>
        _session.CurrentDay?.Id
        ?? throw new InvalidOperationException("No competition day is currently selected.");

    private DisciplineType CurrentDayDefaultDiscipline =>
        _session.CurrentDay?.DefaultDiscipline ?? DisciplineType.SetCourse;

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

    /// <summary>Derives the default age window for a newly created group from its name and the competition year.</summary>
    private async Task<(int? MinBirthYear, int? MaxBirthYear)> DeriveAgeWindowAsync(
        string name, CancellationToken cancellationToken)
    {
        var startYear = await GetCompetitionStartYearAsync(FolderPath, cancellationToken);
        return Group.DeriveAgeWindow(name, startYear);
    }

    public Task SaveInfoAsync(CompetitionInfo info, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        return _eventStore.SaveCompetitionInfoAsync(FolderPath, info, cancellationToken);
    }

    public async Task<DashboardInfo> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var ev = _session.CurrentEvent;
        var day = _session.CurrentDay;
        if (ev is null || day is null)
            return new DashboardInfo { HasSelection = false };

        var folder = ev.FolderPath;

        // Competition-wide counts.
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var chips = await _eventStore.GetRentalChipsAsync(folder, cancellationToken);
        var allLinks = await _eventStore.GetAllParticipantDaysAsync(folder, cancellationToken);

        // Rental chips: a chip is "handed out" when its number is held by at least one participant on any
        // day. Match the holder map's case-insensitive trimmed-number keying.
        var heldNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in allLinks)
        {
            var chip = link.Chip.Trim();
            if (chip.Length != 0)
                heldNumbers.Add(chip);
        }
        var chipsHandedOut = chips.Count(c => heldNumbers.Contains(c.Number.Trim()));

        // Current-day counts.
        var dayLinks = await _eventStore.GetParticipantDaysAsync(folder, day.Id, cancellationToken);
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

        // Run results for the current day, computed exactly as the participant tables do.
        var results = await ComputeDayResultsAsync(folder, day, cancellationToken);

        int finishedOk = 0, finishedProblem = 0, onCourse = 0;
        foreach (var link in dayLinks)
        {
            var r = results.TryGetValue(link.Id, out var res) ? res : ParticipantDayResult.Empty;
            if (r.Status == FinishStatus.Ok)
                finishedOk++;
            else if (r.StatusIsProblem)
                finishedProblem++;
            else if (r.ActualStart is null && r.FinishTime is null)
            {
                // Still on course: no actual (chip) start, no finish and no (blank) status — the exact same
                // three-field test the "На дистанції" participants filter uses, so the count matches the
                // filtered list. (A read that produced no start/finish punch and no status counts here too.)
                onCourse++;
            }
        }

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
            OnCourse = onCourse,
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

        // Give the new day its files folder (where imported XML for the day is stored).
        Directory.CreateDirectory(DayFolders.PathFor(FolderPath, nextNumber));
        return day;
    }

    public Task UpdateDayAsync(EventDay day, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(day);
        return _eventStore.UpdateDayAsync(FolderPath, day, cancellationToken);
    }

    public Task DeleteDayAsync(Guid dayId, CancellationToken cancellationToken = default)
        => _eventStore.DeleteDayAsync(FolderPath, dayId, cancellationToken);

    public async Task<EventDay?> ChangeDayNumberAsync(Guid dayId, int newNumber, CancellationToken cancellationToken = default)
    {
        if (newNumber < 1)
            return null;

        var folder = FolderPath;
        var days = await _eventStore.GetDaysAsync(folder, cancellationToken);

        var day = days.FirstOrDefault(d => d.Id == dayId);
        if (day is null || day.Number == newNumber)
            return null;

        // The target number must be free — we swap a single day's number, we don't shift the others.
        if (days.Any(d => d.Id != dayId && d.Number == newNumber))
            return null;

        var oldFolder = DayFolders.PathFor(folder, day.Number);
        var newFolder = DayFolders.PathFor(folder, newNumber);

        await _eventStore.UpdateDayNumberAsync(folder, dayId, newNumber, cancellationToken);

        // Keep the day's files folder name in step with its number. The target name is free (its
        // number was), so a plain move works; if the old folder was never created, just create the
        // new one so the day always has its folder.
        try
        {
            if (Directory.Exists(oldFolder) && !Directory.Exists(newFolder))
                Directory.Move(oldFolder, newFolder);
            else if (!Directory.Exists(newFolder))
                Directory.CreateDirectory(newFolder);
        }
        catch
        {
            // A locked/again-renamed folder shouldn't fail the renumber — the DB is the source of
            // truth for the number; the folder is just where imported files are kept.
        }

        return new EventDay
        {
            Id = day.Id,
            Number = newNumber,
            Date = day.Date,
            Venue = day.Venue,
            DefaultDiscipline = day.DefaultDiscipline,
            CreatedAt = day.CreatedAt
        };
    }

    public async Task<string?> SaveDayFileAsync(string fileName, byte[] content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (_session.CurrentDay is not { } day)
            return null;

        var safeName = Path.GetFileName((fileName ?? string.Empty).Trim());
        if (string.IsNullOrEmpty(safeName))
            safeName = "import.xml";

        var folder = DayFolders.PathFor(FolderPath, day.Number);
        Directory.CreateDirectory(folder);

        var target = Path.Combine(folder, safeName);

        // If the exact name already holds identical bytes, reuse it — don't write a duplicate.
        if (File.Exists(target) && SameContent(target, content))
            return target;

        // Name free → write it as-is.
        if (!File.Exists(target))
        {
            await File.WriteAllBytesAsync(target, content, cancellationToken);
            return target;
        }

        // Name taken by different content → append a short content hash so both survive. If a file
        // with that hashed name is itself already identical (a re-import of the same file), reuse it.
        var stem = Path.GetFileNameWithoutExtension(safeName);
        var ext = Path.GetExtension(safeName);
        var hash = ShortHash(content);
        var hashedName = $"{stem}-{hash}{ext}";
        var hashedTarget = Path.Combine(folder, hashedName);

        if (File.Exists(hashedTarget) && SameContent(hashedTarget, content))
            return hashedTarget;

        await File.WriteAllBytesAsync(hashedTarget, content, cancellationToken);
        return hashedTarget;
    }

    private static bool SameContent(string path, byte[] content)
    {
        try
        {
            var existing = File.ReadAllBytes(path);
            return existing.AsSpan().SequenceEqual(content);
        }
        catch
        {
            // Unreadable existing file — treat as different so we fall through to a hashed copy.
            return false;
        }
    }

    private static string ShortHash(byte[] content)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(content);
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant(); // 8 hex chars
    }

    public Task<IReadOnlyList<ControlPoint>> GetControlPointsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || _session.CurrentDay is null)
            return Task.FromResult<IReadOnlyList<ControlPoint>>([]);

        return _eventStore.GetControlPointsAsync(FolderPath, CurrentDayId, cancellationToken);
    }

    public async Task<ControlPoint> AddControlPointAsync(CancellationToken cancellationToken = default)
    {
        var dayId = CurrentDayId;
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

    public Task UpdateControlPointAsync(ControlPoint point, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(point);
        return _eventStore.UpdateControlPointAsync(FolderPath, point, cancellationToken);
    }

    public Task DeleteControlPointAsync(Guid pointId, CancellationToken cancellationToken = default)
        => _eventStore.DeleteControlPointAsync(FolderPath, pointId, cancellationToken);

    public Task<int> SetProblematicControlsAsync(
        IReadOnlyCollection<Guid> disabledPointIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disabledPointIds);
        if (_session.CurrentEvent is null || _session.CurrentDay is null)
            return Task.FromResult(0);

        return _eventStore.SetControlPointsDisabledAsync(
            FolderPath, CurrentDayId, disabledPointIds, cancellationToken);
    }

    public async Task<ControlPointImportResult> ImportControlPointsAsync(
        IofCourseData data,
        bool replaceAll,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var dayId = CurrentDayId;
        var folder = FolderPath;

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
        if (_session.CurrentEvent is null || _session.CurrentDay is null)
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

        var rows = new List<GroupDayRow>(settings.Count);
        foreach (var s in settings)
        {
            // Defensive: skip a settings row whose group was removed out from under it.
            if (byId.TryGetValue(s.GroupId, out var group))
                rows.Add(ToRow(s, group, countByGroup.GetValueOrDefault(s.GroupId)));
        }
        return rows;
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
        var trimmed = (name ?? string.Empty).Trim();

        // Reuse an existing group with the same name (case-insensitive), or create a new one.
        var groups = await _eventStore.GetGroupsAsync(FolderPath, cancellationToken);
        var group = groups.FirstOrDefault(g => string.Equals(g.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (group is null)
        {
            var (min, max) = await DeriveAgeWindowAsync(trimmed, cancellationToken);
            group = new Group { Name = trimmed, MinBirthYear = min, MaxBirthYear = max };
            await _eventStore.AddGroupAsync(FolderPath, group, cancellationToken);
        }

        // If the group is already on this day, return its existing row instead of duplicating.
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

    public async Task UpdateGroupDayRowAsync(GroupDayRow row, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        // Rename the group (affects every day it runs on). Ignore empty names and collisions with a
        // different group, keeping the previous name — the row text reverts on the next reload.
        var target = (row.Name ?? string.Empty).Trim();
        if (target.Length > 0)
        {
            var groups = await _eventStore.GetGroupsAsync(FolderPath, cancellationToken);
            var collides = groups.Any(g =>
                g.Id != row.GroupId && string.Equals(g.Name, target, StringComparison.OrdinalIgnoreCase));
            if (!collides)
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
            .GroupBy(g => g.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var startYear = await GetCompetitionStartYearAsync(folder, cancellationToken);

        var settings = await _eventStore.GetGroupDaySettingsAsync(folder, dayId, cancellationToken);
        var settingsByGroupId = settings.ToDictionary(s => s.GroupId);
        var nextOrder = settings.Count == 0 ? 1 : settings.Max(s => s.Order) + 1;

        var added = 0;
        var updated = 0;

        // Collapse duplicate course names within the file (keep the first), so a repeated course
        // does not create a second group or fight itself for the same day row.
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var course in data.Courses)
        {
            var name = course.Name.Trim();
            if (name.Length == 0 || !seenNames.Add(name))
                continue;

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
                // Already on the day. In add-only mode leave it as-is; otherwise overwrite the course
                // order/distance and reset the discipline override to the day default (null).
                if (!updateExisting)
                    continue;

                existing.CourseOrder = courseOrder;
                existing.DistanceKm = distanceKm;
                existing.DisciplineOverride = null;
                await _eventStore.UpdateGroupDaySettingsAsync(folder, existing, cancellationToken);
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
                    DistanceKm = distanceKm
                };
                await _eventStore.AddGroupDaySettingsAsync(folder, row, cancellationToken);
                settingsByGroupId[group.Id] = row;
                added++;
            }
        }

        return new GroupImportResult(Added: added, Updated: updated);
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
            c.Id != chip.Id && string.Equals(c.Number.Trim(), number, StringComparison.OrdinalIgnoreCase));
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
        var holders = new Dictionary<string, (List<string> Names, HashSet<Guid> Seen)>(StringComparer.OrdinalIgnoreCase);
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
            StringComparer.OrdinalIgnoreCase);
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
            StringComparer.OrdinalIgnoreCase);

        // Preserve the start's digit width so e.g. "0042" yields "0042", "0043", … not "42".
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
            StringComparer.OrdinalIgnoreCase);
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

        // Reuse an existing region with the same name (case-insensitive), or create a new one.
        var regions = await _eventStore.GetRegionsAsync(FolderPath, cancellationToken);
        var region = regions.FirstOrDefault(r => string.Equals(r.Name, trimmed, StringComparison.OrdinalIgnoreCase));
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
            r.Id != region.Id && string.Equals(r.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (collides || name.Length == 0)
            name = regions.FirstOrDefault(r => r.Id == region.Id)?.Name ?? name;

        await _eventStore.UpdateRegionAsync(FolderPath, new Region { Id = region.Id, Name = name }, cancellationToken);
    }

    public async Task DeleteRegionAsync(Guid regionId, CancellationToken cancellationToken = default)
    {
        var folder = FolderPath;
        // Region is optional on a participant; clear it from anyone using it, then delete the region.
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

        // Region is competition-level: update the participant identity row, preserving its other fields.
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
        var club = clubs.FirstOrDefault(c => string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase));
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
            c.Id != club.Id && string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
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
        var dussh = dusshes.FirstOrDefault(d => string.Equals(d.Name, trimmed, StringComparison.OrdinalIgnoreCase));
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
            d.Id != dussh.Id && string.Equals(d.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
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
        if (_session.CurrentEvent is null || _session.CurrentDay is null)
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
        var allLinks = await _eventStore.GetAllParticipantDaysAsync(folder, cancellationToken);
        var linksByParticipant = allLinks
            .GroupBy(l => l.ParticipantId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Computed run results for this day, keyed by participant-day link id.
        var results = await ComputeDayResultsAsync(folder, _session.CurrentDay, cancellationToken);

        var rows = new List<ParticipantDayRow>(links.Count);
        foreach (var link in links)
        {
            // Defensive: skip a link whose participant was removed out from under it.
            if (!byParticipant.TryGetValue(link.ParticipantId, out var participant))
                continue;

            var all = linksByParticipant.TryGetValue(participant.Id, out var ls) ? ls : [link];
            var memberDays = all.Select(l => ((Guid?)l.GroupId, l.Chip)).ToList();
            // The day grid lets a row recompute live; it needs the OTHER days' fixed contributions.
            var otherDays = all
                .Where(l => l.Id != link.Id)
                .Select(l => new ParticipantFeeDay(l.GroupId, l.Chip))
                .ToList();
            var result = results.TryGetValue(link.Id, out var r) ? r : ParticipantDayResult.Empty;
            rows.Add(ToRow(link, participant, groupName, regionName, clubName, dusshName,
                participant.PaysRaisedFee, fees.SelectedDiscountIds(participant.Id),
                fees.Total(participant, memberDays), otherDays, result));
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

        // Computed run results, per day, keyed by participant-day link id (the roster spans all days).
        var resultsByDay = new Dictionary<Guid, IReadOnlyDictionary<Guid, ParticipantDayResult>>(days.Count);
        foreach (var day in days)
            resultsByDay[day.Id] = await ComputeDayResultsAsync(folder, day, cancellationToken);

        // Index links by (participant, day) so each roster cell is a quick lookup.
        var linkByKey = links.ToDictionary(l => (l.ParticipantId, l.EventDayId));

        var rows = new List<ParticipantRosterRow>(participants.Count);
        foreach (var participant in participants)
        {
            var cells = new List<RosterDayCell>(days.Count);
            var memberDays = new List<(Guid?, string)>();
            foreach (var day in days)
            {
                if (linkByKey.TryGetValue((participant.Id, day.Id), out var link))
                {
                    var name = link.GroupId is { } gid && groupName.TryGetValue(gid, out var n) ? n : string.Empty;
                    var result = resultsByDay.TryGetValue(day.Id, out var dr) && dr.TryGetValue(link.Id, out var r)
                        ? r : ParticipantDayResult.Empty;
                    cells.Add(new RosterDayCell(day.Id, day.Number, link.Id, IsMember: true, link.GroupId, name, link.Chip, link.StartTime, link.OutOfCompetition, link.Bonus, result));
                    memberDays.Add((link.GroupId, link.Chip));
                }
                else
                {
                    cells.Add(new RosterDayCell(day.Id, day.Number, LinkId: null, IsMember: false, GroupId: null, GroupName: string.Empty, Chip: string.Empty, StartTime: null, OutOfCompetition: false, Bonus: null, ParticipantDayResult.Empty));
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
                participant.Note,
                participant.Team,
                participant.PaysRaisedFee,
                fees.SelectedDiscountIds(participant.Id),
                fees.Total(participant, memberDays),
                cells));
        }
        return rows;
    }

    public async Task<ParticipantDayRow?> AddParticipantToDayAsync(CancellationToken cancellationToken = default)
    {
        var folder = FolderPath;
        var dayId = CurrentDayId;

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

        // A fresh participant has no region/club/ДЮСШ yet, so empty name maps are fine.
        return ToRow(link, participant,
            new Dictionary<Guid, string> { [firstGroup.GroupId] = firstGroup.Name },
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, string>());
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

        // Re-read the roster and return this participant's row so the UI can append it.
        var roster = await GetParticipantRosterAsync(cancellationToken);
        return roster.FirstOrDefault(r => r.ParticipantId == participant.Id);
    }

    public async Task UpdateParticipantDayRowAsync(ParticipantDayRow row, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        var folder = FolderPath;
        var dayId = CurrentDayId;

        // Save the participant identity (affects every day). A number colliding with a different
        // participant is dropped, keeping the stored number — the row reverts on the next reload.
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var number = (row.Number ?? string.Empty).Trim();
        var numberCollides = number.Length > 0 && participants.Any(p =>
            p.Id != row.ParticipantId && string.Equals(p.Number.Trim(), number, StringComparison.OrdinalIgnoreCase));
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
            l.Id != row.LinkId && string.Equals(l.Chip.Trim(), chip, StringComparison.OrdinalIgnoreCase));
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

    public async Task RemoveParticipantFromDayAsync(Guid linkId, Guid participantId, CancellationToken cancellationToken = default)
    {
        var folder = FolderPath;
        await _eventStore.DeleteParticipantDayAsync(folder, linkId, cancellationToken);

        // A participant that no longer runs on any day is removed entirely.
        var remaining = await _eventStore.CountParticipantDaysForParticipantAsync(folder, participantId, cancellationToken);
        if (remaining == 0)
            await _eventStore.DeleteParticipantAsync(folder, participantId, cancellationToken);
    }

    public Task DeleteParticipantAsync(Guid participantId, CancellationToken cancellationToken = default)
        => _eventStore.DeleteParticipantAsync(FolderPath, participantId, cancellationToken);

    public async Task<Guid> SetParticipantDayGroupAsync(Guid participantId, Guid dayId, Guid? groupId, CancellationToken cancellationToken = default)
    {
        var folder = FolderPath;
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var existing = links.FirstOrDefault(l => l.ParticipantId == participantId);

        if (existing is null)
        {
            // Joining the day: create the link carrying the chosen group.
            var nextOrder = links.Count == 0 ? 1 : links.Max(l => l.Order) + 1;
            var link = new ParticipantDay
            {
                EventDayId = dayId,
                ParticipantId = participantId,
                Order = nextOrder,
                GroupId = groupId
            };
            await _eventStore.AddParticipantDayAsync(folder, link, cancellationToken);
            return link.Id;
        }

        // Already a member: update only the group, preserving the day's chip/order.
        await _eventStore.UpdateParticipantDayAsync(folder, new ParticipantDay
        {
            Id = existing.Id,
            EventDayId = dayId,
            ParticipantId = participantId,
            Order = existing.Order,
            GroupId = groupId,
            Chip = existing.Chip,
            StartTime = existing.StartTime,
            OutOfCompetition = existing.OutOfCompetition
        }, cancellationToken);
        return existing.Id;
    }

    public async Task SetParticipantDayChipAsync(Guid participantId, Guid dayId, string chip, CancellationToken cancellationToken = default)
    {
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
            l.Id != existing.Id && string.Equals(l.Chip.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
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

    public Task<int> SetParticipantDayChipsBatchAsync(
        IReadOnlyList<(Guid ParticipantId, Guid DayId, string Chip)> assignments,
        CancellationToken cancellationToken = default)
        => _eventStore.SetParticipantDayChipsBatchAsync(FolderPath, assignments, cancellationToken);

    public Task<int> SetParticipantNumbersBatchAsync(
        IReadOnlyList<(Guid ParticipantId, string Number)> assignments,
        CancellationToken cancellationToken = default)
        => _eventStore.SetParticipantNumbersBatchAsync(FolderPath, assignments, cancellationToken);

    public async Task SetParticipantDayStartTimeAsync(Guid participantId, Guid dayId, TimeSpan? startTime, CancellationToken cancellationToken = default)
    {
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
            .Where(r => string.Equals(r.ChipNumber.Trim(), chip, StringComparison.OrdinalIgnoreCase))
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
        var folder = FolderPath;
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var existing = links.FirstOrDefault(l => l.ParticipantId == participantId);
        if (existing is null)
            return;

        // Its own writer (UpdateParticipantDayAsync leaves the bonus column alone), so the debounced row
        // save can't wipe the correction set here. The recompute folds it into «Бали» (see ComputeDayResultsAsync).
        await _eventStore.SetParticipantDayBonusAsync(folder, existing.Id, bonus, cancellationToken);
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
        var days = await _eventStore.GetDaysAsync(folder, cancellationToken);
        var day = days.FirstOrDefault(d => d.Id == dayId);
        if (day is null)
            return new ResultProtocolData([]);

        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var groups = await _eventStore.GetGroupsAsync(folder, cancellationToken);
        var settings = await _eventStore.GetGroupDaySettingsAsync(folder, dayId, cancellationToken);
        var regions = await _eventStore.GetRegionsAsync(folder, cancellationToken);
        var clubs = await _eventStore.GetClubsAsync(folder, cancellationToken);
        var dusshes = await _eventStore.GetDusshesAsync(folder, cancellationToken);
        var controlPoints = await _eventStore.GetControlPointsAsync(folder, dayId, cancellationToken);
        var info = await _eventStore.GetCompetitionInfoAsync(folder, cancellationToken);

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
        var results = await ComputeDayResultsAsync(folder, day, cancellationToken, rankCalcs);

        // Bucket each member's row under its group; a member with no group is skipped (a protocol is by group).
        var rowsByGroup = new Dictionary<Guid, List<ResultProtocolRow>>();
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
            bucket.Add(new ResultProtocolRow(
                p.Number, p.FullName, p.BirthDate, club, region, dussh, p.Coach, p.Rank, p.Team.Trim(), result));
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
            var isTeam = strategy.Type == DisciplineType.Rogaine;

            // Control count: blank order ⇒ unknown (null). For the «mixed» pattern the count is what the
            // pattern requires (via the strategy), not a flat token count; other disciplines count the
            // running controls minus the day's start/finish codes.
            var controlCount = string.IsNullOrWhiteSpace(s.CourseOrder)
                ? (int?)null
                : discipline == DisciplineType.Mixed
                    ? strategy.ControlCount(s.CourseOrder)
                    : CountCourseControls(s.CourseOrder, countExcluded);
            var rows = rowsByGroup.TryGetValue(s.GroupId, out var b) ? b : [];
            // Course-setter: the group's per-day override wins; else fall back to the competition default.
            var (setter, setterCat) = ResolveCourseSetter(s, info);
            rankCalcs.TryGetValue(s.GroupId, out var rankCalc);
            sections.Add(new ResultProtocolGroup(
                name, s.Order, s.DistanceKm, controlCount, s.TimeLimitSeconds, isTeam, rows, setter, setterCat,
                rankCalc));
        }

        return new ResultProtocolData(sections, OfficialsFrom(info));
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
        foreach (var day in days)
            resultsByDay[day.Id] = await ComputeDayResultsAsync(folder, day, cancellationToken);

        // The group a member runs in: the group of their FIRST day membership (groups are normally constant
        // across days). The per-day result is keyed by day id. A member with no group on any day is skipped.
        var orderedDays = days.OrderBy(d => d.Number).ToList();
        var linksByParticipant = links
            .Where(l => l.GroupId is not null)
            .GroupBy(l => l.ParticipantId);

        // Group order: follow the group entity order (mirrors the day grid). Build a per-group member list.
        var groupOrder = groups.Select((g, i) => (g.Id, i)).ToDictionary(t => t.Id, t => t.i);
        var membersByGroup = new Dictionary<Guid, List<SummaryProtocolParticipant>>();

        foreach (var pg in linksByParticipant)
        {
            if (!byParticipant.TryGetValue(pg.Key, out var p))
                continue;

            // The member's group = the group of their earliest-numbered day membership.
            var firstLink = pg
                .OrderBy(l => orderedDays.FindIndex(d => d.Id == l.EventDayId))
                .First();
            if (firstLink.GroupId is not { } gid)
                continue;

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
            summaryGroups.Add(new SummaryProtocolGroup(name, order, members));
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
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var groups = await _eventStore.GetGroupsAsync(folder, cancellationToken);
        var settings = await _eventStore.GetGroupDaySettingsAsync(folder, dayId, cancellationToken);
        var regions = await _eventStore.GetRegionsAsync(folder, cancellationToken);
        var clubs = await _eventStore.GetClubsAsync(folder, cancellationToken);
        var dusshes = await _eventStore.GetDusshesAsync(folder, cancellationToken);
        var info = await _eventStore.GetCompetitionInfoAsync(folder, cancellationToken);

        var byParticipant = participants.ToDictionary(p => p.Id);
        var groupName = groups.ToDictionary(g => g.Id, g => g.Name);
        var regionName = regions.ToDictionary(r => r.Id, r => r.Name);
        var clubName = clubs.ToDictionary(c => c.Id, c => c.Name);
        var dusshName = dusshes.ToDictionary(d => d.Id, d => d.Name);

        // Bucket each member's row under its group; a member with no group is skipped (a start protocol is
        // built per group / per minute, and a runner without a group has no section to land in).
        var rowsByGroup = new Dictionary<Guid, List<StartProtocolRow>>();
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
            sections.Add(new StartProtocolGroup(name, s.Order, rows, setter, setterCat));
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

        // Latest read-out per chip (the "last read-out" rule), so a re-read chip uses its newest punches.
        var latestByChip = new Dictionary<string, FinishReadout>(StringComparer.OrdinalIgnoreCase);
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
            if (strategy.Type == DisciplineType.Rogaine)
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

            var splits = BuildSplitsForLink(inputs, gs, discipline, link, readout, teamCommon);
            var result = results.TryGetValue(link.Id, out var rr) ? rr : ParticipantDayResult.Empty;

            if (!rowsByGroup.TryGetValue(gid, out var bucket))
            {
                bucket = [];
                rowsByGroup[gid] = bucket;
            }
            bucket.Add(new SplitExportDataRow(p.Number, p.FullName, p.Team.Trim(), result, splits));
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
            var layout = isScored ? SplitsLayout.Scored : SplitsLayout.Ordered;

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
        DaySplitInputs inputs, GroupDaySettings? gs, DisciplineType discipline,
        ParticipantDay link, FinishReadout readout, IReadOnlySet<string>? teamCommon)
    {
        var expected = CourseControls(gs?.CourseOrder, inputs.StartFinishCodes, inputs.DisabledCodes);
        var disabledInCourse = DisabledInCourse(gs?.CourseOrder, inputs.StartFinishCodes, inputs.DisabledCodes);
        var punches = DecodePunchTimes(readout.PunchTimes)
            .Where(p => !inputs.StartFinishCodes.Contains(p.ControlCode.Trim()))
            .ToList();
        var start = readout.StartTime ?? CombineWithFinishDate(link.StartTime, readout.FinishTime);

        var context = new SplitsContext
        {
            ExpectedControls = expected,
            CourseOrderText = gs?.CourseOrder ?? string.Empty,
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
            TeamCommonControls = teamCommon
        };

        return _strategies.For(discipline).BuildSplits(context);
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
            string.Equals(TeamKeyFor(l, teamByParticipant), teamKey, StringComparison.OrdinalIgnoreCase)).ToList();

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
    /// Computes each participant's result on one day from the finish read-outs, keyed by the
    /// participant-day link id. Reuses the finish-evaluation pipeline (<see cref="EvaluateFinish"/>) and
    /// the scored splits tally (for rogaine «Бали»), then ranks the OK results within each group. A chip
    /// read more than once on the day resolves to its <b>latest</b> read-out (highest <c>Order</c>).
    /// </summary>
    /// <param name="rankCalcs">When non-null, receives the per-group rank-award derivation built by the ranks
    /// pass — used only by the results protocol to print its explanatory line. Null on every other caller.</param>
    private async Task<IReadOnlyDictionary<Guid, ParticipantDayResult>> ComputeDayResultsAsync(
        string folder, EventDay day, CancellationToken cancellationToken,
        Dictionary<Guid, GroupRankCalculation>? rankCalcs = null)
    {
        var dayId = day.Id;
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var readouts = await _eventStore.GetFinishReadoutsAsync(folder, dayId, cancellationToken);
        if (links.Count == 0)
            return new Dictionary<Guid, ParticipantDayResult>();

        var settings = await _eventStore.GetGroupDaySettingsAsync(folder, dayId, cancellationToken);
        var settingsByGroup = settings.ToDictionary(s => s.GroupId);
        var controlPoints = await _eventStore.GetControlPointsAsync(folder, dayId, cancellationToken);
        var startFinishCodes = new HashSet<string>(
            controlPoints
                .Where(c => c.Type is ControlPointType.Start or ControlPointType.Finish)
                .Select(c => c.Code.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var disabledCodes = DisabledCodesOf(controlPoints);
        var dayDefault = day.DefaultDiscipline;

        // Per-control point values, positions and map scale — needed only to tally rogaine «Бали».
        var pointsByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cp in controlPoints)
            if (cp.Points is { } pts)
                pointsByCode[cp.Code.Trim()] = pts;

        // Team name per participant — rogaine is a team format and the team lives on the participant
        // (competition-level), not on the per-day link, so resolve it once for the team ranking below.
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var teamByParticipant = participants.ToDictionary(p => p.Id, p => p.Team.Trim());

        // Latest read-out per chip (the "last read-out" rule): highest Order wins on a repeated chip.
        var latestByChip = new Dictionary<string, FinishReadout>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in readouts)
        {
            var key = r.ChipNumber.Trim();
            if (key.Length == 0)
                continue;
            if (!latestByChip.TryGetValue(key, out var cur) || r.Order > cur.Order)
                latestByChip[key] = r;
        }

        // First pass: evaluate every member with a chip that was read. Carry the group id and the sort
        // keys (result time, score) so the second pass can rank within each group. Rogaine members are
        // ranked as teams instead (see the team pass), so they go into `teamMembers`, not `ranking`.
        var results = new Dictionary<Guid, ParticipantDayResult>(links.Count);
        var ranking = new List<(Guid LinkId, Guid? GroupId, bool Scored, int Score, TimeSpan Time)>();
        var teamMembers = new List<TeamMember>();
        foreach (var link in links)
        {
            GroupDaySettings? gs0 = link.GroupId is { } gid0 && settingsByGroup.TryGetValue(gid0, out var s0) ? s0 : null;
            var discipline0 = gs0?.DisciplineOverride ?? dayDefault;
            var isRogaine = _strategies.For(discipline0).Type == DisciplineType.Rogaine;

            var chip = link.Chip.Trim();
            if (chip.Length == 0 || !latestByChip.TryGetValue(chip, out var readout))
            {
                // No read-out for this member's chip — but a manual override still shows a status. An "OK"
                // override is meaningless without a result (OK is the all-clear for someone who ran), so it
                // resolves to blank — matching how SetParticipantDayResultStatusAsync normalizes it.
                var noReadOverride = link.ResultStatusOverride == FinishStatus.Ok ? null : link.ResultStatusOverride;
                // No read-out ⇒ nothing to compute, so "auto" resolves to blank (FinishStatus.None).
                results[link.Id] = new ParticipantDayResult(
                    null, null, noReadOverride ?? FinishStatus.None, noReadOverride, FinishStatus.None,
                    null, null, null, HasReadout: false);
                // A rogaine member with no read-out makes their team incomplete — record it so the team
                // earns no place until every IN-COMPETITION member has finished. A поза конкурсом member who
                // never read out does NOT drop the team (RankRogaineTeams excludes them from the OK check).
                if (isRogaine)
                    teamMembers.Add(TeamMember.Incomplete(
                        link.Id, link.GroupId, TeamKeyFor(link, teamByParticipant), link.OutOfCompetition, link.Bonus));
                continue;
            }

            var (computed, _, resolvedStart) = EvaluateFinish(readout, link, settingsByGroup, startFinishCodes, disabledCodes, dayDefault);
            var status = link.ResultStatusOverride ?? computed;

            var resultTime = status == FinishStatus.Ok && resolvedStart is { } rs && readout.FinishTime is { } f
                ? f - rs
                : (TimeSpan?)null;

            GroupDaySettings? gs = gs0;
            var discipline = discipline0;
            HashSet<string>? punchedAllowed = null;
            int? score = null;
            if (_strategies.For(discipline).UsesControlPointPoints)
            {
                var detail = ScoreDetailFor(readout, gs, startFinishCodes, disabledCodes, pointsByCode, discipline, resolvedStart);
                // Fold in the judge's points correction («бонус»): the personal score is the computed net
                // plus this member's own bonus. For a teamed rogaine runner the team pass below replaces
                // this Score with the team net (which folds in the team-min bonus instead).
                score = detail.Net + (link.Bonus ?? 0);
                punchedAllowed = detail.Punched;
            }

            results[link.Id] = new ParticipantDayResult(
                readout.StartTime, readout.FinishTime, status, link.ResultStatusOverride, computed, resultTime, Place: null, score, HasReadout: true)
            {
                // Personal breakdown (the controls this chip scored). For a teamed rogaine runner the team
                // pass replaces this with the team's common controls so the tooltip matches the team Бали.
                ScoreBreakdown = punchedAllowed is { } pa ? ScoreLines(pa, pointsByCode) : [],
                Bonus = link.Bonus,
                // Personal-discipline поза конкурсом ⇒ marked «П/К», never placed (and excluded from `ranking`
                // below so it doesn't shift the others). Rogaine поза конкурсом is the team pass's job.
                OutOfCompetition = !isRogaine && link.OutOfCompetition
            };

            if (isRogaine)
            {
                // Rogaine: collect the member for the team tally. The elapsed time (finish − start) feeds
                // both the team's over-time penalty (max member time) and the tie-break (last finisher);
                // the bonus feeds the team-min points correction.
                var elapsed = resolvedStart is { } rs2 && readout.FinishTime is { } f2 ? f2 - rs2 : (TimeSpan?)null;
                teamMembers.Add(new TeamMember(
                    link.Id, link.GroupId, TeamKeyFor(link, teamByParticipant), link.OutOfCompetition,
                    status == FinishStatus.Ok, punchedAllowed ?? [], elapsed, gs, discipline, link.Bonus));
            }
            // Only OK results are placed; a non-positive result time can't be ranked by time. A поза
            // конкурсом runner is NOT placed and does NOT occupy a position — they are left out of the
            // ranking entirely so the in-competition runners are placed as if they were absent.
            else if (status == FinishStatus.Ok && !link.OutOfCompetition)
                ranking.Add((link.Id, link.GroupId, score is not null,
                    score ?? 0, resultTime is { } t && t > TimeSpan.Zero ? t : TimeSpan.MaxValue));
        }

        // Second pass: rank within each group. Rogaine (scored) orders by score desc then time asc;
        // every other format by time asc. Equal keys share a place; the next place skips the ties.
        foreach (var group in ranking.GroupBy(r => r.GroupId))
        {
            var ordered = group
                .OrderByDescending(r => r.Scored ? r.Score : 0)
                .ThenBy(r => r.Time)
                .ToList();

            var place = 0;
            var seen = 0;
            (int Score, TimeSpan Time)? prev = null;
            foreach (var r in ordered)
            {
                seen++;
                var key = (r.Scored ? r.Score : 0, r.Time);
                if (prev is null || prev.Value.Score != key.Item1 || prev.Value.Time != key.Item2)
                    place = seen; // a new (worse) key takes the running position; ties keep the prior place
                prev = key;
                results[r.LinkId] = results[r.LinkId] with { Place = place };
            }
        }

        // Rogaine team pass: score and place by team, then stamp the team's Бали/Місце onto every member.
        RankRogaineTeams(teamMembers, pointsByCode, results);

        // Points pass: award «Очки» from each group's effective points rule (its override, else the
        // competition default), keyed off the place/time/score just computed.
        await AwardPointsAsync(folder, links, settingsByGroup, info: null, results, cancellationToken);

        // Ranks pass: award «виконаний розряд» (Додаток 89) per group from its rank level, the course rank
        // (sum of the members' current-rank points) and the result-vs-leader percentage.
        await AwardRanksAsync(links, settingsByGroup, participants, dayDefault, results, cancellationToken, rankCalcs);

        return results;
    }

    /// <summary>
    /// Awards «Очки» (ranking points) for every member, per group, from the group's effective points rule
    /// (its per-day override, else the competition default). Stamps <see cref="ParticipantDayResult.Points"/>
    /// onto each result. A group with no effective rule, and any non-rankable member, is left blank.
    ///
    /// A <see cref="PointsRuleKind.Table"/> rule keys off the member's place; a formula rule additionally
    /// references the group leader's time/score (the placed runner whose place is 1) and the group size,
    /// so those are resolved once per group. Points rules live in the app database (shared across
    /// competitions); the per-group / competition-default assignment lives in the event database.
    /// </summary>
    private async Task AwardPointsAsync(
        string folder,
        IReadOnlyList<ParticipantDay> links,
        IReadOnlyDictionary<Guid, GroupDaySettings> settingsByGroup,
        CompetitionInfo? info,
        Dictionary<Guid, ParticipantDayResult> results,
        CancellationToken cancellationToken)
    {
        var rules = await _appStore.GetPointsRulesAsync(cancellationToken);
        if (rules.Count == 0)
            return;
        var ruleById = rules.ToDictionary(r => r.Id);

        info ??= await _eventStore.GetCompetitionInfoAsync(folder, cancellationToken);
        var defaultRuleId = info?.DefaultPointsRuleId;

        // The members of each group, paired with their computed result, so points can be ranked per group.
        var membersByGroup = new Dictionary<Guid, List<(Guid LinkId, ParticipantDayResult Result)>>();
        foreach (var link in links)
        {
            if (link.GroupId is not { } gid || !results.TryGetValue(link.Id, out var result))
                continue;
            if (!membersByGroup.TryGetValue(gid, out var list))
                membersByGroup[gid] = list = [];
            list.Add((link.Id, result));
        }

        foreach (var (groupId, members) in membersByGroup)
        {
            // Effective rule: the group's override, else the competition default. A group override of
            // Guid.Empty is an explicit "no points" choice — score nothing, never fall back to the default.
            // Unknown ids likewise award nothing.
            var ruleId = settingsByGroup.TryGetValue(groupId, out var gs) && gs.PointsRuleId is { } o
                ? o
                : defaultRuleId;
            if (ruleId is not { } id || id == Guid.Empty || !ruleById.TryGetValue(id, out var rule))
                continue;

            // Group context the formula references: the leader (place 1) time/score and the group size
            // (the number of placed finishers). A table rule ignores all of this and keys off place only.
            var placed = members.Where(m => m.Result.Place is not null).ToList();
            var groupSize = placed.Count;
            var leader = placed.FirstOrDefault(m => m.Result.Place == 1).Result;
            var leaderTime = leader?.ResultTime?.TotalSeconds;
            var leaderScore = leader?.Score;

            foreach (var (linkId, result) in members)
            {
                // Only a clean, in-competition finish earns points (a поза конкурсом runner, a problem
                // status, or a no-result member is left blank).
                if (result.Status != FinishStatus.Ok || result.OutOfCompetition)
                    continue;

                var input = new PointsRuleInput(
                    Place: result.Place,
                    ResultTimeSeconds: result.ResultTime?.TotalSeconds,
                    Score: result.Score,
                    LeaderTimeSeconds: leaderTime,
                    LeaderScore: leaderScore,
                    GroupSize: groupSize);
                var points = PointsRuleEvaluator.Evaluate(rule, input);
                if (points is not null)
                    results[linkId] = results[linkId] with { Points = points };
            }
        }
    }

    /// <summary>
    /// Awards «виконаний розряд» (Додаток 89, simplified) for every member, per group. A group whose level
    /// is <see cref="GroupRankLevel.None"/> awards nothing; a group failing the validity conditions (fewer
    /// placed members than the configured minimum, or fewer distinct regions) likewise awards nothing for
    /// anyone. Otherwise the course rank is the sum of the current-rank point values of the first N participants
    /// BY PLACE (the top finishers, N configurable default 12; from the seeded <c>SportRank</c> set); the top <c>MasterCount</c> placed runners get «МСУ»
    /// (adult groups only); the rest are awarded from the editable qualification table by their result as a
    /// percentage of the group leader's (time for time-based disciplines, score for point-scoring ones).
    /// </summary>
    /// <param name="rankCalcs">When non-null, receives the per-group rank-award derivation (course class, course
    /// rank, attainable ranks with their % thresholds + implied cut-offs) for every group that actually awards a
    /// rank — used by the results protocol to print the explanatory line. Null on the normal results path.</param>
    private async Task AwardRanksAsync(
        IReadOnlyList<ParticipantDay> links,
        IReadOnlyDictionary<Guid, GroupDaySettings> settingsByGroup,
        IReadOnlyList<Participant> participants,
        DisciplineType dayDefault,
        Dictionary<Guid, ParticipantDayResult> results,
        CancellationToken cancellationToken,
        Dictionary<Guid, GroupRankCalculation>? rankCalcs = null)
    {
        // Any group awarding a rank short-circuits the load; otherwise we never touch the app DB here.
        if (!settingsByGroup.Values.Any(s => s.RankLevel != GroupRankLevel.None))
            return;

        var rows = await _appStore.GetRankQualificationAsync(cancellationToken);
        var conditions = await _appStore.GetRankConditionsAsync(cancellationToken) ?? (3, 8, 12);
        var (minParticipants, minRegions, countForRank) = conditions;

        var ranks = await _appStore.GetRanksAsync(cancellationToken);
        // Key on a normalised rank name so a participant's rank still matches the seeded rank when the two use
        // look-alike letters (the Roman numerals I/II/III are Latin in the seed but often arrive as Cyrillic
        // «І» from a UOF/XML import — visually identical, different code points — which otherwise scores 0).
        var pointsByRankName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in ranks)
            pointsByRankName[NormalizeRankName(r.Name)] = r.Points;

        var rankByParticipant = participants.ToDictionary(p => p.Id, p => NormalizeRankName(p.Rank));
        var regionByParticipant = participants.ToDictionary(p => p.Id, p => p.RegionId);

        // Distinct regions are counted across the WHOLE day (every participant running it), not per group —
        // the «не менше восьми областей» condition is competition-wide. When it fails, NO group awards a rank.
        var dayRegions = links
            .Select(l => regionByParticipant.TryGetValue(l.ParticipantId, out var rid) ? rid : null)
            .Where(rid => rid is not null)
            .Distinct()
            .Count();
        if (dayRegions < minRegions)
            return;

        // The members of each group, paired with their computed result (mirrors AwardPointsAsync).
        var membersByGroup = new Dictionary<Guid, List<(ParticipantDay Link, ParticipantDayResult Result)>>();
        foreach (var link in links)
        {
            if (link.GroupId is not { } gid || !results.TryGetValue(link.Id, out var result))
                continue;
            if (!membersByGroup.TryGetValue(gid, out var list))
                membersByGroup[gid] = list = [];
            list.Add((link, result));
        }

        foreach (var (groupId, members) in membersByGroup)
        {
            if (!settingsByGroup.TryGetValue(groupId, out var gs) || gs.RankLevel == GroupRankLevel.None)
                continue;
            var level = gs.RankLevel;

            // Per-group validity (Додаток 89, п.7 — «не менше трьох»): too few participants ⇒ no ranks
            // for this group. (The distinct-regions condition is checked competition-wide above.)
            if (members.Count < minParticipants)
                continue;

            // Course rank: take the first N participants BY PLACE (the top finishers, not the highest-ranked),
            // N configurable (default 12), and sum their CURRENT-rank point values. Members with a place come
            // first by ascending place; the unplaced (DNS/DSQ/поза конкурсом — no place) sort last so they only
            // fill the count after every placed runner. This mirrors the protocol's «перші N за місцем» rule.
            var rank = (int)Math.Floor(members
                .OrderBy(m => m.Result.Place ?? int.MaxValue)
                .Take(countForRank)
                .Select(m => rankByParticipant.TryGetValue(m.Link.ParticipantId, out var rn)
                    && pointsByRankName.TryGetValue(rn, out var pts) ? pts : 0.0)
                .Sum());

            var discipline = gs.DisciplineOverride ?? dayDefault;
            var pointsBased = _strategies.For(discipline).UsesControlPointPoints;

            // Group leader (place 1) context the percentage is measured against.
            var placed = members.Where(m => m.Result.Place is not null).ToList();
            var leader = placed.FirstOrDefault(m => m.Result.Place == 1).Result;
            var leaderTime = leader?.ResultTime?.TotalSeconds;
            var leaderScore = leader?.Score;

            var masterCount = level == GroupRankLevel.Adult ? gs.MasterCount ?? 0 : 0;

            // The derivation line for the protocol: the applicable bracket's attainable ranks (highest first),
            // each with its % threshold and the cut-off it implies against the leader's result. Only built when
            // a leader's reference result exists (no leader ⇒ no percentages to show).
            if (rankCalcs is not null &&
                RankQualificationEvaluator.ApplicableRow(rows, rank, level) is { } bracket)
            {
                var leaderRef = pointsBased ? leaderScore : leaderTime;
                if (leaderRef is { } reference && reference > 0)
                {
                    var attainable = RankQualificationEvaluator.AttainableRanks(bracket, level, pointsBased);
                    if (attainable.Count > 0)
                    {
                        var entries = attainable
                            .Select(a => new RankCalculationEntry(
                                a.Name, a.Percent,
                                CutoffTimeSeconds: pointsBased ? null : reference * a.Percent / 100.0,
                                CutoffScore: pointsBased ? (int)Math.Round(reference * a.Percent / 100.0) : null))
                            .ToList();
                        rankCalcs[groupId] = new GroupRankCalculation(
                            CourseClass: attainable[0].Name, Rank: rank, Entries: entries);
                    }
                }
            }

            foreach (var (link, result) in members)
            {
                // Only a clean, in-competition finish can earn a rank.
                if (result.Status != FinishStatus.Ok || result.OutOfCompetition)
                    continue;

                // Master sporta: the top-N placed runners (adult groups only).
                if (masterCount > 0 && result.Place is { } place && place <= masterCount)
                {
                    results[link.Id] = results[link.Id] with { AwardedRank = RankQualificationEvaluator.Master };
                    continue;
                }

                // The rest: the qualification table, keyed off the result as a % of the leader's.
                double? percent = pointsBased
                    ? (leaderScore is { } ls && ls > 0 && result.Score is { } sc ? (double)sc / ls * 100.0 : null)
                    : (leaderTime is { } lt && lt > 0 && result.ResultTime is { } rt ? rt.TotalSeconds / lt * 100.0 : null);
                if (percent is not { } pct)
                    continue;

                var awarded = RankQualificationEvaluator.Award(rows, rank, level, pointsBased, pct);
                if (awarded is not null)
                    results[link.Id] = results[link.Id] with { AwardedRank = awarded };
            }
        }
    }

    // Normalises a rank name for matching: trims, then folds the Cyrillic «І» that looks identical to the Latin
    // «I» used in the seeded rank names (I / II / III / I-ю …). Ukrainian rank strings carry the Roman numerals
    // as Cyrillic «І» (U+0406) far more often than Latin «I» (U+0049) — visually identical, different code
    // points — so without this fold an imported «ІІ» розряд would miss the seeded «II» and score 0. The «-ю»
    // junior suffix is already Cyrillic on both sides, so it needs no fold. Comparison is otherwise
    // case-insensitive (the dictionary uses OrdinalIgnoreCase).
    private static string NormalizeRankName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return trimmed;
        var chars = trimmed.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            chars[i] = chars[i] switch
            {
                'І' => 'I',   // U+0406 Cyrillic capital І → Latin I
                'і' => 'I',   // U+0456 Cyrillic small і → Latin I
                _ => chars[i]
            };
        return new string(chars);
    }

    // The trimmed team name for a member's participant; empty when the participant has no team (поза
    // конкурсом for rogaine).
    private static string TeamKeyFor(ParticipantDay link, IReadOnlyDictionary<Guid, string> teamByParticipant)
        => teamByParticipant.TryGetValue(link.ParticipantId, out var t) ? t : string.Empty;

    /// <summary>
    /// One rogaine member's contribution to the team tally. A team is the members sharing the same
    /// (<see cref="GroupId"/>, <see cref="TeamKey"/>) on the day. <see cref="Ok"/> false (a problem status,
    /// or — via <see cref="Incomplete"/> — no read-out at all) makes the whole team unplaced (rules 1/3);
    /// <see cref="Punched"/> is this member's set of visited allowed controls (intersected across the team
    /// for the team score, rule 2); <see cref="Elapsed"/> feeds the team penalty (max time, rule 2) and the
    /// tie-break (last finisher, rule 5).
    /// </summary>
    private sealed record TeamMember(
        Guid LinkId, Guid? GroupId, string TeamKey, bool OutOfCompetition, bool Ok,
        IReadOnlySet<string> Punched, TimeSpan? Elapsed, GroupDaySettings? Settings, DisciplineType Discipline,
        int? Bonus)
    {
        /// <summary>A member with no read-out: present in the team but not finished, so the team is incomplete
        /// (unless they are поза конкурсом, in which case they don't drop the team).</summary>
        public static TeamMember Incomplete(Guid linkId, Guid? groupId, string teamKey, bool outOfCompetition, int? bonus) =>
            new(linkId, groupId, teamKey, outOfCompetition, Ok: false,
                Punched: new HashSet<string>(StringComparer.OrdinalIgnoreCase), Elapsed: null, Settings: null,
                Discipline: DisciplineType.Rogaine, Bonus: bonus);
    }

    // Ranks rogaine members by team and writes the team's net «Бали» and place onto every member.
    // A control scores for the team only when every IN-COMPETITION member punched it (intersection); the
    // over-time penalty is computed once from the team's largest member time (all members, поза конкурсом
    // included); the team time (tie-break) is the last finisher. A member with no team is поза конкурсом
    // (kept teamless, no place). A «поза конкурсом» member stays in the team and earns the team's Бали/Місце
    // but does NOT count toward the team score or drop the team if non-OK; only when EVERY member is поза
    // конкурсом does the whole team go unplaced (rules 1–5).
    private void RankRogaineTeams(
        List<TeamMember> members,
        IReadOnlyDictionary<string, int> pointsByCode,
        Dictionary<Guid, ParticipantDayResult> results)
    {
        if (members.Count == 0)
            return;

        // Members with no team run поза конкурсом — keep their personal score, no place. (Done by simply
        // not ranking them; their result already has Place == null.) Members WITH a team stay in the team
        // even when поза конкурсом — they just don't count toward the score (filtered to `scoring` below).
        var teams = members
            .Where(m => m.TeamKey.Length > 0)
            .GroupBy(m => (m.GroupId, m.TeamKey));

        // Each ranked team carries its net score and tie-break time, grouped back by GroupId for placing.
        var ranked = new List<(Guid? GroupId, int Net, TimeSpan Time, List<Guid> LinkIds)>();
        foreach (var team in teams)
        {
            var roster = team.ToList();
            var linkIds = roster.Select(m => m.LinkId).ToList();

            // The members who actually count toward the team result. A «поза конкурсом» runner is on the
            // team (still shown, still gets the team place) but is excluded from the score/OK checks below.
            var scoring = roster.Where(m => !m.OutOfCompetition).ToList();

            // When EVERY member is поза конкурсом the whole team runs поза конкурсом — no place.
            if (scoring.Count == 0)
                continue;

            // Rule 1/3: a team with any unfinished or non-OK IN-COMPETITION member is not placed (still
            // keeps personal Бали). A поза конкурсом member's status never drops the team.
            if (scoring.Any(m => !m.Ok))
                continue;

            // Rule 2: a control scores only when every in-competition member punched it — the intersection
            // of their sets. A поза конкурсом member's punches do not count toward the team total.
            var common = new HashSet<string>(scoring[0].Punched, StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < scoring.Count; i++)
                common.IntersectWith(scoring[i].Punched);
            var gross = common.Sum(code => pointsByCode.TryGetValue(code, out var p) ? p : 0);

            // Rule 2: one over-time penalty per team, measured from the largest member time — including a
            // поза конкурсом member (their time still counts toward the team penalty / tie-break). Reuse the
            // strategy's penalty math by feeding it a synthetic read-out whose elapsed = the max time.
            var maxElapsed = roster.Select(m => m.Elapsed).Max();
            var settings = scoring.Select(m => m.Settings).FirstOrDefault(s => s is not null);
            var afterPenalty = ApplyTeamPenalty(gross, maxElapsed, settings, scoring[0].Discipline);

            // Points correction («бонус»): the smallest bonus ENTERED by an in-competition team member (an
            // un-entered member does not drag it to 0). Null when nobody entered one ⇒ no correction.
            var teamBonus = scoring.Select(m => m.Bonus).Where(b => b.HasValue).Select(b => b!.Value).DefaultIfEmpty().Min();
            var teamBonusOrNull = scoring.Any(m => m.Bonus.HasValue) ? teamBonus : (int?)null;
            var net = afterPenalty + teamBonus;

            // Rule 5: tie-break by the last finisher's time (the largest member time).
            var time = maxElapsed is { } e && e > TimeSpan.Zero ? e : TimeSpan.MaxValue;
            ranked.Add((team.Key.GroupId, net, time, linkIds));

            // The team's net Бали shows on every member row, with the per-control breakdown (the common
            // controls), the over-time penalty and the points correction for the «Бали» tooltip.
            var breakdown = ScoreLines(common, pointsByCode);
            var penalty = gross - afterPenalty;
            foreach (var id in linkIds)
                results[id] = results[id] with
                {
                    Score = net,
                    ScoreBreakdown = breakdown,
                    ScoreGross = penalty > 0 ? gross : null,
                    ScorePenalty = penalty > 0 ? penalty : null,
                    Bonus = teamBonusOrNull
                };
        }

        // Place teams within each group: net desc, then team time asc; ties share a place, next skips ties.
        foreach (var group in ranked.GroupBy(r => r.GroupId))
        {
            var ordered = group.OrderByDescending(r => r.Net).ThenBy(r => r.Time).ToList();
            var place = 0;
            var seen = 0;
            (int Net, TimeSpan Time)? prev = null;
            foreach (var team in ordered)
            {
                seen++;
                var key = (team.Net, team.Time);
                if (prev is null || prev.Value.Net != key.Net || prev.Value.Time != key.Time)
                    place = seen;
                prev = key;
                foreach (var id in team.LinkIds)
                    results[id] = results[id] with { Place = place };
            }
        }
    }

    // Turns a set of scored control codes into ordered ScoreLines (ascending control number, then text)
    // each carrying its point value — the per-control «Бали» breakdown shown in the column tooltip.
    private static IReadOnlyList<ScoreLine> ScoreLines(
        IReadOnlySet<string> codes, IReadOnlyDictionary<string, int> pointsByCode)
        => codes
            .OrderBy(c => long.TryParse(c, out var n) ? n : long.MaxValue)
            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ScoreLine(c, pointsByCode.TryGetValue(c, out var p) ? p : 0))
            .ToList();

    // The team's net «Бали» after the over-time penalty, computed by the discipline strategy so the
    // rounding/floor rules stay in one place. Feeds it a synthetic context whose finish − start equals the
    // team's largest member time and whose gross is the intersection total (modelled as one control worth
    // `gross`), so BuildSplits applies the same penalty it does per chip.
    private int ApplyTeamPenalty(int gross, TimeSpan? maxElapsed, GroupDaySettings? settings, DisciplineType discipline)
    {
        if (maxElapsed is not { } elapsed || settings?.TimeLimitSeconds is null)
            return gross; // no resolvable time or no limit ⇒ no penalty

        var start = DateTimeOffset.UnixEpoch;
        var context = new SplitsContext
        {
            ExpectedControls = ["__team__"],
            Punches = [new ChipPunch("__team__", start)],
            PointsByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["__team__"] = gross },
            StartTime = start,
            FinishTime = start + elapsed,
            TimeLimit = TimeSpan.FromSeconds(settings.TimeLimitSeconds.Value),
            PenaltyPerMinute = settings.PenaltyPerMinute
        };
        return _strategies.For(discipline).BuildSplits(context).TotalPoints;
    }

    // Total «Бали» (points) for a read-out on a point-scoring day: the scored splits tally over the
    // group's allowed controls. Mirrors the SplitsContext the finish-splits panel builds, minus the
    // geometry (distance/pace) it doesn't need.
    private int ScoreFor(
        FinishReadout readout,
        GroupDaySettings? gs,
        HashSet<string> startFinishCodes,
        HashSet<string> disabledCodes,
        IReadOnlyDictionary<string, int> pointsByCode,
        DisciplineType discipline,
        DateTimeOffset? resolvedStart)
        => ScoreDetailFor(readout, gs, startFinishCodes, disabledCodes, pointsByCode, discipline, resolvedStart).Net;

    // The personal score plus the set of allowed controls the chip actually punched (first punch each),
    // used by the rogaine team tally (a control scores for the team only when every member's set has it).
    private (int Net, HashSet<string> Punched) ScoreDetailFor(
        FinishReadout readout,
        GroupDaySettings? gs,
        HashSet<string> startFinishCodes,
        HashSet<string> disabledCodes,
        IReadOnlyDictionary<string, int> pointsByCode,
        DisciplineType discipline,
        DateTimeOffset? resolvedStart)
    {
        var expected = CourseControls(gs?.CourseOrder, startFinishCodes, disabledCodes);
        var punches = DecodePunchTimes(readout.PunchTimes)
            .Where(p => !startFinishCodes.Contains(p.ControlCode.Trim()))
            .ToList();
        var context = new SplitsContext
        {
            ExpectedControls = expected,
            Punches = punches,
            PointsByCode = pointsByCode,
            // The resolved start (chip read-out start, else the assigned start paired with the finish date)
            // — the same start the finish evaluation uses — so the over-time penalty measures real elapsed.
            StartTime = resolvedStart,
            FinishTime = readout.FinishTime,
            TimeLimit = gs?.TimeLimitSeconds is { } secs ? TimeSpan.FromSeconds(secs) : null,
            PenaltyPerMinute = gs?.PenaltyPerMinute
        };
        var net = _strategies.For(discipline).BuildSplits(context).TotalPoints;

        // The allowed controls this chip visited (intersection of punched and allowed), for the team tally.
        var allowed = new HashSet<string>(expected.Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase);
        var punched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in punches)
        {
            var code = p.ControlCode.Trim();
            if (code.Length > 0 && allowed.Contains(code))
                punched.Add(code);
        }
        return (net, punched);
    }

    public async Task<DrawPrepData> GetDrawPrepDataAsync(Guid dayId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return new DrawPrepData([]);

        var folder = FolderPath;
        var settings = await _eventStore.GetGroupDaySettingsAsync(folder, dayId, cancellationToken);
        var groups = await _eventStore.GetGroupsAsync(folder, cancellationToken);
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var regions = await _eventStore.GetRegionsAsync(folder, cancellationToken);
        var clubs = await _eventStore.GetClubsAsync(folder, cancellationToken);

        var groupName = groups.ToDictionary(g => g.Id, g => g.Name);
        var byParticipant = participants.ToDictionary(p => p.Id);
        var regionName = regions.ToDictionary(r => r.Id, r => r.Name);
        var clubName = clubs.ToDictionary(c => c.Id, c => c.Name);

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
            list.Add(new DrawParticipant(link.Id, p.Id, p.FullName, p.Number, region, club, p.Team));
        }

        // One DrawGroup per group on the day, in the day grid order, carrying its first control point.
        var drawGroups = new List<DrawGroup>(settings.Count);
        foreach (var s in settings)
        {
            if (!groupName.TryGetValue(s.GroupId, out var name))
                continue;
            var members = membersByGroup.TryGetValue(s.GroupId, out var m) ? m : [];
            var controls = CourseControlsOf(s.CourseOrder);
            drawGroups.Add(new DrawGroup(
                s.GroupId, name, controls.Count > 0 ? controls[0] : string.Empty, controls, members));
        }

        return new DrawPrepData(drawGroups);
    }

    public async Task<StartOrderData> GetStartOrderDataAsync(Guid dayId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return new StartOrderData([]);

        var folder = FolderPath;
        var settings = await _eventStore.GetGroupDaySettingsAsync(folder, dayId, cancellationToken);
        var groups = await _eventStore.GetGroupsAsync(folder, cancellationToken);
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var regions = await _eventStore.GetRegionsAsync(folder, cancellationToken);
        var clubs = await _eventStore.GetClubsAsync(folder, cancellationToken);

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

    /// <summary>
    /// Parses the ordered control-point codes out of a free-text course order such as "S1 31 32 33 F". Start
    /// and finish markers (pure-letter tokens like "S"/"Start"/"F"/"Фініш") are dropped; every token that
    /// carries a digit is kept, in order. Returns an empty list when the order is blank or has no controls.
    /// The first element is the opening control used to cluster start groups; the whole list is used to detect
    /// groups that run an identical distance.
    /// </summary>
    private static IReadOnlyList<string> CourseControlsOf(string? courseOrder)
    {
        if (string.IsNullOrWhiteSpace(courseOrder))
            return [];

        // A «mixed» order pattern (with <…>/[N …] blocks) flattens to its leaf controls; a plain order is
        // split on the usual separators. Either way, keep only digit-bearing tokens (drop S/Start/F markers).
        IEnumerable<string> tokens = courseOrder.IndexOfAny(['<', '[']) >= 0
            ? CourseOrderCodes(courseOrder)
            : courseOrder.Split(
                [' ', ',', ';', '\t', '-', '>'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Any token that contains a digit is a real control (skips pure-letter S/Start/F markers); keep order.
        return tokens.Where(t => t.Any(char.IsDigit)).ToList();
    }

    public Task<int> SaveDrawStartTimesAsync(
        IReadOnlyList<DrawStartAssignment> assignments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        if (_session.CurrentEvent is null || assignments.Count == 0)
            return Task.FromResult(0);

        var batch = assignments.Select(a => (a.LinkId, a.StartTime)).ToList();
        return _eventStore.SetParticipantDayStartTimesBatchAsync(FolderPath, batch, cancellationToken);
    }

    public async Task<IReadOnlyList<FinishReadoutRow>> GetFinishReadoutRowsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || _session.CurrentDay is null)
            return [];

        var folder = FolderPath;
        var dayId = CurrentDayId;
        var readouts = await _eventStore.GetFinishReadoutsAsync(folder, dayId, cancellationToken);

        // Resolve each chip against the day's participants (chip → number/name/group), case-insensitive.
        var links = await _eventStore.GetParticipantDaysAsync(folder, dayId, cancellationToken);
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var groups = await _eventStore.GetGroupsAsync(folder, cancellationToken);
        var byId = participants.ToDictionary(p => p.Id);
        var groupName = groups.ToDictionary(g => g.Id, g => g.Name);

        // For the order-check: each group's prescribed course + settings, and the day's start/finish
        // control codes (so they're dropped from both the expected course and the punched sequence).
        var settings = await _eventStore.GetGroupDaySettingsAsync(folder, dayId, cancellationToken);
        var settingsByGroup = settings.ToDictionary(s => s.GroupId);
        var controlPoints = await _eventStore.GetControlPointsAsync(folder, dayId, cancellationToken);
        var startFinishCodes = new HashSet<string>(
            controlPoints
                .Where(c => c.Type is ControlPointType.Start or ControlPointType.Finish)
                .Select(c => c.Code.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var disabledCodes = DisabledCodesOf(controlPoints);
        var dayDefault = CurrentDayDefaultDiscipline;

        // Per-control point values — needed only to tally the «Бали» column on point-scoring days.
        var pointsByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cp in controlPoints)
            if (cp.Points is { } pts)
                pointsByCode[cp.Code.Trim()] = pts;

        // A chip on a day is unique (enforced when assigning), so first match by trimmed chip wins.
        var holderByChip = new Dictionary<string, ParticipantDay>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            var key = link.Chip.Trim();
            if (key.Length > 0)
                holderByChip.TryAdd(key, link);
        }

        var rows = new List<FinishReadoutRow>(readouts.Count);
        foreach (var r in readouts)
        {
            if (holderByChip.TryGetValue(r.ChipNumber.Trim(), out var link) && byId.TryGetValue(link.ParticipantId, out var p))
            {
                var group = link.GroupId is { } gid && groupName.TryGetValue(gid, out var gn) ? gn : string.Empty;
                var (status, detail, resolvedStart) = EvaluateFinish(r, link, settingsByGroup, startFinishCodes, disabledCodes, dayDefault);
                // A judge's manual status override wins over the computed one (and clears its detail).
                if (r.ManualStatus is { } manual)
                {
                    status = manual;
                    detail = string.Empty;
                }
                var elapsed = resolvedStart is { } s && r.FinishTime is { } f ? f - s : (TimeSpan?)null;
                // «Бали»: tally the scored splits when this row's discipline scores points (rogaine); null otherwise.
                GroupDaySettings? gs = link.GroupId is { } sgid && settingsByGroup.TryGetValue(sgid, out var sset) ? sset : null;
                var discipline = gs?.DisciplineOverride ?? dayDefault;
                int? score = _strategies.For(discipline).UsesControlPointPoints
                    ? ScoreFor(r, gs, startFinishCodes, disabledCodes, pointsByCode, discipline, resolvedStart)
                    : null;
                rows.Add(new FinishReadoutRow(r.Id, r.Order, r.ChipNumber, r.StartTime, r.FinishTime,
                    IsKnown: true, p.Number, p.FullName, group, status, detail, resolvedStart, elapsed, score));
            }
            else
            {
                // An unrecognised chip has no group/course to judge against — no computed status — but a
                // manual override still shows (a judge may rule on an unknown chip).
                rows.Add(new FinishReadoutRow(r.Id, r.Order, r.ChipNumber, r.StartTime, r.FinishTime,
                    IsKnown: false, ParticipantNumber: string.Empty, FullName: string.Empty, GroupName: string.Empty,
                    Status: r.ManualStatus ?? FinishStatus.None, StatusDetail: string.Empty, ResolvedStartTime: null, Elapsed: null));
            }
        }
        return rows;
    }

    public async Task<bool> CurrentDayUsesScoreAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || _session.CurrentDay is null)
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
            string.Equals(TeamKeyFor(l, teamByParticipant), teamKey, StringComparison.OrdinalIgnoreCase)).ToList();

        // A поза конкурсом member's punches don't count toward the team total, so they don't shape the
        // common-controls annotation either. Drop them unless the whole team is поза конкурсом (then there
        // is nothing scoring to intersect, so fall back to all mates so the annotation still has a basis).
        var scoring = mates.Where(m => !m.OutOfCompetition).ToList();
        if (scoring.Count > 0)
            mates = scoring;

        var readouts = await _eventStore.GetFinishReadoutsAsync(folder, dayId, cancellationToken);
        var latestByChip = new Dictionary<string, FinishReadout>(StringComparer.OrdinalIgnoreCase);
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

    public async Task<SplitsView?> GetFinishSplitsAsync(Guid readoutId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || _session.CurrentDay is null)
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
            string.Equals(l.Chip.Trim(), readout.ChipNumber.Trim(), StringComparison.OrdinalIgnoreCase));

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
        if (link is not null && _strategies.For(discipline).Type == DisciplineType.Rogaine)
            teamCommon = await TeamCommonControlsAsync(
                folder, dayId, link, links, settingsByGroup, startFinishCodes, disabledCodes, cancellationToken);

        // Expected = course controls minus start/finish; punches = read punches (with times) minus the
        // same. An unknown chip (no link) has no prescribed course, so the expected list is empty and
        // every punch reads off-course. Start = chip start when present, else the assigned start (only
        // when a holder is known) paired with the finish's date.
        var expected = CourseControls(gs?.CourseOrder, startFinishCodes, disabledCodes);
        var disabledInCourse = DisabledInCourse(gs?.CourseOrder, startFinishCodes, disabledCodes);
        var punches = DecodePunchTimes(readout.PunchTimes)
            .Where(p => !startFinishCodes.Contains(p.ControlCode.Trim()))
            .ToList();
        var start = readout.StartTime ?? CombineWithFinishDate(link?.StartTime, readout.FinishTime);

        var context = new SplitsContext
        {
            ExpectedControls = expected,
            CourseOrderText = gs?.CourseOrder ?? string.Empty,
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
            TeamCommonControls = teamCommon
        };

        // A known holder gets their group's discipline shape. An unknown chip has no group/course, so we
        // always use the ordered (set-course) layout: with no prescribed controls it degrades to a flat
        // list of every punch in chip order — the raw passage, which is what's wanted for an unknown chip
        // (the scored layout would instead drop every punch as "not an allowed control" and show nothing).
        var strategy = link is null ? _strategies.For(DisciplineType.SetCourse) : _strategies.For(discipline);
        return strategy.BuildSplits(context);
    }

    public async Task<SplitPrintDocument?> GetSplitPrintDocumentAsync(Guid readoutId, CancellationToken cancellationToken = default)
    {
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
            string.Equals(c.Number.Trim(), row.ChipNumber.Trim(), StringComparison.OrdinalIgnoreCase));

        // Manual bonus («Бонус») the judge set on this runner's day record, if any: it corrects the score the
        // same way the result columns do, so the slip prints the summed total and spells the bonus out.
        var links = await _eventStore.GetParticipantDaysAsync(FolderPath, CurrentDayId, cancellationToken);
        var bonus = links.FirstOrDefault(l =>
            string.Equals(l.Chip.Trim(), row.ChipNumber.Trim(), StringComparison.OrdinalIgnoreCase))?.Bonus;

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
            : BuildScoredRows(view.Entries);

        // Start/finish wall-clock and course length come from the ordered passage (start marker → finish
        // marker). The average pace is the result time over the total straight-line distance.
        var startClock = string.Empty;
        var finishClock = string.Empty;
        decimal totalKm = 0m;
        if (view.Layout == SplitsLayout.Ordered)
        {
            foreach (var p in view.Passage)
            {
                if (p.Kind == PassageKind.Start && p.Time is { } st)
                    startClock = st.ToString("HH\\:mm\\:ss", CultureInfo.InvariantCulture);
                else if (p.Kind == PassageKind.Finish && p.Time is { } ft)
                    finishClock = ft.ToString("HH\\:mm\\:ss\\.f", CultureInfo.InvariantCulture);
                if (p.LegKm is { } km && km > 0m)
                    totalKm += km;
            }
        }

        var resultElapsed = row.Elapsed is { } e && e >= TimeSpan.Zero ? e : (TimeSpan?)null;

        // When a set course broke the order (MP), append the prescribed (correct) order so the slip shows
        // what should have been done, with the first untaken control flagged as where the route went wrong.
        var correctOrder = view.Layout == SplitsLayout.Ordered && row.Status == FinishStatus.Mp
            ? BuildCorrectOrderRows(view.Expected)
            : [];

        // The result columns add the manual bonus on top of the discipline's net points; the slip mirrors that
        // so the printed «Сума балів» matches the protocol. A bonus only applies to a point-scoring view.
        var isScored = view.HasPoints || view.Layout == SplitsLayout.Scored;
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
            ResultText = resultElapsed is { } re ? re.ToString("h\\:mm\\:ss") : string.Empty,
            StatusText = status,
            StatusDetail = row.StatusDetail,
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
            Rows = rows,
            // Rogaine (Ordered + points) keeps the full geometry columns plus a бал column; the geometry-less
            // choice/score formats (Scored layout) use the compact layout instead.
            HasGeometry = view.Layout == SplitsLayout.Ordered && view.HasPoints,
            CorrectOrder = correctOrder
        };
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

    // Maps the scored layout (score/choice/rogaine) to printable rows. The passage order is preserved and a
    // points value is carried (non-null), so the renderer adds the бал column.
    private static IReadOnlyList<SplitPrintRow> BuildScoredRows(IReadOnlyList<ScoreEntry> entries)
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
                PointsText: entry.Visited && entry.Points != 0 ? $"+{entry.Points}" : string.Empty,
                OnCourse: entry.Visited));
        }
        return list;
    }

    // Layer-neutral duration formatting matching the splits panel: "m:ss" under an hour, "h:mm:ss" above,
    // blank for null/negative.
    private static string FormatDuration(TimeSpan? span) => span is { } s && s >= TimeSpan.Zero
        ? (s.TotalHours >= 1 ? s.ToString("h\\:mm\\:ss") : s.ToString("m\\:ss"))
        : string.Empty;

    // Distance in whole metres (unit lives in the header); blank for null/zero. Used for both the per-leg
    // column and the course total on the header line.
    private static string FormatDistanceMetres(decimal? km) => km is { } d && d > 0m
        ? Math.Round(d * 1000m).ToString("0", CultureInfo.InvariantCulture)
        : string.Empty;

    // Pace "m:ss" from seconds-per-km (unit /км lives in the header); blank for null/non-positive.
    private static string FormatPace(double? secondsPerKm)
    {
        if (secondsPerKm is not { } s || s <= 0 || double.IsInfinity(s))
            return string.Empty;
        var span = TimeSpan.FromSeconds(Math.Round(s));
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }

    // Average pace (seconds per km) over the whole result: result time ÷ total course distance.
    private static double? AvgPaceSecondsPerKm(TimeSpan? result, decimal totalKm) =>
        result is { } r && r > TimeSpan.Zero && totalKm > 0m
            ? r.TotalSeconds / (double)totalKm
            : null;

    // Builds the FinishContext for one read-out + its holder and asks the group's discipline strategy
    // for the status. Start time is the chip's read-out start when present, else the assigned start.
    // Also returns the resolved start so the caller can derive the finish − start elapsed duration.
    private (FinishStatus Status, string Detail, DateTimeOffset? ResolvedStartTime) EvaluateFinish(
        FinishReadout readout,
        ParticipantDay link,
        IReadOnlyDictionary<Guid, GroupDaySettings> settingsByGroup,
        HashSet<string> startFinishCodes,
        HashSet<string> disabledCodes,
        DisciplineType dayDefault)
    {
        GroupDaySettings? gs = link.GroupId is { } gid && settingsByGroup.TryGetValue(gid, out var s) ? s : null;
        var discipline = gs?.DisciplineOverride ?? dayDefault;

        // Expected = course-order codes minus the day's start/finish controls AND its disabled («проблемні»)
        // controls, so missing a disabled control is never an MP. Punched keeps disabled codes — a chip may
        // still have punched one; for a set course it just reads as an extra, for a scored one it's ignored
        // because it's no longer in the allowed set. We only drop the start/finish boxes from the punches.
        var expected = CourseControls(gs?.CourseOrder, startFinishCodes, disabledCodes);
        var punched = SplitCodes(readout.Punches).Where(c => !startFinishCodes.Contains(c)).ToList();

        // Start = chip read-out start when present, else the assigned start (a time-of-day) paired with
        // the finish's date so finish − start is a meaningful duration.
        var start = readout.StartTime ?? CombineWithFinishDate(link.StartTime, readout.FinishTime);

        var context = new FinishContext
        {
            ExpectedControls = expected,
            CourseOrderText = gs?.CourseOrder ?? string.Empty,
            PunchedControls = punched,
            StartTime = start,
            FinishTime = readout.FinishTime,
            TimeLimit = gs?.TimeLimitSeconds is { } secs ? TimeSpan.FromSeconds(secs) : null
        };

        var result = _strategies.For(discipline).EvaluateFinish(context);
        return (result.Status, result.Detail, start);
    }

    // Pairs an assigned start time-of-day with the read-out finish's date, so the time-limit check
    // compares two same-day timestamps. Null when either part is missing.
    private static DateTimeOffset? CombineWithFinishDate(TimeSpan? startOfDay, DateTimeOffset? finish) =>
        startOfDay is { } t && finish is { } f ? new DateTimeOffset(f.Date + t, f.Offset) : null;

    private static IEnumerable<string> SplitCodes(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // The control codes referenced by a course order, in reading order (duplicates kept). For a plain order
    // ("S1 31 32 F") this is just the tokens; for a «mixed» order <b>pattern</b> ("&lt;41 42&gt; [2 45 46]")
    // it flattens the &lt;…&gt;/[N …] blocks to their leaf controls (the bracket/amount tokens are not codes).
    // Every course-order consumer routes through this so a pattern is never mistaken for literal tokens.
    private static List<string> CourseOrderCodes(string? courseOrder) =>
        string.IsNullOrWhiteSpace(courseOrder)
            ? []
            : courseOrder.IndexOfAny(['<', '[']) < 0
                ? SplitCodes(courseOrder).ToList()
                : Disciplines.CoursePattern.CoursePattern.Parse(courseOrder).ControlCodes.ToList();

    // The day's disabled («проблемні») control codes (trimmed), case-insensitive. These are dropped from the
    // prescribed/allowed course wherever it is required, so a runner missing one is not penalised and a scored
    // control no longer counts.
    private static HashSet<string> DisabledCodesOf(IReadOnlyList<ControlPoint> controlPoints) =>
        new(controlPoints.Where(c => c.IsDisabled).Select(c => c.Code.Trim()),
            StringComparer.OrdinalIgnoreCase);

    // The prescribed course codes minus the day's start/finish markers AND its disabled controls, in order —
    // the canonical "controls that must be visited / are allowed" list every scoring/status/splits path uses.
    private static List<string> CourseControls(
        string? courseOrder, IReadOnlySet<string> startFinishCodes, IReadOnlySet<string> disabledCodes) =>
        CourseOrderCodes(courseOrder)
            .Where(c => !startFinishCodes.Contains(c) && !disabledCodes.Contains(c))
            .ToList();

    // The disabled («проблемні») controls that actually appear in a given group's course order (start/finish
    // markers aside), so the splits view lists only the ones relevant to that course as «вимкнено».
    private static HashSet<string> DisabledInCourse(
        string? courseOrder, IReadOnlySet<string> startFinishCodes, IReadOnlySet<string> disabledCodes) =>
        new(CourseOrderCodes(courseOrder).Where(c => !startFinishCodes.Contains(c) && disabledCodes.Contains(c)),
            StringComparer.OrdinalIgnoreCase);

    public async Task<FinishReadoutImportResult> ImportFinishReadoutsAsync(ChipReadData data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (_session.CurrentEvent is null || _session.CurrentDay is null)
            return default;

        var folder = FolderPath;
        var dayId = CurrentDayId;
        var existing = await _eventStore.GetFinishReadoutsAsync(folder, dayId, cancellationToken);

        // Seed the dedup set with what's already logged (content keys), and continue the sequence.
        var seen = new HashSet<string>(existing.Select(r => r.ContentKey), StringComparer.Ordinal);
        var nextOrder = existing.Count == 0 ? 1 : existing.Max(r => r.Order) + 1;

        var toAdd = new List<FinishReadout>();
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
        }

        await _eventStore.AddFinishReadoutsAsync(folder, toAdd, cancellationToken);
        return new FinishReadoutImportResult(
            Added: toAdd.Count,
            Skipped: skipped,
            AddedIds: toAdd.Select(r => r.Id).ToList());
    }

    public Task<int> ClearFinishReadoutsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || _session.CurrentDay is null)
            return Task.FromResult(0);
        return _eventStore.DeleteFinishReadoutsForDayAsync(FolderPath, CurrentDayId, cancellationToken);
    }

    public async Task<FinishReadoutEditData?> GetFinishReadoutEditAsync(Guid readoutId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || _session.CurrentDay is null)
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

        var options = new List<FinishReadoutParticipantOption>(links.Count);
        foreach (var link in links)
        {
            if (!byId.TryGetValue(link.ParticipantId, out var p))
                continue;
            var group = link.GroupId is { } gid && groupName.TryGetValue(gid, out var gn) ? gn : string.Empty;
            options.Add(new FinishReadoutParticipantOption(p.Id, p.Number, p.FullName, group));
        }
        options = options
            .OrderBy(o => o.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var holder = links.FirstOrDefault(l =>
            string.Equals(l.Chip.Trim(), readout.ChipNumber.Trim(), StringComparison.OrdinalIgnoreCase));

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
            CurrentHolderId = holder?.ParticipantId
        };
    }

    public async Task UpdateFinishReadoutAsync(FinishReadoutEdit edit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (_session.CurrentEvent is null || _session.CurrentDay is null)
            return;

        var folder = FolderPath;
        var dayId = CurrentDayId;

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

        // (Re)assign this read-out's chip to the chosen participant on the day, taking it from any previous
        // holder so the chip stays unique per day. The caller has already confirmed the reassignment.
        if (edit.ReassignToParticipantId is { } targetId)
            await ReassignParticipantDayChipAsync(targetId, dayId, readout.ChipNumber, cancellationToken);
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

    // Serializes punches as comma-separated "code@ticks" (UTC ticks, "-" for an unknown time), the
    // form stored in FinishReadout.PunchTimes and reused inside the content key.
    private static string EncodePunchTimes(IEnumerable<ChipPunch> punches) =>
        string.Join(",", punches.Select(p =>
            $"{p.ControlCode.Trim()}@{p.Time?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "-"}"));

    // Reverses EncodePunchTimes back into a punch list (codes + times) for the splits view. Tokens that
    // can't be parsed are skipped; a "-" time becomes null.
    private static IReadOnlyList<ChipPunch> DecodePunchTimes(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
            return [];

        var list = new List<ChipPunch>();
        foreach (var token in encoded.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = token.LastIndexOf('@');
            if (at <= 0)
                continue;
            var code = token[..at].Trim();
            var timePart = token[(at + 1)..];
            DateTimeOffset? time = timePart != "-" && long.TryParse(timePart, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var ticks)
                ? new DateTimeOffset(ticks, TimeSpan.Zero)
                : null;
            list.Add(new ChipPunch(code, time));
        }
        return list;
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
            string.Equals(l.Chip.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
        if (holder is null)
            return null;

        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var name = participants.FirstOrDefault(p => p.Id == holder.ParticipantId)?.FullName ?? string.Empty;
        return name;
    }

    public async Task<Guid?> ReassignParticipantDayChipAsync(Guid participantId, Guid dayId, string chip, CancellationToken cancellationToken = default)
    {
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
                string.Equals(l.Chip.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
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
            string.Equals(c.Number.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));

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
            p.Id != row.ParticipantId && string.Equals(p.Number.Trim(), number, StringComparison.OrdinalIgnoreCase));
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
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (_session.CurrentEvent is null)
            return default;

        // Ensure every day referenced by a ProgEvent exists; create the missing ones (numbered up to
        // the highest reference) here, because AddDayAsync also makes each day's files folder (I/O).
        // The actual roster write then happens in one transaction inside the store (fast for big files).
        var maxDay = data.Participants
            .SelectMany(p => p.DayNumbers)
            .DefaultIfEmpty(0)
            .Max();
        var days = await _eventStore.GetDaysAsync(FolderPath, cancellationToken);
        var daysCreated = 0;
        var highestExisting = days.Count == 0 ? 0 : days.Max(d => d.Number);
        for (var n = highestExisting + 1; n <= maxDay; n++)
        {
            await AddDayAsync(cancellationToken);
            daysCreated++;
        }

        return await _eventStore.ImportParticipantsBatchAsync(
            FolderPath, data, clearFirst, daysCreated, progress, cancellationToken);
    }

    // ── Online live-results publishing ───────────────────────────────────────────────────────────────

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

        var groups = data.Groups
            .Select(g => new OnlineGroup(g.Name, g.DistanceKm, g.ControlCount, g.Order))
            .ToList();

        // The frontend keys results by (event, bib, day) with bib an integer, so a participant with no
        // (positive integer) start number can't be addressed and is left out of the snapshot. Count them so
        // the publish log can warn that some runners won't appear online until they're given a number.
        var rows = new List<OnlineResultRow>();
        var skippedNoNumber = 0;
        foreach (var g in data.Groups)
        {
            foreach (var r in g.Rows)
            {
                var trimmed = (r.Number ?? string.Empty).Trim();
                if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bib) || bib <= 0)
                {
                    skippedNoNumber++;
                    continue;
                }

                var res = r.Result;
                rows.Add(new OnlineResultRow(
                    Bib: bib,
                    GroupName: g.Name,
                    FullName: r.FullName,
                    Team: r.Team,
                    Club: r.ClubName,
                    Region: r.RegionName,
                    Birth: r.BirthDate is { } bd ? bd.ToString("dd.MM.yyyy") : string.Empty,
                    Qual: r.Rank,
                    Place: res.Place,
                    ResultTime: res.ResultTime,
                    StartTime: res.ActualStart?.TimeOfDay,
                    FinishTime: res.FinishTime?.TimeOfDay,
                    Score: res.Score,
                    Points: res.Points,
                    Status: res.Status,
                    HasReadout: res.HasReadout,
                    OutOfCompetition: res.OutOfCompetition));
            }
        }

        return new OnlineResultsSnapshot(onlineDays, day.Number, groups, rows, skippedNoNumber);
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
        _session.CurrentEvent is null || _session.CurrentDay is not { } day
            ? null
            : DayFolders.PathFor(FolderPath, day.Number);

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
        var dayFolder = DayFolders.PathFor(FolderPath, builtDay.Number);

        // Reuse the same computed data the protocols and online publish use — one section per group, each
        // member already carrying their result. Group rows are kept in the protocol order (placed finishers
        // first, then the rest), which is exactly the monitor order.
        var data = await GetResultProtocolDataAsync(dayId, cancellationToken);
        var info = await _eventStore.GetCompetitionInfoAsync(FolderPath, cancellationToken);
        var subtitle = !string.IsNullOrWhiteSpace(info?.Name) ? info!.Name : _session.CurrentEvent.Name;

        // Pre-compute, per group, the leader's clean result so the «Відставання» (gap) column can be filled.
        var groupsByName = data.Groups.ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);

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
        var groupsByName = data.Groups.ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);

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
        var groupsByName = source.Data.Groups.ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);
        return BuildMonitorDocument(file, columns, source.Data, groupsByName, source.Subtitle, labels);
    }

    // Builds one monitor document from a file (groups + title + timing) and the SHARED column selection against
    // the day's computed result data. Shared by the batch generation (BuildMonitorDocumentsAsync) and the page's
    // single-file live preview (BuildMonitorPreviewAsync). Columns are the same for every file.
    private static MonitorDocument BuildMonitorDocument(
        MonitorFile file, ResultColumnSelection columnSelection, ResultProtocolData data,
        IReadOnlyDictionary<string, ResultProtocolGroup> groupsByName, string subtitle, MonitorLabels labels)
    {
        var columns = columnSelection.Resolve()
            .Select(d => new MonitorColumn(d.Column, labels.ColumnHeaders.GetValueOrDefault(d.Column, d.Key)))
            .ToList();

        // The chosen groups in day order; an empty selection means "all groups of the day".
        var chosen = file.GroupNames.Count == 0
            ? data.Groups
            : file.GroupNames
                .Select(n => groupsByName.GetValueOrDefault(n))
                .Where(g => g is not null)
                .Select(g => g!)
                .ToList();

        var monitorGroups = new List<MonitorGroup>(chosen.Count);
        foreach (var g in chosen.OrderBy(g => g.Name, StringComparer.CurrentCulture))
        {
            var leader = g.Rows
                .Where(r => r.Result.Place is not null && r.Result.ResultTime is not null)
                .OrderBy(r => r.Result.ResultTime!.Value)
                .Select(r => r.Result.ResultTime)
                .FirstOrDefault();

            // Sort by result: placed finishers first by ascending place, then everyone still unplaced
            // (DNS/MP/running/поза конкурсом) after them — the same order the printed protocol uses.
            var rows = g.Rows
                .OrderBy(r => r.Result.Place ?? int.MaxValue)
                .ThenBy(r => r.Result.ResultTime ?? TimeSpan.MaxValue)
                .ThenBy(r => r.FullName, StringComparer.CurrentCultureIgnoreCase)
                .Select(r => BuildMonitorRow(r, columns, leader, labels))
                .ToList();

            monitorGroups.Add(new MonitorGroup(g.Name, BuildGroupCaption(g, labels), rows));
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
                ResultColumn.StartTime => FormatClock(res.ActualStart?.TimeOfDay),
                ResultColumn.ResultTime => res.Status == FinishStatus.Ok ? FormatSpan(res.ResultTime) : string.Empty,
                ResultColumn.Gap => placed && leader is { } l && res.ResultTime is { } rt && rt > l
                    ? "+" + FormatSpan(rt - l)
                    : string.Empty,
                ResultColumn.Status => StatusText(res, labels),
                ResultColumn.Points => res.Points?.ToString("0.##", CultureInfo.InvariantCulture)
                    ?? res.Score?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                _ => string.Empty,
            });

        // Grey out a row that has no place yet (DNS / MP / running / out-of-competition), like the legacy monitor.
        return new MonitorRow(values, Unplaced: res.Place is null || res.OutOfCompetition);
    }

    // The short status code shown for an unplaced run; blank for a clean finish (its time is in the result column).
    private static string StatusText(ParticipantDayResult res, MonitorLabels labels) => res.Status switch
    {
        FinishStatus.Dns => labels.StatusDns,
        FinishStatus.Mp => labels.StatusMp,
        FinishStatus.Ovt => labels.StatusOvt,
        FinishStatus.Dnf => labels.StatusDnf,
        FinishStatus.Dsq => labels.StatusDsq,
        FinishStatus.Ok => string.Empty,
        _ => res.HasReadout ? string.Empty : labels.StatusRunning,
    };

    private static string FormatSpan(TimeSpan? t) =>
        t is { } v ? (v.TotalHours >= 1
            ? v.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : v.ToString(@"m\:ss", CultureInfo.InvariantCulture))
        : string.Empty;

    private static string FormatClock(TimeSpan? t) =>
        t is { } v ? v.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : string.Empty;

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

    public Task SetParticipantDiscountAsync(Guid participantId, Guid discountId, bool on, CancellationToken cancellationToken = default) =>
        _eventStore.SetParticipantDiscountAsync(FolderPath, participantId, discountId, on, cancellationToken);

    // ── Fee computation ─────────────────────────────────────────────────────────────────────────
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

        public decimal Total(Participant participant, IEnumerable<(Guid? GroupId, string Chip)> memberDays) =>
            context.Total(participant.PaysRaisedFee, participant.IsFsouMember, SelectedDiscountIds(participant.Id), memberDays);
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
        ParticipantDayResult? result = null)
    {
        var name = link.GroupId is { } gid && groupName.TryGetValue(gid, out var n) ? n : string.Empty;
        var region = p.RegionId is { } rid && regionName.TryGetValue(rid, out var rn) ? rn : string.Empty;
        var club = p.ClubId is { } cid && clubName.TryGetValue(cid, out var cn) ? cn : string.Empty;
        var dussh = p.DusshId is { } did && dusshName.TryGetValue(did, out var dn) ? dn : string.Empty;
        return new ParticipantDayRow(
            LinkId: link.Id,
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

    private GroupDayRow ToRow(GroupDaySettings s, Group group, int participantCount = 0) => new(
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
        ParticipantCount: participantCount);
}
