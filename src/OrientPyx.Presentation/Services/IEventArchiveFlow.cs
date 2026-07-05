using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Orchestrates the File → Export / Import Competition commands: the file dialogs, the import-conflict
/// modal, and running the zip/unzip under the busy overlay.
/// </summary>
public interface IEventArchiveFlow
{
    /// <summary>The picker the flow uses for OS file dialogs. Set once by the main window on load.</summary>
    IArchiveFilePicker? Picker { get; set; }

    /// <summary>
    /// Exports the given competition to an archive the user picks a location for. No-op when the user
    /// cancels the save dialog. Returns true when a file was written.
    /// </summary>
    Task<bool> ExportAsync(EventSummary competition);

    /// <summary>
    /// Imports a competition archive the user picks. Handles the identifier-clash decision. Returns the
    /// imported competition on success, or null when cancelled / on a bad archive.
    /// </summary>
    Task<EventSummary?> ImportAsync();
}
