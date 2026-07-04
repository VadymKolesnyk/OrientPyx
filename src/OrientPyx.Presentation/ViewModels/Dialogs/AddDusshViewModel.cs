using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// Modal for creating a new sports school (ДЮСШ) from the participants page's school dropdown
/// ("+ новий"): a single name field. Callers <c>await</c> <see cref="Completion"/> for the trimmed
/// name, or null on cancel. Mirrors <see cref="AddClubViewModel"/>.
/// </summary>
public sealed partial class AddDusshViewModel : ObservableObject
{
    private readonly TaskCompletionSource<string?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AddDusshViewModel(ILocalizationService localization)
    {
        Localization = localization;
        Localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public ILocalizationService Localization { get; }

    public string Title => Localization.Get("Dussh.Add.Title");

    /// <summary>The new school's name.</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>Completes with the trimmed name on confirm, or null on cancel/close.</summary>
    public Task<string?> Completion => _completion.Task;

    [RelayCommand]
    private void Confirm()
    {
        var trimmed = (Name ?? string.Empty).Trim();
        _completion.TrySetResult(trimmed.Length == 0 ? null : trimmed);
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}
