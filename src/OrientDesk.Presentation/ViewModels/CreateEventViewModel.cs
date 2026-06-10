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

    private const int MinDays = 1;
    private const int MaxDays = 31;

    // A plain int with a custom +/- stepper in the view. We avoid Avalonia's NumericUpDown: its
    // Windows automation peer crashes ("UnsupportedType Decimal") when it raises a value-changed
    // automation event while spinning.
    [ObservableProperty]
    private int _dayCount = 1;

    [ObservableProperty]
    private DateTimeOffset? _startDate;

    [ObservableProperty]
    private DateTimeOffset? _endDate;

    [ObservableProperty]
    private bool _isMultiDay;

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
        DayCount = 1;
        StartDate = null;
        EndDate = null;
        IsMultiDay = false;
        ErrorMessage = null;
        IsBusy = false;
    }

    [RelayCommand]
    private void IncrementDays()
    {
        if (DayCount < MaxDays)
            DayCount++;
    }

    [RelayCommand]
    private void DecrementDays()
    {
        if (DayCount > MinDays)
            DayCount--;
    }

    // Day count drives the multi-day flag (which reveals the end-date field) and auto-fills
    // a suggested end date; the user can still override the end date afterwards.
    partial void OnDayCountChanged(int value)
    {
        IsMultiDay = value > 1;
        AutoFillEndDate();
    }

    partial void OnStartDateChanged(DateTimeOffset? value) => AutoFillEndDate();

    // Suggests an end date when the start date or day count changes (last day = start + count-1).
    // The user can still edit the end date afterwards; it only re-fills on the next start/count change.
    private void AutoFillEndDate()
    {
        if (!IsMultiDay || StartDate is not { } start)
            return;

        EndDate = start.AddDays(Math.Max(1, DayCount) - 1);
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
            // Creation (file + migrations) runs off the UI thread; the post-create callback may
            // touch the UI (refresh + show the list), so it runs after the await on the UI thread.
            var dayCount = Math.Max(1, DayCount);
            // Single-day: the one "Дата" field is both start and end. Multi-day: use the (possibly
            // edited) end date, falling back to start + count-1 when it was left unset.
            var startDate = StartDate;
            var endDate = dayCount == 1
                ? startDate
                : EndDate ?? startDate?.AddDays(dayCount - 1);
            var summary = await _busy.RunAsync(
                () => _catalog.CreateEventAsync(Name, Identifier, Venue, dayCount, startDate, endDate));
            if (OnCreatedAsync is not null)
                await OnCreatedAsync(summary);
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
