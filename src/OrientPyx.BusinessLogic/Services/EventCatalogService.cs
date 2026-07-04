using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>Lists competitions by scanning the events folder and creates new ones.</summary>
public sealed class EventCatalogService : IEventCatalogService
{
    private readonly IAppSettingsService _settings;
    private readonly IEventFolderScanner _scanner;
    private readonly IEventStore _eventStore;

    public EventCatalogService(IAppSettingsService settings, IEventFolderScanner scanner, IEventStore eventStore)
    {
        _settings = settings;
        _scanner = scanner;
        _eventStore = eventStore;
    }

    public async Task<IReadOnlyList<EventSummary>> GetEventsAsync(CancellationToken cancellationToken = default)
    {
        var paths = await _settings.GetPathsAsync(cancellationToken);
        return await _scanner.ScanAsync(paths.EventsPath, cancellationToken);
    }

    public async Task<EventSummary?> FindByIdentifierAsync(string identifier, CancellationToken cancellationToken = default)
    {
        var events = await GetEventsAsync(cancellationToken);
        return events.FirstOrDefault(e => string.Equals(e.Identifier, identifier, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<EventSummary> CreateEventAsync(
        string name,
        string identifier,
        string venue,
        int dayCount,
        DateTimeOffset? startDate,
        DateTimeOffset? endDate,
        CompetitionOfficials? officials = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        identifier = (identifier ?? string.Empty).Trim();
        if (!IsValidIdentifier(identifier))
            throw new ArgumentException("Identifier must be a valid folder name.", nameof(identifier));

        // Always create at least one day; ignore nonsensical counts.
        dayCount = Math.Max(1, dayCount);

        var paths = await _settings.GetPathsAsync(cancellationToken);
        Directory.CreateDirectory(paths.EventsPath);

        var folderPath = Path.Combine(paths.EventsPath, identifier);
        if (Directory.Exists(folderPath))
            throw new InvalidOperationException($"A competition with identifier '{identifier}' already exists.");

        await _eventStore.EnsureCreatedAsync(folderPath, cancellationToken);

        // For a single day, end == start; otherwise fall back to start + dayCount-1 if no end given.
        endDate ??= dayCount == 1 ? startDate : startDate?.AddDays(dayCount - 1);

        var o = officials ?? CompetitionOfficials.None;
        var info = new CompetitionInfo
        {
            Name = name.Trim(),
            Identifier = identifier,
            Venue = (venue ?? string.Empty).Trim(),
            StartDate = startDate,
            EndDate = endDate,
            CourseSetter = (o.CourseSetter ?? string.Empty).Trim(),
            CourseSetterCategory = (o.CourseSetterCategory ?? string.Empty).Trim(),
            ChiefJudge = (o.ChiefJudge ?? string.Empty).Trim(),
            ChiefJudgeCategory = (o.ChiefJudgeCategory ?? string.Empty).Trim(),
            ChiefSecretary = (o.ChiefSecretary ?? string.Empty).Trim(),
            ChiefSecretaryCategory = (o.ChiefSecretaryCategory ?? string.Empty).Trim(),
            Jury = (o.Jury ?? string.Empty).Trim()
        };
        await _eventStore.SaveCompetitionInfoAsync(folderPath, info, cancellationToken);

        // Create the requested days, numbered from 1, each one calendar day after the previous.
        // Each day defaults to the competition venue (editable per day on the Days page later).
        // Alongside each day row we create its day{N} folder, where files imported for that day
        // (e.g. the IOF XML the courses came from) are stored.
        for (var i = 0; i < dayCount; i++)
        {
            var number = i + 1;
            await _eventStore.AddDayAsync(
                folderPath,
                new EventDay
                {
                    Number = number,
                    Date = startDate?.AddDays(i),
                    Venue = info.Venue
                },
                cancellationToken);

            Directory.CreateDirectory(DayFolders.PathFor(folderPath, number));
        }

        return new EventSummary
        {
            Identifier = info.Identifier,
            Name = info.Name,
            Venue = info.Venue,
            FolderPath = folderPath,
            CreatedAt = info.CreatedAt,
            DayCount = dayCount,
            StartDate = startDate,
            EndDate = endDate
        };
    }

    private static bool IsValidIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return false;

        if (identifier.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        // Disallow path navigation tokens.
        return identifier is not "." and not ".."
            && !identifier.Contains(Path.DirectorySeparatorChar)
            && !identifier.Contains(Path.AltDirectorySeparatorChar);
    }
}
