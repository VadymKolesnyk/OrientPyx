using Avalonia.Platform.Storage;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Writes the bytes of an export to the file the user picked in the save dialog, handling the common
/// "the document is already open in Word" case: a locked target is not silently skipped (which is what the
/// old bare <c>catch {}</c> did — the user saw nothing and assumed the file was overwritten) but raised as
/// a modal offering to save under a different name, pre-filled with the first free "name (2).ext".
/// </summary>
public interface IExportFileSaver
{
    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="file"/>. On a sharing violation (the file is open
    /// in another application) asks the user for another name — pre-filled with a free one, re-checked as
    /// they type — and writes that; on any other write failure shows an error modal. Returns the path
    /// actually written, or null when the user cancelled or nothing could be saved.
    /// </summary>
    Task<string?> SaveAsync(IStorageFile file, byte[] bytes);
}
