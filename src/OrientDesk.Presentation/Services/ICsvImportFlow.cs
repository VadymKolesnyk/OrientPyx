namespace OrientDesk.Presentation.Services;

/// <summary>
/// The "Import participants from CSV / Excel" flow on the participants page. Reads the file's header,
/// asks the user to map each column onto our participant fields (with an auto-guess), then imports the
/// mapped rows for the current competition — assigning every imported athlete to all existing days,
/// since a tabular file carries no per-day information. The page reloads itself when this returns true.
/// Mirrors <see cref="IParticipantImportFlow"/>, adding the column-mapping step. Both entry points share
/// the same mapping + import path; only the parse step differs (text vs workbook bytes).
/// </summary>
public interface ICsvImportFlow
{
    /// <summary>
    /// Runs the import for the supplied (already-decoded) CSV text. Returns true when the user confirmed
    /// and the import ran (so the caller should reload), or false when the file was unreadable/invalid,
    /// no competition/day exists, or the user cancelled.
    /// </summary>
    Task<bool> RunAsync(string csv);

    /// <summary>
    /// Runs the import for the raw bytes of an .xlsx workbook (its first worksheet). Same contract as
    /// <see cref="RunAsync(string)"/>.
    /// </summary>
    Task<bool> RunXlsxAsync(byte[] bytes);
}
