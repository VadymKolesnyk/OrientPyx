using System.ComponentModel;
using OrientDesk.Presentation.ViewModels.Dialogs;

namespace OrientDesk.Presentation.Services;

/// <summary>
/// Hosts a single application-modal dialog rendered as an overlay in the main window. Any view
/// model can request a dialog and <c>await</c> its result without knowing where it is shown.
/// Implemented as a singleton bound by the window's overlay layer.
/// </summary>
public interface IDialogService : INotifyPropertyChanged
{
    /// <summary>The dialog currently shown, or null when none is open. Bound by the overlay.</summary>
    object? Current { get; }

    /// <summary>True while a dialog is open. Bound to the overlay's visibility.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Shows an import-options modal and awaits the user's choice. Returns the toggle values on
    /// OK, or null when cancelled/closed. Only one dialog is shown at a time.
    /// </summary>
    Task<ImportOptionsResult?> ShowImportOptionsAsync(ImportOptionsViewModel dialog);
}
