using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>The user's decision when importing a competition archive.</summary>
/// <param name="Identifier">The folder name to import as.</param>
/// <param name="Overwrite">True to replace an existing competition with that identifier.</param>
public sealed record ImportEventDecision(string Identifier, bool Overwrite);

/// <summary>
/// Modal shown when importing a competition archive. If a competition with the archive's own identifier
/// already exists, the user picks between overwriting it and importing under a new, unique identifier
/// (validated live). If the identifier is free, it's a simple confirm. Callers <c>await</c>
/// <see cref="Completion"/> for the decision, or null on cancel.
/// </summary>
public sealed partial class ImportEventViewModel : ObservableObject
{
    private readonly TaskCompletionSource<ImportEventDecision?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Checks whether a candidate identifier is a valid, currently-unused folder name (hits the events
    // folder, so it's async). Supplied by the host flow.
    private readonly Func<string, Task<bool>> _isAvailableAsync;

    // Guards against a stale async validation result overwriting a newer one (last edit wins).
    private int _validationToken;

    public ImportEventViewModel(
        ILocalizationService localization,
        string archiveIdentifier,
        bool identifierExists,
        Func<string, Task<bool>> isAvailableAsync)
    {
        Localization = localization;
        ArchiveIdentifier = archiveIdentifier;
        IdentifierExists = identifierExists;
        _isAvailableAsync = isAvailableAsync;

        // Default the "new name" field to the archive's identifier so the user only tweaks it.
        _newIdentifier = archiveIdentifier;

        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Message));
        };

        if (identifierExists)
            _ = ValidateAsync(archiveIdentifier);
    }

    public ILocalizationService Localization { get; }

    /// <summary>The identifier stored inside the archive.</summary>
    public string ArchiveIdentifier { get; }

    /// <summary>True when a competition with <see cref="ArchiveIdentifier"/> already exists.</summary>
    public bool IdentifierExists { get; }

    /// <summary>True when the identifier is free, so a plain confirm is all that's needed.</summary>
    public bool IdentifierFree => !IdentifierExists;

    public string Title => Localization.Get("ImportEvent.Title");

    public string Message => IdentifierExists
        ? string.Format(Localization.Get("ImportEvent.Conflict"), ArchiveIdentifier)
        : string.Format(Localization.Get("ImportEvent.Confirm"), ArchiveIdentifier);

    /// <summary>The candidate new identifier (only used when overwriting is declined).</summary>
    [ObservableProperty]
    private string _newIdentifier;

    /// <summary>True once the entered new identifier is valid and unused.</summary>
    [ObservableProperty]
    private bool _isNewIdentifierAvailable;

    /// <summary>Localized message describing why the entered identifier can't be used (blank when it can).</summary>
    [ObservableProperty]
    private string? _newIdentifierError;

    partial void OnNewIdentifierChanged(string value) => _ = ValidateAsync(value);

    private async Task ValidateAsync(string candidate)
    {
        var token = ++_validationToken;
        var available = await _isAvailableAsync(candidate ?? string.Empty);
        if (token != _validationToken)
            return; // a newer edit superseded this check

        IsNewIdentifierAvailable = available;
        NewIdentifierError = available
            ? null
            : Localization.Get("ImportEvent.NameTaken");
    }

    /// <summary>Completes with the decision on confirm, or null on cancel/close.</summary>
    public Task<ImportEventDecision?> Completion => _completion.Task;

    /// <summary>Confirms overwriting the existing competition (only offered when it exists).</summary>
    [RelayCommand]
    private void Overwrite() => _completion.TrySetResult(new ImportEventDecision(ArchiveIdentifier, Overwrite: true));

    /// <summary>
    /// The single OK for the free-identifier case, or "import under new name" for the conflict case.
    /// Blocked (no-op) while the entered new identifier is unavailable.
    /// </summary>
    [RelayCommand]
    private void Confirm()
    {
        if (IdentifierFree)
        {
            _completion.TrySetResult(new ImportEventDecision(ArchiveIdentifier, Overwrite: false));
            return;
        }

        var trimmed = (NewIdentifier ?? string.Empty).Trim();
        if (!IsNewIdentifierAvailable || trimmed.Length == 0)
            return;
        _completion.TrySetResult(new ImportEventDecision(trimmed, Overwrite: false));
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}
