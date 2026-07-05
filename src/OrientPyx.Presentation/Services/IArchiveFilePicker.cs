namespace OrientPyx.Presentation.Services;

/// <summary>
/// Shows the OS open/save file dialogs for a competition archive. Implemented by the main window (which
/// owns the <c>StorageProvider</c>) and consumed by <see cref="IEventArchiveFlow"/> so the flow stays
/// free of view concerns.
/// </summary>
public interface IArchiveFilePicker
{
    /// <summary>
    /// Shows a save dialog for a competition archive, defaulting to <paramref name="suggestedFileName"/>.
    /// Returns the chosen absolute path, or null when cancelled.
    /// </summary>
    Task<string?> PickSaveArchiveAsync(string suggestedFileName);

    /// <summary>
    /// Shows an open dialog for a competition archive. Returns the chosen absolute path, or null when
    /// cancelled.
    /// </summary>
    Task<string?> PickOpenArchiveAsync();
}
