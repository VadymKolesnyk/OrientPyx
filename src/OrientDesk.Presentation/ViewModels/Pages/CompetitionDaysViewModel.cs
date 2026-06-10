using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;
using OrientDesk.Presentation.ViewModels.Dialogs;

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
    private readonly IDialogService _dialogs;

    public CompetitionDaysViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IBusyService busy,
        IDialogService dialogs)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _busy = busy;
        _dialogs = dialogs;
        // Singleton VM: reload the day rows whenever the competition/day changes so a switched
        // event never leaves the previous competition's days on screen. The event may arrive on a
        // pool thread (session writes run inside RunAsync), so marshal LoadAsync onto the UI thread.
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
    }

    public override string NavKey => "Nav.CompetitionDays";
    public override string TitleKey => "Page.CompetitionDays.Title";
    public override string TextKey => "Page.CompetitionDays.Text";

    public ObservableCollection<DayRowViewModel> Days { get; } = [];

    /// <summary>The row selected in the grid; the Delete key acts on it.</summary>
    [ObservableProperty]
    private DayRowViewModel? _selectedRow;

    /// <summary>Reloads the day rows from the current competition. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        var activeNumber = _session.CurrentDay?.Number;
        // BD read runs off the UI thread; the collection is rebuilt afterwards on the UI thread.
        var days = await _busy.RunAsync(() => _editor.GetDaysAsync());

        Days.Clear();
        SelectedRow = null;
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

    // The grid's delete button binds to this command. A plain click asks for confirmation first;
    // Ctrl+Click routes through DeleteDayNoConfirm. The Delete key on a selected row is routed
    // through DeleteSelectedDayAsync in the view.
    [RelayCommand]
    private Task DeleteDayAsync(DayRowViewModel? row) => RemoveDayAsync(row, skipConfirm: false);

    /// <summary>Deletes a row without the confirmation prompt (Ctrl+Click / Ctrl+Delete).</summary>
    public Task DeleteDayNoConfirmAsync(DayRowViewModel? row) => RemoveDayAsync(row, skipConfirm: true);

    /// <summary>Deletes the currently selected day (Delete key); confirms unless skipConfirm.</summary>
    public Task DeleteSelectedDayAsync(bool skipConfirm) => RemoveDayAsync(SelectedRow, skipConfirm);

    private async Task RemoveDayAsync(DayRowViewModel? row, bool skipConfirm)
    {
        if (row is null)
            return;

        // Keep at least one day, and never delete the active one.
        if (Days.Count <= 1 || row.IsActive)
            return;

        if (!skipConfirm)
        {
            var confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "CompetitionDays.Delete.ConfirmTitle",
                messageKey: "CompetitionDays.Delete.ConfirmMessage"));
            if (!confirmed)
                return;
        }

        await _busy.RunAsync(() => _editor.DeleteDayAsync(row.Id));
        await LoadAsync();
    }
}
