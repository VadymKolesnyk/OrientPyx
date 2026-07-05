using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Default <see cref="IEventArchiveFlow"/>. Drives the file dialogs (via the window-supplied
/// <see cref="IArchiveFilePicker"/>), the import-conflict modal, and the zip/unzip under the busy
/// overlay. All file/zip work lives in <see cref="IEventArchiveService"/> (DataAccess).
/// </summary>
public sealed class EventArchiveFlow : IEventArchiveFlow
{
    private readonly ILocalizationService _localization;
    private readonly IEventArchiveService _archive;
    private readonly IDialogService _dialogs;
    private readonly IBusyService _busy;
    private readonly IActivityLog _log;

    public EventArchiveFlow(
        ILocalizationService localization,
        IEventArchiveService archive,
        IDialogService dialogs,
        IBusyService busy,
        IActivityLog log)
    {
        _localization = localization;
        _archive = archive;
        _dialogs = dialogs;
        _busy = busy;
        _log = log;
    }

    public IArchiveFilePicker? Picker { get; set; }

    public async Task<bool> ExportAsync(EventSummary competition)
    {
        if (Picker is null || competition is null || string.IsNullOrWhiteSpace(competition.Identifier))
            return false;

        var path = await Picker.PickSaveArchiveAsync(SuggestedFileName(competition));
        if (path is null)
            return false; // cancelled

        try
        {
            await _busy.RunAsync(() => _archive.ExportAsync(competition.Identifier, path));
            _log.Info($"Експортовано змагання «{competition.Name}» до {path}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Не вдалося експортувати змагання", ex);
            await ShowErrorAsync("ExportEvent.Error");
            return false;
        }
    }

    public async Task<EventSummary?> ImportAsync()
    {
        if (Picker is null)
            return null;

        var path = await Picker.PickOpenArchiveAsync();
        if (path is null)
            return null; // cancelled

        // Inspect the archive (off the UI thread) to learn its identifier and whether it clashes.
        EventArchivePreview preview;
        try
        {
            preview = await _busy.RunAsync(() => _archive.PreviewImportAsync(path));
        }
        catch (EventArchiveFormatException)
        {
            await ShowErrorAsync("ImportEvent.BadArchive");
            return null;
        }
        catch (Exception ex)
        {
            _log.Error("Не вдалося прочитати архів змагань", ex);
            await ShowErrorAsync("ImportEvent.Error");
            return null;
        }

        // Ask the user to confirm, or resolve a clash (overwrite vs new unique identifier, validated live).
        var decision = await _dialogs.ShowImportEventAsync(new ImportEventViewModel(
            _localization,
            preview.Identifier,
            preview.IdentifierExists,
            candidate => _archive.IsIdentifierAvailableAsync(candidate)));
        if (decision is null)
            return null; // cancelled

        try
        {
            var summary = await _busy.RunAsync(() =>
                _archive.ImportAsync(path, decision.Identifier, decision.Overwrite));
            _log.Info($"Імпортовано змагання «{summary.Name}» як {summary.Identifier}");
            return summary;
        }
        catch (EventArchiveFormatException)
        {
            await ShowErrorAsync("ImportEvent.BadArchive");
            return null;
        }
        catch (Exception ex)
        {
            _log.Error("Не вдалося імпортувати змагання", ex);
            await ShowErrorAsync("ImportEvent.Error");
            return null;
        }
    }

    // "<competition> <date>.opyx" (sanitised), the default name in the save dialog.
    private string SuggestedFileName(EventSummary competition)
    {
        var name = string.IsNullOrWhiteSpace(competition.Name)
            ? competition.Identifier
            : competition.Name;
        var stamp = DateTime.Now.ToString("yyyy-MM-dd");
        var baseName = $"{name} {stamp}";
        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(invalid, '_');
        return $"{baseName}.{EventArchiveConstants.Extension}";
    }

    // Reuses the import-options modal as a plain "title + message + OK" error box (no toggles).
    private Task ShowErrorAsync(string messageKey) =>
        _dialogs.ShowImportOptionsAsync(new ImportOptionsViewModel(
            _localization,
            titleKey: "ImportEvent.Title",
            messageKey: messageKey,
            options: []));
}
