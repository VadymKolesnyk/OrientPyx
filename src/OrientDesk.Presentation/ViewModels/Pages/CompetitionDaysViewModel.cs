using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// Table of the current competition's days. Each row can edit its date, venue and discipline,
/// and be made the active (current) day, which switches the running session.
/// Opened from the "Competition → Days" top menu.
/// </summary>
public sealed partial class CompetitionDaysViewModel : PageViewModelBase
{
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;

    public CompetitionDaysViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IBusyService busy)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _busy = busy;
        // Singleton VM: reload the day rows whenever the competition/day changes so a switched
        // event never leaves the previous competition's days on screen.
        _session.SessionChanged += (_, _) => _ = LoadAsync();
    }

    public override string NavKey => "Nav.CompetitionDays";
    public override string TitleKey => "Page.CompetitionDays.Title";
    public override string TextKey => "Page.CompetitionDays.Text";

    public ObservableCollection<DayRowViewModel> Days { get; } = [];

    /// <summary>Reloads the day rows from the current competition. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        Days.Clear();

        var activeNumber = _session.CurrentDay?.Number;
        var days = await _editor.GetDaysAsync();
        foreach (var day in days)
            Days.Add(new DayRowViewModel(day, day.Number == activeNumber, Localization));
    }

    [RelayCommand]
    private async Task AddDayAsync()
    {
        await _busy.RunAsync(() => _editor.AddDayAsync());
        await LoadAsync();
    }

    [RelayCommand]
    private async Task SaveDayAsync(DayRowViewModel? row)
    {
        if (row is null)
            return;

        await _busy.RunAsync(() => _editor.UpdateDayAsync(row.ToEntity()));
        row.MarkSaved();
    }

    [RelayCommand]
    private async Task DeleteDayAsync(DayRowViewModel? row)
    {
        if (row is null)
            return;

        // Keep at least one day, and never delete the active one.
        if (Days.Count <= 1 || row.IsActive)
            return;

        await _busy.RunAsync(() => _editor.DeleteDayAsync(row.Id));
        await LoadAsync();
    }
}
