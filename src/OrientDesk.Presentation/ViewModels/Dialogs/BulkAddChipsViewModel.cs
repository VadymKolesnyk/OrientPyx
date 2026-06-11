using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Dialogs;

/// <summary>
/// Modal for adding a contiguous range of rental chips: a start number, how many to add, and an
/// optional note applied to all of them (e.g. "100 chips from 9007400"). Callers <c>await</c>
/// <see cref="Completion"/> for the entered values, or null on cancel. Mirrors the
/// <see cref="ImportOptionsViewModel"/> TaskCompletionSource pattern.
/// </summary>
public sealed partial class BulkAddChipsViewModel : ObservableObject
{
    private readonly TaskCompletionSource<BulkAddChipsResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BulkAddChipsViewModel(ILocalizationService localization)
    {
        Localization = localization;
        Localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public ILocalizationService Localization { get; }

    public string Title => Localization.Get("Chips.BulkAdd.Title");

    /// <summary>First chip number in the range, e.g. "9007400". Leading zeros are preserved.</summary>
    [ObservableProperty]
    private string _startNumber = string.Empty;

    /// <summary>How many chips to add. Bound to a +/- stepper.</summary>
    [ObservableProperty]
    private int _count = 100;

    /// <summary>Optional note applied to every chip added in this batch.</summary>
    [ObservableProperty]
    private string _note = string.Empty;

    /// <summary>Completes with the entered values on confirm, or null on cancel/close.</summary>
    public Task<BulkAddChipsResult?> Completion => _completion.Task;

    [RelayCommand]
    private void Increment() => Count++;

    [RelayCommand]
    private void Decrement()
    {
        if (Count > 1)
            Count--;
    }

    [RelayCommand]
    private void Confirm() =>
        _completion.TrySetResult(new BulkAddChipsResult(
            (StartNumber ?? string.Empty).Trim(),
            Count,
            (Note ?? string.Empty).Trim()));

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}

/// <summary>The values entered in the bulk-add modal.</summary>
/// <param name="StartNumber">First chip number in the range.</param>
/// <param name="Count">How many chips to add.</param>
/// <param name="Note">Note applied to every chip in the batch.</param>
public sealed record BulkAddChipsResult(string StartNumber, int Count, string Note);
