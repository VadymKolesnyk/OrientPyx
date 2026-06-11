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
    private readonly ICourseDistanceCalculator _distance;

    public CompetitionEditorService(
        ISessionService session,
        IEventStore eventStore,
        ICourseDistanceCalculator distance)
    {
        _session = session;
        _eventStore = eventStore;
        _distance = distance;
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

    public Task SaveInfoAsync(CompetitionInfo info, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        return _eventStore.SaveCompetitionInfoAsync(FolderPath, info, cancellationToken);
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
        return day;
    }

    public Task UpdateDayAsync(EventDay day, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(day);
        return _eventStore.UpdateDayAsync(FolderPath, day, cancellationToken);
    }

    public Task DeleteDayAsync(Guid dayId, CancellationToken cancellationToken = default)
        => _eventStore.DeleteDayAsync(FolderPath, dayId, cancellationToken);

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
                points.Add(ToEntity(parsed[i], dayId, order: i + 1));

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
            toAdd.Add(ToEntity(control, dayId, order: nextOrder++));
        }

        await _eventStore.AddControlPointsAsync(folder, toAdd, cancellationToken);
        return new ControlPointImportResult(
            Imported: existing.Count + toAdd.Count,
            Added: toAdd.Count,
            Replaced: false);
    }

    private static ControlPoint ToEntity(IofControl control, Guid dayId, int order) => new()
    {
        EventDayId = dayId,
        Order = order,
        Code = control.Code.Trim(),
        Latitude = control.Latitude,
        Longitude = control.Longitude,
        Type = control.Type
    };

    public async Task<IReadOnlyList<GroupDayRow>> GetGroupDayRowsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || _session.CurrentDay is null)
            return [];

        var settings = await _eventStore.GetGroupDaySettingsAsync(FolderPath, CurrentDayId, cancellationToken);
        var groups = await _eventStore.GetGroupsAsync(FolderPath, cancellationToken);
        var byId = groups.ToDictionary(g => g.Id);

        var rows = new List<GroupDayRow>(settings.Count);
        foreach (var s in settings)
        {
            // Defensive: skip a settings row whose group was removed out from under it.
            if (byId.TryGetValue(s.GroupId, out var group))
                rows.Add(ToRow(s, group.Name));
        }
        return rows;
    }

    public async Task<GroupDayRow> AddGroupToDayAsync(string name, CancellationToken cancellationToken = default)
    {
        var dayId = CurrentDayId;
        var trimmed = (name ?? string.Empty).Trim();

        // Reuse an existing group with the same name (case-insensitive), or create a new one.
        var groups = await _eventStore.GetGroupsAsync(FolderPath, cancellationToken);
        var group = groups.FirstOrDefault(g => string.Equals(g.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (group is null)
        {
            group = new Group { Name = trimmed };
            await _eventStore.AddGroupAsync(FolderPath, group, cancellationToken);
        }

        // If the group is already on this day, return its existing row instead of duplicating.
        var settings = await _eventStore.GetGroupDaySettingsAsync(FolderPath, dayId, cancellationToken);
        var existing = settings.FirstOrDefault(s => s.GroupId == group.Id);
        if (existing is not null)
            return ToRow(existing, group.Name);

        var nextOrder = settings.Count == 0 ? 1 : settings.Max(s => s.Order) + 1;
        var row = new GroupDaySettings
        {
            EventDayId = dayId,
            GroupId = group.Id,
            Order = nextOrder
        };
        await _eventStore.AddGroupDaySettingsAsync(FolderPath, row, cancellationToken);
        return ToRow(row, group.Name);
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
            PenaltyPerMinute = row.PenaltyPerMinute
        }, cancellationToken);
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
        foreach (var control in data.Controls)
        {
            var code = control.Code.Trim();
            if (code.Length > 0)
                coordsByCode[code] = new GeoPoint(control.Latitude, control.Longitude);
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
            var distanceKm = ComputeDistance(course.ControlCodes, coordsByCode);

            // Resolve (or create) the competition-level group for this course name.
            if (!groupsByName.TryGetValue(name, out var group))
            {
                group = new Group { Name = name };
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

    // Maps a course's running control codes to their day coordinates and sums the straight-line
    // distance. Unknown codes resolve to a coordinate-less point, so their legs count as 0 km.
    private decimal? ComputeDistance(
        IReadOnlyList<string> controlCodes,
        IReadOnlyDictionary<string, GeoPoint> coordsByCode)
    {
        if (controlCodes.Count < 2)
            return null;

        var points = new List<GeoPoint>(controlCodes.Count);
        foreach (var code in controlCodes)
        {
            var key = code.Trim();
            points.Add(coordsByCode.TryGetValue(key, out var p) ? p : new GeoPoint(null, null));
        }

        return _distance.TotalKilometres(points);
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

    private GroupDayRow ToRow(GroupDaySettings s, string name) => new(
        SettingsId: s.Id,
        GroupId: s.GroupId,
        Order: s.Order,
        Name: name,
        CourseOrder: s.CourseOrder,
        DistanceKm: s.DistanceKm,
        DisciplineOverride: s.DisciplineOverride,
        DayDefaultDiscipline: CurrentDayDefaultDiscipline,
        TimeLimitSeconds: s.TimeLimitSeconds,
        RequiredControlCount: s.RequiredControlCount,
        PenaltyPerMinute: s.PenaltyPerMinute);
}
