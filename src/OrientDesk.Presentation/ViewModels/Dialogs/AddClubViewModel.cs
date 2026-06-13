using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Dialogs;

/// <summary>
/// Modal for creating a new club from the participants page's club dropdown ("+ новий"): a single name
/// field. Callers <c>await</c> <see cref="Completion"/> for the trimmed name, or null on cancel.
/// Mirrors <see cref="AddRegionViewModel"/>.
/// </summary>
public sealed partial class AddClubViewModel : ObservableObject
{
    private readonly TaskCompletionSource<string?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AddClubViewModel(ILocalizationService localization)
    {
        Localization = localization;
        Localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public ILocalizationService Localization { get; }

    public string Title => Localization.Get("Clubs.Add.Title");

    /// <summary>The new club's name.</summary>
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
