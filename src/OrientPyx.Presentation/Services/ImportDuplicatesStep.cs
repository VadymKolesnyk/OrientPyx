using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// The duplicate check shared by the UOF and CSV participant imports. For a current-day-only import it asks
/// the editor for rows that would add a namesake of someone who ran in the same group on another day and,
/// when there are any, lets the user decide per row (merge vs new) in <see cref="ImportDuplicatesViewModel"/>.
/// </summary>
internal static class ImportDuplicatesStep
{
    /// <summary>The scope to import with (carrying the merge choices), or null when the user cancelled.</summary>
    public static async Task<ParticipantImportScope?> ResolveAsync(
        UofParticipantData data,
        bool clearFirst,
        ParticipantImportScope scope,
        ICompetitionEditorService editor,
        IDialogService dialogs,
        IBusyService busy,
        ILocalizationService localization)
    {
        if (scope.Mode != ParticipantImportMode.CurrentDayOnly)
            return scope;

        var cases = await busy.RunAsync(() => editor.FindImportDuplicatesAsync(data, clearFirst, scope));
        if (cases.Count == 0)
            return scope;

        var merges = await dialogs.ShowImportDuplicatesAsync(
            new ImportDuplicatesViewModel(localization, scope.TargetDayNumber, scope.LinkField, cases));
        return merges is null ? null : scope.WithMergeInto(merges);
    }
}
