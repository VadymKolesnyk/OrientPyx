using System.ComponentModel;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Services;

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
    /// Shows the manual start-order modal (drag members to re-order a group's start sequence) and awaits
    /// the user's input. Returns the start-time reassignments on save (empty when nothing changed), or null
    /// when cancelled/closed. Only one dialog is shown at a time.
    /// </summary>
    Task<IReadOnlyList<BusinessLogic.Models.DrawStartAssignment>?> ShowStartOrderAsync(StartOrderViewModel dialog);

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
    /// Shows the export-format modal (CSV vs Excel) and awaits the user's choice. Returns the chosen
    /// format on confirm, or null when cancelled/closed. Only one dialog is shown at a time.
    /// </summary>
    Task<BusinessLogic.Models.ExportFormat?> ShowExportFormatAsync(ExportFormatViewModel dialog);

    /// <summary>
    /// Shows the finish-read edit modal (reassign chip, edit times/punches, set status) and awaits the
    /// user's choice. Returns the confirmed edit on save, or null when cancelled/closed. Only one dialog
    /// is shown at a time.
    /// </summary>
    Task<FinishReadoutEdit?> ShowFinishReadoutEditAsync(FinishReadoutEditViewModel dialog);

    /// <summary>
    /// Shows the «проблемні КП» modal (tick the day's broken controls) and awaits the user's choice.
    /// Returns the ids of the controls to disable on save, or null when cancelled/closed. Only one dialog
    /// is shown at a time.
    /// </summary>
    Task<IReadOnlyList<Guid>?> ShowProblematicControlsAsync(ProblematicControlsViewModel dialog);

    /// <summary>
    /// Shows the read-only course-pattern help modal (how to write the «mixed» order pattern) and awaits
    /// its close. Only one dialog is shown at a time.
    /// </summary>
    Task ShowCoursePatternHelpAsync(CoursePatternHelpViewModel dialog);

    /// <summary>
    /// Shows the read-only per-screen help modal («що це / для чого / як користуватися») and awaits its
    /// close. Opened from the «?» button in each page header. Only one dialog is shown at a time.
    /// </summary>
    Task ShowScreenHelpAsync(ScreenHelpViewModel dialog);

    /// <summary>
    /// Shows the read-only draw-clash explanation modal (why a group chip is highlighted red — which groups
    /// it overlaps and what they share) and awaits its close. Only one dialog is shown at a time.
    /// </summary>
    Task ShowDrawClashHelpAsync(DrawClashHelpViewModel dialog);

    /// <summary>
    /// Shows the import-competition modal (confirm, or resolve an identifier clash by overwriting or
    /// entering a new unique name) and awaits the user's choice. Returns the decision on confirm, or null
    /// when cancelled/closed. Only one dialog is shown at a time.
    /// </summary>
    Task<ImportEventDecision?> ShowImportEventAsync(ImportEventViewModel dialog);
}
