using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;

namespace OrientDesk.Presentation.ViewModels;

/// <summary>Form for creating a new competition (name, identifier/folder, venue).</summary>
public sealed partial class CreateEventViewModel : ViewModelBase
{
    private readonly IEventCatalogService _catalog;
    private readonly IBusyService _busy;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _identifier = string.Empty;

    [ObservableProperty]
    private string _venue = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isBusy;

    public CreateEventViewModel(IEventCatalogService catalog, IBusyService busy, ILocalizationService localization)
    {
        _catalog = catalog;
        _busy = busy;
        Localization = localization;
    }

    public ILocalizationService Localization { get; }

    /// <summary>
    /// Awaited after a competition is created, so the host can refresh and show the list
    /// while the loader is still on screen. Set by the host.
    /// </summary>
    public Func<EventSummary, Task>? OnCreatedAsync { get; set; }

    /// <summary>Raised when the user cancels creation.</summary>
    public event EventHandler? Cancelled;

    public void Reset()
    {
        Name = string.Empty;
        Identifier = string.Empty;
        Venue = string.Empty;
        ErrorMessage = null;
        IsBusy = false;
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Identifier))
        {
            ErrorMessage = Localization.Get("CreateEvent.Error.Required");
            return;
        }

        try
        {
            IsBusy = true;
            await _busy.RunAsync(async () =>
            {
                var summary = await _catalog.CreateEventAsync(Name, Identifier, Venue);
                if (OnCreatedAsync is not null)
                    await OnCreatedAsync(summary);
            });
        }
        catch (InvalidOperationException)
        {
            ErrorMessage = Localization.Get("CreateEvent.Error.Duplicate");
        }
        catch (ArgumentException)
        {
            ErrorMessage = Localization.Get("CreateEvent.Error.Invalid");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke(this, EventArgs.Empty);
}
