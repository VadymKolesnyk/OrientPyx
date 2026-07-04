using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Prints a split printout to an installed system printer. The implementation lives in DataAccess (it
/// touches the OS print spooler, which BusinessLogic must not). It is Windows-only at runtime: a thermal
/// printer prints to the roll (auto-cut handled by its driver), and a "Print to PDF" printer prompts to
/// save a PDF — the same code path, just a different installed printer.
/// </summary>
public interface ISplitPrintService
{
    /// <summary>True when printing is available on this OS (Windows). False elsewhere — the UI warns instead.</summary>
    bool IsSupported { get; }

    /// <summary>Installed printer names for the settings dropdown; empty when printing is unsupported.</summary>
    IReadOnlyList<string> GetInstalledPrinters();

    /// <summary>
    /// Renders <paramref name="document"/> (with localized <paramref name="labels"/>) to the printer named
    /// in <paramref name="settings"/> at its roll width. Throws <see cref="PrintNotSupportedException"/>
    /// off Windows; the caller surfaces a message.
    /// </summary>
    Task PrintAsync(
        SplitPrintDocument document,
        SplitPrintLabels labels,
        PrintSettings settings,
        CancellationToken cancellationToken = default);
}

/// <summary>Thrown when a print is attempted on a platform without printing support (non-Windows).</summary>
public sealed class PrintNotSupportedException : Exception
{
    public PrintNotSupportedException() : base("Printing is only supported on Windows.") { }
}
