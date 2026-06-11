using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Dialogs;

/// <summary>
/// Modal for changing a day's number. Shows the day's current number and lets the user pick a new
/// one via a +/- stepper; confirming is only allowed when the chosen number differs from the current
/// one and is not already used by another day. Callers <c>await</c> <see cref="Completion"/> for the
/// chosen number, or null on cancel. Mirrors the <see cref="BulkAddChipsViewModel"/> pattern.
/// </summary>
public sealed partial class ChangeDayNumberViewModel : ObservableObject
{
    private readonly TaskCompletionSource<int?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly int _currentNumber;
    private readonly IReadOnlySet<int> _takenNumbers;

    public ChangeDayNumberViewModel(
        ILocalizationService localization,
        int currentNumber,
        IReadOnlySet<int> takenByOtherDays)
    {
        Localization = localization;
        _currentNumber = currentNumber;
        _takenNumbers = takenByOtherDays;
        _newNumber = currentNumber;

        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Message));
            OnPropertyChanged(nameof(Error));
        };
    }

    public ILocalizationService Localization { get; }

    public string Title => Localization.Get("CompetitionDays.ChangeNumber.Title");

    public string Message => string.Format(
        Localization.Get("CompetitionDays.ChangeNumber.Message"), _currentNumber);

    /// <summary>The new number the user is picking; bound to a +/- stepper.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private int _newNumber;

    /// <summary>Validation text shown when the chosen number is taken; empty when it's valid.</summary>
    public string Error =>
        NewNumber != _currentNumber && _takenNumbers.Contains(NewNumber)
            ? string.Format(Localization.Get("CompetitionDays.ChangeNumber.Taken"), NewNumber)
            : string.Empty;

    /// <summary>Confirm is enabled only for a different, free number.</summary>
    public bool CanConfirm => NewNumber >= 1 && NewNumber != _currentNumber && !_takenNumbers.Contains(NewNumber);

    /// <summary>Completes with the chosen number on confirm, or null on cancel/close.</summary>
    public Task<int?> Completion => _completion.Task;

    [RelayCommand]
    private void Increment() => NewNumber++;

    [RelayCommand]
    private void Decrement()
    {
        if (NewNumber > 1)
            NewNumber--;
    }

    [RelayCommand]
    private void Confirm()
    {
        if (CanConfirm)
            _completion.TrySetResult(NewNumber);
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}
