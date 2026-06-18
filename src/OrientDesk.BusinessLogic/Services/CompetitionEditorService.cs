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
    private readonly Disciplines.IDisciplineStrategyProvider _strategies;
    private readonly IEntryFeeCalculator _entryFees;

    public CompetitionEditorService(
        ISessionService session,
        IEventStore eventStore,
        ICourseDistanceCalculator distance,
        Disciplines.IDisciplineStrategyProvider strategies,
        IEntryFeeCalculator entryFees)
    {
        _session = session;
        _eventStore = eventStore;
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

        var rows = new List<GroupDayRow>(settings.Count);
        foreach (var s in settings)
        {
            // Defensive: skip a settings row whose group was removed out from under it.
            if (byId.TryGetValue(s.GroupId, out var group))
                rows.Add(ToRow(s, group.Name));
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
                    cells.Add(new RosterDayCell(day.Id, day.Number, link.Id, IsMember: true, link.GroupId, name, link.Chip, link.StartTime, link.OutOfCompetition, result));
                    memberDays.Add((link.GroupId, link.Chip));
                }
                else
                {
                    cells.Add(new RosterDayCell(day.Id, day.Number, LinkId: null, IsMember: false, GroupId: null, GroupName: string.Empty, Chip: string.Empty, StartTime: null, OutOfCompetition: false, ParticipantDayResult.Empty));
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

    /// <summary>
    /// Computes each participant's result on one day from the finish read-outs, keyed by the
    /// participant-day link id. Reuses the finish-evaluation pipeline (<see cref="EvaluateFinish"/>) and
    /// the scored splits tally (for rogaine «Бали»), then ranks the OK results within each group. A chip
    /// read more than once on the day resolves to its <b>latest</b> read-out (highest <c>Order</c>).
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, ParticipantDayResult>> ComputeDayResultsAsync(
        string folder, EventDay day, CancellationToken cancellationToken)
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
        var dayDefault = day.DefaultDiscipline;

        // Per-control point values, positions and map scale — needed only to tally rogaine «Бали».
        var pointsByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cp in controlPoints)
            if (cp.Points is { } pts)
                pointsByCode[cp.Code.Trim()] = pts;

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
        // keys (result time, score) so the second pass can rank within each group.
        var results = new Dictionary<Guid, ParticipantDayResult>(links.Count);
        var ranking = new List<(Guid LinkId, Guid? GroupId, bool Scored, int Score, TimeSpan Time)>();
        foreach (var link in links)
        {
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
                continue;
            }

            var (computed, _, resolvedStart) = EvaluateFinish(readout, link, settingsByGroup, startFinishCodes, dayDefault);
            var status = link.ResultStatusOverride ?? computed;

            var resultTime = status == FinishStatus.Ok && resolvedStart is { } rs && readout.FinishTime is { } f
                ? f - rs
                : (TimeSpan?)null;

            GroupDaySettings? gs = link.GroupId is { } gid && settingsByGroup.TryGetValue(gid, out var s) ? s : null;
            var discipline = gs?.DisciplineOverride ?? dayDefault;
            int? score = _strategies.For(discipline).UsesControlPointPoints
                ? ScoreFor(readout, gs, startFinishCodes, pointsByCode, discipline)
                : null;

            results[link.Id] = new ParticipantDayResult(
                readout.StartTime, readout.FinishTime, status, link.ResultStatusOverride, computed, resultTime, Place: null, score, HasReadout: true);

            // Only OK results are placed; a non-positive result time can't be ranked by time.
            if (status == FinishStatus.Ok)
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

        return results;
    }

    // Total «Бали» (points) for a read-out on a point-scoring day: the scored splits tally over the
    // group's allowed controls. Mirrors the SplitsContext the finish-splits panel builds, minus the
    // geometry (distance/pace) it doesn't need.
    private int ScoreFor(
        FinishReadout readout,
        GroupDaySettings? gs,
        HashSet<string> startFinishCodes,
        IReadOnlyDictionary<string, int> pointsByCode,
        DisciplineType discipline)
    {
        var expected = SplitCodes(gs?.CourseOrder).Where(c => !startFinishCodes.Contains(c)).ToList();
        var punches = DecodePunchTimes(readout.PunchTimes)
            .Where(p => !startFinishCodes.Contains(p.ControlCode.Trim()))
            .ToList();
        var context = new SplitsContext
        {
            ExpectedControls = expected,
            Punches = punches,
            PointsByCode = pointsByCode,
            StartTime = readout.StartTime,
            FinishTime = readout.FinishTime
        };
        return _strategies.For(discipline).BuildSplits(context).TotalPoints;
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
                var (status, detail, resolvedStart) = EvaluateFinish(r, link, settingsByGroup, startFinishCodes, dayDefault);
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
                    ? ScoreFor(r, gs, startFinishCodes, pointsByCode, discipline)
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

        // Expected = course controls minus start/finish; punches = read punches (with times) minus the
        // same. An unknown chip (no link) has no prescribed course, so the expected list is empty and
        // every punch reads off-course. Start = chip start when present, else the assigned start (only
        // when a holder is known) paired with the finish's date.
        var expected = SplitCodes(gs?.CourseOrder).Where(c => !startFinishCodes.Contains(c)).ToList();
        var punches = DecodePunchTimes(readout.PunchTimes)
            .Where(p => !startFinishCodes.Contains(p.ControlCode.Trim()))
            .ToList();
        var start = readout.StartTime ?? CombineWithFinishDate(link?.StartTime, readout.FinishTime);

        var context = new SplitsContext
        {
            ExpectedControls = expected,
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
            FinishTime = readout.FinishTime
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
            ? BuildPassageRows(view.Passage)
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
            TotalPointsText = view.Layout == SplitsLayout.Scored
                ? view.TotalPoints.ToString(CultureInfo.InvariantCulture)
                : string.Empty,
            StartClock = startClock,
            FinishClock = finishClock,
            TotalDistanceText = FormatDistanceMetres(totalKm),
            AvgPaceText = FormatPace(AvgPaceSecondsPerKm(resultElapsed, totalKm)),
            Rows = rows,
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

    // Maps the ordered passage (set-course layout) to printable rows. The start marker is dropped (the
    // table opens at control 1); the finish marker becomes an "F" row with a blank №. Each row carries the
    // leg distance in km, the cumulative (elapsed) and leg times and the leg pace — as the panel shows them.
    private static IReadOnlyList<SplitPrintRow> BuildPassageRows(IReadOnlyList<PassagePunch> passage)
    {
        var list = new List<SplitPrintRow>(passage.Count);
        foreach (var p in passage)
        {
            if (p.Kind == PassageKind.Start)
                continue;

            var code = p.Kind == PassageKind.Finish ? "F" : p.Code;
            list.Add(new SplitPrintRow(
                Index: p.Kind == PassageKind.Control ? p.Index.ToString(CultureInfo.InvariantCulture) : string.Empty,
                Code: code,
                DistanceText: FormatDistanceMetres(p.LegKm),
                LegText: FormatDuration(p.Leg),
                ElapsedText: FormatDuration(p.Elapsed),
                PaceText: FormatPace(p.PaceSecondsPerKm),
                PointsText: null,
                OnCourse: p.Kind != PassageKind.Control || p.OnCourse));
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
        DisciplineType dayDefault)
    {
        GroupDaySettings? gs = link.GroupId is { } gid && settingsByGroup.TryGetValue(gid, out var s) ? s : null;
        var discipline = gs?.DisciplineOverride ?? dayDefault;

        // Expected = course-order codes minus the day's start/finish controls; punched = read codes
        // minus the same (the chip records start/finish boxes too, which are not course controls).
        var expected = SplitCodes(gs?.CourseOrder).Where(c => !startFinishCodes.Contains(c)).ToList();
        var punched = SplitCodes(readout.Punches).Where(c => !startFinishCodes.Contains(c)).ToList();

        // Start = chip read-out start when present, else the assigned start (a time-of-day) paired with
        // the finish's date so finish − start is a meaningful duration.
        var start = readout.StartTime ?? CombineWithFinishDate(link.StartTime, readout.FinishTime);

        var context = new FinishContext
        {
            ExpectedControls = expected,
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
        return new FinishReadoutImportResult(Added: toAdd.Count, Skipped: skipped);
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
                rows.Add(ToRow(s, group.Name));
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
            DayDefaultDiscipline: CurrentDayDefaultDiscipline,
            Result: result ?? ParticipantDayResult.Empty);
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
