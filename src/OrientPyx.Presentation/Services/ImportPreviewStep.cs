using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// The confirmation shared by the UOF and CSV participant imports. For an all-days import that keeps the current
/// roster it asks the editor what the file would do (<see cref="ImportPreview"/>) and shows it in
/// <see cref="ImportPreviewViewModel"/>: the user unticks rows to leave out and ticks the athletes the file no
/// longer lists to delete them. A day-scoped import, a clear-first import, or a file that changes nothing skip
/// the modal.
/// </summary>
internal static class ImportPreviewStep
{
    /// <summary>The scope to import with (rows left out) and the athletes to delete after the import, or null
    /// when the user cancelled.</summary>
    public static async Task<(ParticipantImportScope Scope, IReadOnlyList<Guid> DeleteIds)?> ResolveAsync(
        UofParticipantData data,
        bool clearFirst,
        ParticipantImportScope scope,
        ICompetitionEditorService editor,
        IDialogService dialogs,
        IBusyService busy,
        ILocalizationService localization)
    {
        if (scope.Mode != ParticipantImportMode.AllDays || clearFirst)
            return (scope, []);

        var preview = await busy.RunAsync(() => editor.PreviewParticipantImportAsync(data, scope));
        if (preview.IsEmpty)
            return (scope, []);

        var decision = await dialogs.ShowImportPreviewAsync(new ImportPreviewViewModel(localization, preview));
        if (decision is null)
            return null;

        return (scope.WithExcludedRows(decision.ExcludedRows), decision.DeleteParticipantIds);
    }

    /// <summary>Deletes the athletes the user ticked in the «missing from the file» block (after the import).</summary>
    public static async Task DeleteAsync(IReadOnlyList<Guid> ids, ICompetitionEditorService editor, IBusyService busy)
    {
        if (ids.Count == 0)
            return;

        await busy.RunAsync(async () =>
        {
            foreach (var id in ids)
                await editor.DeleteParticipantAsync(id);
        });
    }
}
