using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Exports a whole competition folder to a single archive file and imports such an archive back as a
/// new competition. The archive is a zip of the competition's <c>events/&lt;id&gt;</c> folder (its
/// <c>event.db</c>, day folders, imported files, views.json, …) so it carries the complete competition.
/// </summary>
public interface IEventArchiveService
{
    /// <summary>
    /// Zips the competition folder identified by <paramref name="identifier"/> into the archive at
    /// <paramref name="destinationArchivePath"/> (overwriting it). Throws if the competition doesn't
    /// exist.
    /// </summary>
    Task ExportAsync(string identifier, string destinationArchivePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the archive at <paramref name="archivePath"/> and returns the identifier (folder name) it
    /// would import as — taken from the archive's single top-level folder — plus whether a competition
    /// with that identifier already exists. Does not write anything. Throws
    /// <see cref="EventArchiveFormatException"/> if the archive isn't a valid competition archive.
    /// </summary>
    Task<EventArchivePreview> PreviewImportAsync(string archivePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts the archive at <paramref name="archivePath"/> into the events folder as a competition
    /// named <paramref name="identifier"/>. When a folder with that identifier already exists it is
    /// replaced only if <paramref name="overwrite"/> is true; otherwise
    /// <see cref="InvalidOperationException"/> is thrown. Throws <see cref="ArgumentException"/> if the
    /// identifier is not a valid folder name, and <see cref="EventArchiveFormatException"/> if the
    /// archive isn't a valid competition archive.
    /// </summary>
    Task<EventSummary> ImportAsync(
        string archivePath,
        string identifier,
        bool overwrite,
        CancellationToken cancellationToken = default);

    /// <summary>True when <paramref name="identifier"/> is a valid folder name with no existing competition.</summary>
    Task<bool> IsIdentifierAvailableAsync(string identifier, CancellationToken cancellationToken = default);

    /// <summary>True when <paramref name="identifier"/> is a valid competition folder name (ignores existence).</summary>
    bool IsValidIdentifier(string identifier);
}
