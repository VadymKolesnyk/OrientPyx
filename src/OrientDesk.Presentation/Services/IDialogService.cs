using System.ComponentModel;
using OrientDesk.BusinessLogic.Models;
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

    /// <summary>
    /// Shows a yes/no confirmation modal and awaits the user's choice. Returns true when confirmed,
    /// false when cancelled/closed. Only one dialog is shown at a time.
    /// </summary>
    Task<bool> ConfirmAsync(ConfirmDialogViewModel dialog);

    /// <summary>
    /// Shows the bulk-add-chips modal and awaits the user's input. Returns the entered values on OK,
    /// or null when cancelled/closed. Only one dialog is shown at a time.
    /// </summary>
    Task<BulkAddChipsResult?> ShowBulkAddChipsAsync(BulkAddChipsViewModel dialog);

    /// <summary>
    /// Shows the assign-start-numbers modal and awaits the user's input. Returns the start number and
    /// reassign flag on OK, or null when cancelled/closed. Only one dialog is shown at a time.
    /// </summary>
    Task<AssignNumbersResult?> ShowAssignNumbersAsync(AssignNumbersViewModel dialog);

    /// <summary>
    /// Shows the assign-chips modal and awaits the user's input. Returns the chosen note filter on OK,
    /// or null when cancelled/closed. Only one dialog is shown at a time.
    /// </summary>
    Task<AssignChipsResult?> ShowAssignChipsAsync(AssignChipsViewModel dialog);

    /// <summary>
    /// Shows the group-splitting preprocessing modal and awaits the user's choice. Returns the
    /// rewritten course data (one course per split group) on confirm, or null when cancelled/closed.
    /// Only one dialog is shown at a time.
    /// </summary>
    Task<IofCourseData?> ShowSplitGroupsAsync(SplitGroupsViewModel dialog);

    /// <summary>
    /// Shows the change-day-number modal and awaits the user's choice. Returns the chosen new number
    /// on confirm, or null when cancelled/closed. Only one dialog is shown at a time.
    /// </summary>
    Task<int?> ShowChangeDayNumberAsync(ChangeDayNumberViewModel dialog);

    /// <summary>
    /// Shows the add-region modal and awaits the user's input. Returns the trimmed name on confirm,
    /// or null when cancelled/closed. Only one dialog is shown at a time.
    /// </summary>
    Task<string?> ShowAddRegionAsync(AddRegionViewModel dialog);

    /// <summary>
    /// Shows the add-club modal and awaits the user's input. Returns the trimmed name on confirm,
    /// or null when cancelled/closed. Only one dialog is shown at a time.
    /// </summary>
    Task<string?> ShowAddClubAsync(AddClubViewModel dialog);

    /// <summary>
    /// Shows the add-ДЮСШ modal and awaits the user's input. Returns the trimmed name on confirm,
    /// or null when cancelled/closed. Only one dialog is shown at a time.
    /// </summary>
    Task<string?> ShowAddDusshAsync(AddDusshViewModel dialog);

    /// <summary>
    /// Shows the CSV column-mapping modal and awaits the user's choice. Returns the field→column
    /// mapping and clear-first flag on confirm, or null when cancelled/closed. Only one dialog is
    /// shown at a time.
    /// </summary>
    Task<CsvMappingResult?> ShowCsvMappingAsync(CsvMappingViewModel dialog);

    /// <summary>
    /// Shows the bulk-edit modal and awaits the user's choice. Returns the chosen field + value on
    /// confirm, or null when cancelled/closed. Only one dialog is shown at a time.
    /// </summary>
    Task<BulkEditResult?> ShowBulkEditAsync(BulkEditViewModel dialog);

    /// <summary>
    /// Shows the print-settings modal (printer + roll width) and awaits the user's choice. Returns true
    /// when the settings were saved, false when cancelled/closed. Only one dialog is shown at a time.
    /// </summary>
    Task<bool> ShowPrintSettingsAsync(PrintSettingsViewModel dialog);

    /// <summary>
    /// Shows the finish-read edit modal (reassign chip, edit times/punches, set status) and awaits the
    /// user's choice. Returns the confirmed edit on save, or null when cancelled/closed. Only one dialog
    /// is shown at a time.
    /// </summary>
    Task<FinishReadoutEdit?> ShowFinishReadoutEditAsync(FinishReadoutEditViewModel dialog);
}
