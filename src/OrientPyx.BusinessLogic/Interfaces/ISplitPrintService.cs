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

    /// <summary>
    /// Renders a <see cref="WinnersPrintDocument"/> (the призери printout) to the printer named in
    /// <paramref name="settings"/> at its roll width — a header, then each group with its prize places (a shared
    /// place printed as "2 третіх" over both names). Same OS constraints as <see cref="PrintAsync"/>: throws
    /// <see cref="PrintNotSupportedException"/> off Windows.
    /// </summary>
    Task PrintWinnersAsync(
        WinnersPrintDocument document,
        WinnersPrintPrintLabels labels,
        PrintSettings settings,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Localized captions the winners printout renderer draws (the document itself is values-only). The place
/// heading (e.g. "1 місце") and the shared-place heading (e.g. "2 третіх місця") are supplied as pre-formatted
/// strings by the caller; the renderer only lays them out.
/// </summary>
public sealed record WinnersPrintPrintLabels(
    /// <summary>The document heading printed above all groups (e.g. "ПРИЗЕРИ").</summary>
    string HeaderTitle);

/// <summary>Thrown when a print is attempted on a platform without printing support (non-Windows).</summary>
public sealed class PrintNotSupportedException : Exception
{
    public PrintNotSupportedException() : base("Printing is only supported on Windows.") { }
}
