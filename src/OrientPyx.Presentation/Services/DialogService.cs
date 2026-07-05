using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Default <see cref="IDialogService"/>. Holds the active dialog view model and raises change
/// notifications so the window's overlay shows/hides it. Shows one dialog at a time.
/// </summary>
public sealed partial class DialogService : ObservableObject, IDialogService
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    private object? _current;

    public bool IsOpen => Current is not null;

    public async Task<ImportOptionsResult?> ShowImportOptionsAsync(ImportOptionsViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            // Clear only if this dialog is still the one on screen (it always is here, but this
            // keeps the overlay correct if dialogs are ever nested in future).
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }

    public async Task<bool> ConfirmAsync(ConfirmDialogViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }

    public async Task<BulkAddChipsResult?> ShowBulkAddChipsAsync(BulkAddChipsViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }

    public async Task<AssignNumbersResult?> ShowAssignNumbersAsync(AssignNumbersViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }

    public async Task<AssignChipsResult?> ShowAssignChipsAsync(AssignChipsViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }

    public async Task<IReadOnlyList<DrawStartAssignment>?> ShowStartOrderAsync(StartOrderViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }

    public async Task<IofCourseData?> ShowSplitGroupsAsync(SplitGroupsViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }

    public async Task<int?> ShowChangeDayNumberAsync(ChangeDayNumberViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }

    public async Task<string?> ShowAddRegionAsync(AddRegionViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }

    public async Task<string?> ShowAddClubAsync(AddClubViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }

    public async Task<string?> ShowAddDusshAsync(AddDusshViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }

    public async Task<CsvMappingResult?> ShowCsvMappingAsync(CsvMappingViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }

    public async Task<BulkEditResult?> ShowBulkEditAsync(BulkEditViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }

    public async Task<bool> ShowPrintSettingsAsync(PrintSettingsViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }

    public async Task<FinishReadoutEdit?> ShowFinishReadoutEditAsync(FinishReadoutEditViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }

    public async Task<ExportFormat?> ShowExportFormatAsync(ExportFormatViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }

    public async Task<IReadOnlyList<Guid>?> ShowProblematicControlsAsync(ProblematicControlsViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }

    public async Task ShowCoursePatternHelpAsync(CoursePatternHelpViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }

    public async Task ShowScreenHelpAsync(ScreenHelpViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }

    public async Task ShowDrawClashHelpAsync(DrawClashHelpViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }

    public async Task<ImportEventDecision?> ShowImportEventAsync(ImportEventViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Current = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
                Current = null;
        }
    }
}
