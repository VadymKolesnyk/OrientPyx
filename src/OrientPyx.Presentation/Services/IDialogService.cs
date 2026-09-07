using System.ComponentModel;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Hosts a single application-modal dialog rendered as an overlay in the main window. Any view
/// model can request a dialog and <c>await</c> its result without knowing where it is shown.
/// Implemented as a singleton bound by the window's overlay layer.
/// Only one dialog is shown at a time; every Show* below awaits the user and returns null (or false)
/// when cancelled or closed. Only deviations from that are documented.
/// </summary>
public interface IDialogService : INotifyPropertyChanged
{
    /// <summary>The dialog currently shown, or null when none is open. Bound by the overlay.</summary>
    object? Current { get; }

    /// <summary>True while a dialog is open. Bound to the overlay's visibility.</summary>
    bool IsOpen { get; }

    /// <summary>Returns the toggle values on OK.</summary>
    Task<ImportOptionsResult?> ShowImportOptionsAsync(ImportOptionsViewModel dialog);

    /// <summary>True when confirmed.</summary>
    Task<bool> ConfirmAsync(ConfirmDialogViewModel dialog);

    /// <summary>Same modal, but reports which button was pressed — for dialogs with the optional third action.</summary>
    Task<ConfirmDialogResult> ChooseAsync(ConfirmDialogViewModel dialog);

    /// <summary>Returns the entered values on OK.</summary>
    Task<BulkAddChipsResult?> ShowBulkAddChipsAsync(BulkAddChipsViewModel dialog);

    /// <summary>Returns the start number and reassign flag on OK.</summary>
    Task<AssignNumbersResult?> ShowAssignNumbersAsync(AssignNumbersViewModel dialog);

    /// <summary>Returns the chosen note filter on OK.</summary>
    Task<AssignChipsResult?> ShowAssignChipsAsync(AssignChipsViewModel dialog);

    /// <summary>Drag members to re-order a group's start sequence. Returns the start-time reassignments
    /// on save; empty when nothing changed.</summary>
    Task<IReadOnlyList<BusinessLogic.Models.DrawStartAssignment>?> ShowStartOrderAsync(StartOrderViewModel dialog);

    /// <summary>«Швидке зняття»: type a number → set a status, surname auto-filled. Returns the
    /// «participant → status» assignments on save; empty when nothing was entered.</summary>
    Task<IReadOnlyList<QuickWithdrawalAssignment>?> ShowQuickWithdrawalAsync(QuickWithdrawalViewModel dialog);

    /// <summary>Returns the rewritten course data — one course per split group.</summary>
    Task<IofCourseData?> ShowSplitGroupsAsync(SplitGroupsViewModel dialog);

    /// <summary>Returns the chosen new number.</summary>
    Task<int?> ShowChangeDayNumberAsync(ChangeDayNumberViewModel dialog);

    /// <summary>An export could not overwrite its target (another program holds it open); offers a
    /// different name. Returns the chosen file name, no path.</summary>
    Task<string?> ShowFileLockedAsync(FileLockedViewModel dialog);

    Task<string?> ShowAddRegionAsync(AddRegionViewModel dialog);

    Task<string?> ShowAddClubAsync(AddClubViewModel dialog);

    /// <summary>Returns the trimmed name.</summary>
    Task<string?> ShowAddDusshAsync(AddDusshViewModel dialog);

    /// <summary>Returns the field→column mapping and clear-first flag.</summary>
    Task<CsvMappingResult?> ShowCsvMappingAsync(CsvMappingViewModel dialog);

    /// <summary>Returns the chosen field + value.</summary>
    Task<BulkEditResult?> ShowBulkEditAsync(BulkEditViewModel dialog);

    /// <summary>Printer + roll width. True when saved.</summary>
    Task<bool> ShowPrintSettingsAsync(PrintSettingsViewModel dialog);

    /// <summary>«Відомість»: live preview of the flat, chip-sorted list with configurable columns and
    /// header. Export/print happen inside via the VM's commands; this just returns on close.</summary>
    Task ShowStatementAsync(StatementViewModel dialog);

    /// <summary>Printer only; paper is always A4. True when saved. May open on top of the statement
    /// modal — the previous overlay is restored when it closes.</summary>
    Task<bool> ShowA4PrintSettingsAsync(A4PrintSettingsViewModel dialog);

    /// <summary>CSV vs Excel.</summary>
    Task<BusinessLogic.Models.ExportFormat?> ShowExportFormatAsync(ExportFormatViewModel dialog);

    /// <summary>Reassign chip, edit times/punches, set status.</summary>
    Task<FinishReadoutEdit?> ShowFinishReadoutEditAsync(FinishReadoutEditViewModel dialog);

    /// <summary>«Проблемні КП»: tick the day's broken controls. Returns the ids to disable.</summary>
    Task<IReadOnlyList<Guid>?> ShowProblematicControlsAsync(ProblematicControlsViewModel dialog);

    /// <summary>Read-only: how to write the «mixed» order pattern.</summary>
    Task ShowCoursePatternHelpAsync(CoursePatternHelpViewModel dialog);

    /// <summary>«Перевірити порядок»: type a passage, see whether the group's pattern accepts it.</summary>
    Task ShowCoursePatternCheckAsync(CoursePatternCheckViewModel dialog);

    /// <summary>«Всі варіанти»: every concrete passage order the group's pattern allows.</summary>
    Task ShowCoursePatternVariantsAsync(CoursePatternVariantsViewModel dialog);

    /// <summary>Read-only «що це / для чого / як користуватися», from the «?» button in a page header.</summary>
    Task ShowScreenHelpAsync(ScreenHelpViewModel dialog);

    /// <summary>Read-only: why a group chip is red — which groups it overlaps and what they share.</summary>
    Task ShowDrawClashHelpAsync(DrawClashHelpViewModel dialog);

    /// <summary>Confirm, or resolve an identifier clash by overwriting or entering a new unique name.</summary>
    Task<ImportEventDecision?> ShowImportEventAsync(ImportEventViewModel dialog);

    /// <summary>Returns the new competition identifier (folder name), validated against the events folder.</summary>
    Task<string?> ShowRenameEventAsync(RenameEventViewModel dialog);
}
