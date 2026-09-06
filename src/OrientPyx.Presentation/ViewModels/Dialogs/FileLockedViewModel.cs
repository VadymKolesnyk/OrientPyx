using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// Shown when an export cannot overwrite its target because another program holds it open (Word keeps a
/// .docx locked). Offers to save under a different name: the field is pre-filled with the first free
/// "name (2).ext" beside the locked file and re-checked as the user types, so confirming always writes a
/// name that is actually free. Callers <c>await</c> <see cref="Completion"/> for the chosen file name
/// (no path), or null on cancel.
/// </summary>
public sealed partial class FileLockedViewModel : ObservableObject
{
    private readonly TaskCompletionSource<string?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Decides whether a candidate name is free in the target folder. Injected so the VM stays
    /// testable and does no file I/O of its own.</summary>
    private readonly Func<string, bool> _isNameFree;

    public FileLockedViewModel(
        ILocalizationService localization,
        string lockedFileName,
        string suggestedFileName,
        Func<string, bool> isNameFree)
    {
        Localization = localization;
        LockedFileName = lockedFileName;
        _isNameFree = isNameFree;
        _fileName = suggestedFileName;

        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Message));
            OnPropertyChanged(nameof(TakenWarning));
        };

        Validate();
    }

    public ILocalizationService Localization { get; }

    /// <summary>The name of the file that could not be overwritten, shown in the message.</summary>
    public string LockedFileName { get; }

    public string Title => Localization.Get("Export.Locked.Title");

    public string Message => string.Format(Localization.Get("Export.Locked.Message"), LockedFileName);

    /// <summary>The name to save under, pre-filled with a free one and editable.</summary>
    [ObservableProperty]
    private string _fileName;

    /// <summary>False while the typed name is blank or already taken — blocks the confirm button.</summary>
    [ObservableProperty]
    private bool _canConfirm = true;

    /// <summary>True when the typed name is taken, showing the inline warning under the field.</summary>
    [ObservableProperty]
    private bool _isTaken;

    public string TakenWarning => Localization.Get("Export.Locked.Taken");

    /// <summary>Completes with the chosen file name (no path) on confirm, or null on cancel/close.</summary>
    public Task<string?> Completion => _completion.Task;

    partial void OnFileNameChanged(string value) => Validate();

    // The typed name must be non-blank and free — the whole point of the dialog is to land on a name that
    // can actually be written. Re-run on every keystroke (the TextBox updates on PropertyChanged).
    private void Validate()
    {
        var trimmed = (FileName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            IsTaken = false;
            CanConfirm = false;
            return;
        }

        IsTaken = !_isNameFree(trimmed);
        CanConfirm = !IsTaken;
    }

    [RelayCommand]
    private void Confirm()
    {
        var trimmed = (FileName ?? string.Empty).Trim();
        if (trimmed.Length == 0 || !_isNameFree(trimmed))
            return; // the button is disabled in this state; ignore a stray Enter

        _completion.TrySetResult(trimmed);
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}
