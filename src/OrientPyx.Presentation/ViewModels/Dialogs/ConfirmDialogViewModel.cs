using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// A reusable yes/no confirmation modal: a title, a short message, and Confirm/Cancel buttons.
/// Used for destructive actions (e.g. deleting a group) where the user should be asked first.
/// Callers <c>await</c> <see cref="Completion"/>, which yields true on Confirm and false on
/// Cancel/close.
/// </summary>
public sealed partial class ConfirmDialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConfirmDialogViewModel(
        ILocalizationService localization,
        string titleKey,
        string messageKey,
        string confirmKey = "Common.Delete",
        string cancelKey = "Common.Cancel")
    {
        Localization = localization;
        TitleKey = titleKey;
        MessageKey = messageKey;
        ConfirmKey = confirmKey;
        CancelKey = cancelKey;

        // Title/message/buttons all resolve through the Localization indexer in XAML; re-raise them
        // on a language switch so an open dialog re-localizes live.
        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Message));
            OnPropertyChanged(nameof(ConfirmText));
            OnPropertyChanged(nameof(CancelText));
        };
    }

    public ILocalizationService Localization { get; }

    public string TitleKey { get; }
    public string MessageKey { get; }
    public string ConfirmKey { get; }
    public string CancelKey { get; }

    /// <summary>
    /// Optional arguments formatted into the localized message via <see cref="string.Format(string, object?[])"/>.
    /// Lets a confirmation include dynamic values (e.g. a chip number and its current holder) while the
    /// message text itself stays a localizable resource with <c>{0}</c>/<c>{1}</c> placeholders.
    /// </summary>
    public object[]? MessageArgs { get; init; }

    public string Title => Localization.Get(TitleKey);
    public string Message => MessageArgs is { Length: > 0 }
        ? string.Format(Localization.Get(MessageKey), MessageArgs)
        : Localization.Get(MessageKey);
    public string ConfirmText => Localization.Get(ConfirmKey);
    public string CancelText => Localization.Get(CancelKey);

    /// <summary>Completes with true when the user confirms, false when they cancel or close.</summary>
    public Task<bool> Completion => _completion.Task;

    [RelayCommand]
    private void Confirm() => _completion.TrySetResult(true);

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(false);
}
