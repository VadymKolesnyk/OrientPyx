using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// The "Export current view" flow on the participants page. Given the table's on-screen snapshot
/// (visible columns + displayed rows, captured by the view since it owns the SheetTable), it asks the
/// user to pick a format (CSV / Excel) in a modal, then serialises the snapshot with the matching writer.
/// File picking stays in the view (it needs the window's StorageProvider), so this returns the chosen
/// format, the serialised bytes and a suggested file name; the view runs the save dialog and writes them.
/// Mirrors the import flows, reversed: the modal comes first, the file picker last.
/// </summary>
public interface IParticipantExportFlow
{
    /// <summary>
    /// Shows the format modal and, on confirm, serialises <paramref name="view"/> into the chosen
    /// format's bytes. Returns the result to save, or null when there is nothing to export or the user
    /// cancelled. The caller saves the bytes (the view owns the save picker).
    /// </summary>
    Task<ParticipantExportResult?> RunAsync(CsvParticipantData view);
}

/// <summary>
/// A ready-to-save export: the chosen <see cref="Format"/>, the serialised <see cref="Bytes"/>, a
/// suggested file name (no path) and the file extension/MIME for the save dialog's type filter.
/// </summary>
public sealed class ParticipantExportResult
{
    public ParticipantExportResult(ExportFormat format, byte[] bytes, string suggestedFileName, string extension, string mimeType)
    {
        Format = format;
        Bytes = bytes;
        SuggestedFileName = suggestedFileName;
        Extension = extension;
        MimeType = mimeType;
    }

    public ExportFormat Format { get; }
    public byte[] Bytes { get; }

    /// <summary>The default file name (with extension) the save dialog opens with.</summary>
    public string SuggestedFileName { get; }

    /// <summary>The file extension without the dot (e.g. "csv", "xlsx") for the save dialog's filter.</summary>
    public string Extension { get; }

    /// <summary>The MIME type for the save dialog's file-type filter.</summary>
    public string MimeType { get; }
}
