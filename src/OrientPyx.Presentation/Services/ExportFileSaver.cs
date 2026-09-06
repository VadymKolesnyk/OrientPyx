using Avalonia.Platform.Storage;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Default <see cref="IExportFileSaver"/>. See the interface for the behaviour; the search limit keeps the
/// free-name scan bounded if a folder somehow holds every candidate.
/// </summary>
public sealed class ExportFileSaver : IExportFileSaver
{
    /// <summary>How many "name (N)" candidates to scan when proposing a free name.</summary>
    private const int MaxCandidates = 200;

    private readonly IDialogService _dialogs;
    private readonly ILocalizationService _localization;
    private readonly IActivityLog _log;

    public ExportFileSaver(IDialogService dialogs, ILocalizationService localization, IActivityLog log)
    {
        _dialogs = dialogs;
        _localization = localization;
        _log = log;
    }

    public async Task<string?> SaveAsync(IStorageFile file, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(bytes);

        var path = file.TryGetLocalPath();

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(bytes);
            return path;
        }
        catch (IOException ex) when (IsLocked(ex) && path is not null)
        {
            // The target is open elsewhere (Word holds a .docx exclusively). Ask for another name.
            _log.Info($"Файл «{path}» зайнятий іншою програмою; пропоную зберегти під іншим іменем.");
        }
        catch (Exception ex)
        {
            _log.Error("Не вдалося зберегти експортований файл", ex);
            await ShowErrorAsync();
            return null;
        }

        return await SaveUnderAnotherNameAsync(path!, bytes);
    }

    /// <summary>
    /// Asks the user whether to save under a different name, pre-filling the first free "name (N).ext" beside
    /// the locked file, and writes to whatever they confirm. Returns null when they cancel.
    /// </summary>
    private async Task<string?> SaveUnderAnotherNameAsync(string lockedPath, byte[] bytes)
    {
        var folder = Path.GetDirectoryName(lockedPath) ?? string.Empty;

        var chosen = await _dialogs.ShowFileLockedAsync(new FileLockedViewModel(
            _localization,
            lockedFileName: Path.GetFileName(lockedPath),
            suggestedFileName: FirstFreeName(lockedPath),
            isNameFree: name => IsFreeIn(folder, name)));

        if (chosen is null)
        {
            _log.Info("Збереження скасовано користувачем (файл зайнятий).");
            return null;
        }

        var target = Path.Combine(folder, chosen);
        try
        {
            await File.WriteAllBytesAsync(target, bytes);
            _log.Info($"Експортований файл збережено як «{target}».");
            return target;
        }
        catch (Exception ex)
        {
            // The name was free a moment ago; something else claimed or blocked it in between.
            _log.Error($"Не вдалося зберегти експортований файл як «{target}»", ex);
            await ShowErrorAsync();
            return null;
        }
    }

    /// <summary>
    /// The first free numbered variant beside the given path. The number goes right after the leading type
    /// part — "Протокол результатів (2) - Кубок 2026 - День 1 - 2026-05-30.docx" — so the file still sorts
    /// with its own kind and reads as "the second results protocol", rather than being pushed past the date.
    /// A name with no separator (the user typed their own in the save dialog) is numbered at the end instead.
    /// Falls back to the last candidate tried if every one is taken — the dialog then flags it and the user
    /// types their own.
    /// </summary>
    private static string FirstFreeName(string lockedPath)
    {
        var folder = Path.GetDirectoryName(lockedPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(lockedPath);
        var ext = Path.GetExtension(lockedPath);

        // Split off the leading type part ("Протокол результатів") from the rest (" - Кубок 2026 - …").
        var cut = stem.IndexOf(ExportFileName.Separator, StringComparison.Ordinal);
        var (head, tail) = cut >= 0
            ? (stem[..cut], stem[cut..])
            : (stem, string.Empty);

        // An already-numbered head ("Протокол результатів (2)") is renumbered, not stacked up.
        head = StripNumber(head);

        var candidate = $"{head} (2){tail}{ext}";
        for (var n = 2; n <= MaxCandidates; n++)
        {
            candidate = $"{head} ({n}){tail}{ext}";
            if (IsFreeIn(folder, candidate))
                return candidate;
        }
        return candidate;
    }

    /// <summary>Drops a trailing " (N)" so re-saving a numbered file yields "(3)", not "(2) (2)".</summary>
    private static string StripNumber(string head)
    {
        var open = head.LastIndexOf(" (", StringComparison.Ordinal);
        if (open < 0 || !head.EndsWith(')'))
            return head;

        var inner = head[(open + 2)..^1];
        return inner.Length > 0 && inner.All(char.IsAsciiDigit) ? head[..open] : head;
    }

    /// <summary>True when <paramref name="name"/> is a usable file name that nothing occupies in the folder.</summary>
    private static bool IsFreeIn(string folder, string name)
    {
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        try
        {
            var full = Path.Combine(folder, name);
            return !File.Exists(full) && !Directory.Exists(full);
        }
        catch
        {
            // A name the path APIs reject outright (too long, reserved) — treat it as unusable.
            return false;
        }
    }

    /// <summary>
    /// True for a sharing violation / lock violation — the "someone else has this file open" case, as
    /// opposed to a missing folder or a read-only drive, which must not be retried under another name.
    /// </summary>
    private static bool IsLocked(IOException ex)
    {
        // Win32: ERROR_SHARING_VIOLATION (32) / ERROR_LOCK_VIOLATION (33) in the low word of the HRESULT;
        // POSIX maps a lock conflict to EACCES/EAGAIN, which .NET surfaces as a plain IOException, so on
        // other platforms fall back to "an IOException that isn't a missing path" — those cannot be fixed
        // by choosing another name and must surface as a real error instead.
        var code = ex.HResult & 0xFFFF;
        if (code is 32 or 33)
            return true;

        return !OperatingSystem.IsWindows()
               && ex is not (FileNotFoundException or DirectoryNotFoundException);
    }

    /// <summary>Shows the "could not write the file" message box (a confirm dialog in message mode).</summary>
    private Task ShowErrorAsync() =>
        _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
            _localization,
            titleKey: "Export.Save.ErrorTitle",
            messageKey: "Export.Save.Error",
            confirmKey: "Common.Ok",
            cancelKey: "Common.Close")
        {
            IsMessage = true
        });
}
