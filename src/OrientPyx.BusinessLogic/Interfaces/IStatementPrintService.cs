using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Prints a participant statement («відомість») to an installed system printer on A4 paper. The implementation
/// lives in DataAccess (it touches the OS print spooler, which BusinessLogic must not). Windows-only at
/// runtime: a real A4 printer prints the sheet; a "Print to PDF" printer prompts to save a PDF — the same code
/// path. It renders the same <see cref="ResultProtocolDocument"/> the .docx export produces, so the printed
/// sheet matches the preview and the Word file (header + applied-filters line + the flat, chip-sorted table
/// with own-chip cells drawn bold).
/// </summary>
public interface IStatementPrintService
{
    /// <summary>True when printing is available on this OS (Windows). False elsewhere — the UI warns instead.</summary>
    bool IsSupported { get; }

    /// <summary>Installed printer names for the settings dropdown; empty when printing is unsupported.</summary>
    IReadOnlyList<string> GetInstalledPrinters();

    /// <summary>
    /// Renders <paramref name="document"/> to the A4 printer named in <paramref name="settings"/> (portrait or
    /// landscape per <see cref="ResultProtocolDocument.Orientation"/>). Throws
    /// <see cref="PrintNotSupportedException"/> off Windows; the caller surfaces a message.
    /// </summary>
    Task PrintAsync(
        ResultProtocolDocument document,
        A4PrintSettings settings,
        CancellationToken cancellationToken = default);
}
