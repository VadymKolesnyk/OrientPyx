using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.DataAccess.Network.Host;

/// <summary>
/// Translates between the competition <b>identifier</b> that travels over the network and the host's folder path
/// (П18): an incoming identifier must pass <see cref="EventIdentifier.IsValid"/> (no separators, no <c>..</c>) before
/// it becomes <c>&lt;events&gt;/&lt;identifier&gt;</c>, so a station can never make the host open a database at an
/// arbitrary path; outgoing <see cref="EventSummary.FolderPath"/> values are reduced back to the identifier, so a
/// station never sees a host path.
/// </summary>
internal sealed class EventPathMapper
{
    private readonly IAppSettingsService _settings;

    public EventPathMapper(IAppSettingsService settings) => _settings = settings;

    /// <summary>The host's events folder (the configured path).</summary>
    public async Task<string> EventsPathAsync(CancellationToken cancellationToken)
        => (await _settings.GetPathsAsync(cancellationToken)).EventsPath;

    /// <summary>The host path of an identifier received from a station; throws <see cref="ArgumentException"/> when
    /// the identifier is not a plain folder name.</summary>
    public static string ToPath(string eventsPath, string? identifier)
    {
        if (!EventIdentifier.IsValid(identifier))
            throw new ArgumentException($"Invalid competition identifier '{identifier}'.", nameof(identifier));
        return Path.Combine(eventsPath, identifier!);
    }

    /// <summary>The identifier of a host path (its folder name).</summary>
    public static string ToIdentifier(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    /// <summary>A copy of a summary with its folder path replaced by the identifier.</summary>
    public static EventSummary Outgoing(EventSummary summary) => new()
    {
        Identifier = summary.Identifier,
        Name = summary.Name,
        Venue = summary.Venue,
        FolderPath = ToIdentifier(summary.FolderPath),
        CreatedAt = summary.CreatedAt,
        DayCount = summary.DayCount,
        StartDate = summary.StartDate,
        EndDate = summary.EndDate,
        IsHidden = summary.IsHidden,
    };

    /// <summary>A copy of a summary received from a station with the identifier resolved to the host path.</summary>
    public static EventSummary Incoming(EventSummary summary, string eventsPath) => new()
    {
        Identifier = summary.Identifier,
        Name = summary.Name,
        Venue = summary.Venue,
        FolderPath = ToPath(eventsPath, summary.FolderPath),
        CreatedAt = summary.CreatedAt,
        DayCount = summary.DayCount,
        StartDate = summary.StartDate,
        EndDate = summary.EndDate,
        IsHidden = summary.IsHidden,
    };

    /// <summary>Replaces every host path inside a method result (a summary, a list of summaries, an import result)
    /// by the identifier; other values pass through unchanged.</summary>
    public static object? MapResult(object? value) => value switch
    {
        null => null,
        EventSummary s => Outgoing(s),
        IReadOnlyList<EventSummary> list => list.Select(Outgoing).ToList(),
        EventArchiveImportResult r => r with { Competition = Outgoing(r.Competition) },
        _ => value,
    };
}
