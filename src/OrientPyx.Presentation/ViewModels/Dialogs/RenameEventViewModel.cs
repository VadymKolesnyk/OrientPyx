using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// Modal for changing a competition's identifier — the name of its folder under the events path.
/// The entered name is validated live against the events folder (valid folder name, not already
/// taken); confirming is blocked until it is free and differs from the current one. Callers
/// <c>await</c> <see cref="Completion"/> for the new identifier, or null on cancel.
/// </summary>
public sealed partial class RenameEventViewModel : ObservableObject
{
    private readonly TaskCompletionSource<string?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Checks whether a candidate identifier is a valid, currently-unused folder name (hits the events
    // folder, so it's async). Supplied by the caller.
    private readonly Func<string, Task<bool>> _isAvailableAsync;

    // Guards against a stale async validation result overwriting a newer one (last edit wins).
    private int _validationToken;

    public RenameEventViewModel(
        ILocalizationService localization,
        string currentIdentifier,
        Func<string, Task<bool>> isAvailableAsync)
    {
        Localization = localization;
        CurrentIdentifier = currentIdentifier;
        _isAvailableAsync = isAvailableAsync;
        _newIdentifier = currentIdentifier;

        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Message));
            OnPropertyChanged(nameof(Warning));
        };
    }

    public ILocalizationService Localization { get; }

    /// <summary>The identifier the competition currently has.</summary>
    public string CurrentIdentifier { get; }

    public string Title => Localization.Get("RenameEvent.Title");

    public string Message => string.Format(Localization.Get("RenameEvent.Message"), CurrentIdentifier);

    /// <summary>Explains that the folder itself moves, so external links to it break.</summary>
    public string Warning => Localization.Get("RenameEvent.Warning");

    /// <summary>The candidate new identifier.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _newIdentifier;

    /// <summary>True once the entered identifier is valid and unused.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private bool _isAvailable;

    /// <summary>Localized message describing why the entered identifier can't be used (blank when it can).</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>Confirm is enabled only for a different, free, valid identifier.</summary>
    public bool CanConfirm =>
        IsAvailable && !string.Equals((NewIdentifier ?? string.Empty).Trim(), CurrentIdentifier, StringComparison.Ordinal);

    /// <summary>Completes with the new identifier on confirm, or null on cancel/close.</summary>
    public Task<string?> Completion => _completion.Task;

    partial void OnNewIdentifierChanged(string value) => _ = ValidateAsync(value);

    private async Task ValidateAsync(string candidate)
    {
        var trimmed = (candidate ?? string.Empty).Trim();

        // The competition's own folder is "taken" by itself, so the availability check would reject both
        // the unchanged name and a pure case change ("cup" → "Cup", the same folder on Windows). Treat
        // any case-insensitive match with the current identifier as free; CanConfirm still blocks the
        // truly unchanged name because it must differ.
        if (string.Equals(trimmed, CurrentIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            _validationToken++;
            IsAvailable = trimmed.Length > 0;
            Error = null;
            return;
        }

        var token = ++_validationToken;
        var available = await _isAvailableAsync(trimmed);
        if (token != _validationToken)
            return; // a newer edit superseded this check

        IsAvailable = available;
        Error = available ? null : Localization.Get("RenameEvent.NameTaken");
    }

    [RelayCommand]
    private void Confirm()
    {
        if (CanConfirm)
            _completion.TrySetResult((NewIdentifier ?? string.Empty).Trim());
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}
