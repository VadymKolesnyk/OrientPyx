using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// Modal for bulk-assigning sequential start numbers to the participants currently shown in the
/// table: a starting number and a "reassign existing" toggle. When the toggle is off, participants
/// that already have a number are skipped and numbers already taken by others are stepped over; when
/// on, every visible row is renumbered and any number held by another participant is freed first.
/// Callers <c>await</c> <see cref="Completion"/> for the entered values, or null on cancel. Mirrors
/// the <see cref="BulkAddChipsViewModel"/> TaskCompletionSource pattern.
/// </summary>
public sealed partial class AssignNumbersViewModel : ObservableObject
{
    private readonly TaskCompletionSource<AssignNumbersResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="startNumber">The pre-filled start number (typically the largest assigned number + 1).</param>
    public AssignNumbersViewModel(ILocalizationService localization, int startNumber = 1)
    {
        Localization = localization;
        _startNumber = startNumber < 1 ? 1 : startNumber;
        Localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public ILocalizationService Localization { get; }

    public string Title => Localization.Get("Participants.AssignNumbers.Title");

    /// <summary>The number the first assigned participant receives. Bound to a +/- stepper.</summary>
    [ObservableProperty]
    private int _startNumber;

    /// <summary>
    /// When true, every visible row is renumbered (even if it already had a number) and a number held
    /// by another participant is cleared from that participant before reuse. When false, already-numbered
    /// rows are left alone and taken numbers are skipped.
    /// </summary>
    [ObservableProperty]
    private bool _reassignExisting;

    /// <summary>Completes with the entered values on confirm, or null on cancel/close.</summary>
    public Task<AssignNumbersResult?> Completion => _completion.Task;

    [RelayCommand]
    private void Increment() => StartNumber++;

    [RelayCommand]
    private void Decrement()
    {
        if (StartNumber > 1)
            StartNumber--;
    }

    [RelayCommand]
    private void Confirm() =>
        _completion.TrySetResult(new AssignNumbersResult(StartNumber, ReassignExisting));

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}

/// <summary>The values entered in the assign-numbers modal.</summary>
/// <param name="StartNumber">The number the first assigned participant receives.</param>
/// <param name="ReassignExisting">Whether to renumber rows that already have a number and free taken numbers.</param>
public sealed record AssignNumbersResult(int StartNumber, bool ReassignExisting);
