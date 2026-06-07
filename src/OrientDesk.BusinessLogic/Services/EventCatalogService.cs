using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Services;

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

    public async Task<EventSummary> CreateEventAsync(string name, string identifier, string venue, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        identifier = (identifier ?? string.Empty).Trim();
        if (!IsValidIdentifier(identifier))
            throw new ArgumentException("Identifier must be a valid folder name.", nameof(identifier));

        var paths = await _settings.GetPathsAsync(cancellationToken);
        Directory.CreateDirectory(paths.EventsPath);

        var folderPath = Path.Combine(paths.EventsPath, identifier);
        if (Directory.Exists(folderPath))
            throw new InvalidOperationException($"A competition with identifier '{identifier}' already exists.");

        await _eventStore.EnsureCreatedAsync(folderPath, cancellationToken);

        var info = new CompetitionInfo
        {
            Name = name.Trim(),
            Identifier = identifier,
            Venue = (venue ?? string.Empty).Trim()
        };
        await _eventStore.SaveCompetitionInfoAsync(folderPath, info, cancellationToken);

        // Every competition starts with one day.
        await _eventStore.AddDayAsync(folderPath, new EventDay { Number = 1 }, cancellationToken);

        return new EventSummary
        {
            Identifier = info.Identifier,
            Name = info.Name,
            Venue = info.Venue,
            FolderPath = folderPath,
            CreatedAt = info.CreatedAt,
            DayCount = 1
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
