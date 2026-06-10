using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Disciplines;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;
using OrientDesk.Presentation.ViewModels.Dialogs;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// Spreadsheet-like groups table for the CURRENT competition day. A group exists at the
/// competition level; its presence here is a per-day attachment. Adding by name reuses an existing
/// group; deleting removes it only from this day (and the group entirely once it runs on no day).
/// Cells auto-save in the background (debounced per row) — no Save button, no busy overlay.
/// </summary>
public sealed partial class GroupsViewModel : PageViewModelBase
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(600);

    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IXmlImportFlow _importFlow;
    private readonly IBusyService _busy;
    private readonly IDisciplineStrategyProvider _strategies;
    private readonly IDialogService _dialogs;
    private readonly Dictionary<Guid, CancellationTokenSource> _saveTimers = new();

    public GroupsViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IXmlImportFlow importFlow,
        IBusyService busy,
        IDisciplineStrategyProvider strategies,
        IDialogService dialogs)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _importFlow = importFlow;
        _busy = busy;
        _strategies = strategies;
        _dialogs = dialogs;
        Localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SelectedCourseHeader));
        // Singleton VM: when the competition/day changes, drop the previous event's rows so the
        // page never shows stale data before it is next opened. The event can be raised on a pool
        // thread (session writes run inside RunAsync), so marshal LoadAsync onto the UI thread.
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
    }

    public override string NavKey => "Nav.Groups";
    public override string TitleKey => "Page.Groups.Title";
    public override string TextKey => "Page.Groups.Text";

    public ObservableCollection<GroupDayRowViewModel> Groups { get; } = [];

    /// <summary>Selectable days for the top-right day picker.</summary>
    public ObservableCollection<DayOption> DayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _selectedDay;

    /// <summary>Day picker is shown only when the competition has more than one day.</summary>
    public bool ShowDaySelector => DayOptions.Count > 1;

    // The row whose course / control list is shown in the bottom editor. Null when nothing is
    // selected, which dims the editor and shows a hint instead.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCourseHeader))]
    [NotifyPropertyChangedFor(nameof(HasSelectedGroup))]
    private GroupDayRowViewModel? _selectedGroup;

    /// <summary>True when a group is selected, so the bottom course editor is enabled.</summary>
    public bool HasSelectedGroup => SelectedGroup is not null;

    /// <summary>Caption above the bottom course editor — names the selected group, or prompts.</summary>
    public string SelectedCourseHeader => SelectedGroup is { } row
        ? string.Format(
            Localization.Get("Groups.SelectedCourse"),
            string.IsNullOrWhiteSpace(row.Name) ? "—" : row.Name)
        : Localization.Get("Groups.SelectedCourse.None");

    // Type-specific columns are shown only when at least one row's effective discipline uses them.
    // (Course order, control count and time limit are used by every discipline, so they have no toggle.)
    public bool ShowRequiredCountColumn => Groups.Any(r => r.UsesRequiredCount);
    public bool ShowPenaltyColumn => Groups.Any(r => r.UsesPenalty);

    /// <summary>Raised when the set of visible type-specific columns may have changed; the view
    /// re-applies column visibility in response (columns live outside the visual tree).</summary>
    public event EventHandler? ColumnsChanged;

    // True while LoadAsync syncs SelectedDay to the session, so the setter does NOT call
    // SetCurrentDayAsync (which would re-raise SessionChanged → LoadAsync in a loop).
    private bool _syncingDay;

    /// <summary>Reloads the groups for the current day. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        CancelAllTimers();

        // Both BD reads run off the UI thread; every collection/property write below happens
        // afterwards on the UI thread (SQLite has no real async I/O, so this can't stay inline).
        var hasDay = _session.CurrentDay is not null;
        var (days, rows) = await _busy.RunAsync(async () =>
        {
            var d = await _editor.GetDaysAsync();
            var r = hasDay ? await _editor.GetGroupDayRowsAsync() : (IReadOnlyList<GroupDayRow>)[];
            return (d, r);
        });

        foreach (var existing in Groups)
            existing.PropertyChanged -= OnRowPropertyChanged;
        Groups.Clear();
        SelectedGroup = null;

        _syncingDay = true;
        try
        {
            // Rebuild the options only when the day set actually changed; otherwise keep the
            // existing DayOption instances so the ComboBox's SelectedItem stays a valid reference
            // (a fresh list would leave the picker showing nothing after a day switch).
            if (!SameDays(days))
            {
                DayOptions.Clear();
                foreach (var day in days)
                    DayOptions.Add(new DayOption(day, Localization));
            }

            var current = _session.CurrentDay?.Number;
            SelectedDay = DayOptions.FirstOrDefault(o => o.Number == current) ?? DayOptions.FirstOrDefault();
        }
        finally
        {
            _syncingDay = false;
        }

        OnPropertyChanged(nameof(ShowDaySelector));

        foreach (var row in rows)
            Groups.Add(CreateRow(row));

        RaiseColumnVisibility();
    }

    // Builds a row VM wired with the discipline provider and a watch on its discipline so the grid's
    // column visibility refreshes when a group's effective type changes.
    private GroupDayRowViewModel CreateRow(GroupDayRow row)
    {
        var vm = new GroupDayRowViewModel(row, Localization, _strategies, RequestRowSave);
        vm.PropertyChanged += OnRowPropertyChanged;
        return vm;
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GroupDayRowViewModel.EffectiveDiscipline))
            RaiseColumnVisibility();

        // Keep the bottom editor's caption in sync while the selected group is being renamed in-grid.
        if (e.PropertyName == nameof(GroupDayRowViewModel.Name) && ReferenceEquals(sender, SelectedGroup))
            OnPropertyChanged(nameof(SelectedCourseHeader));
    }

    private void RaiseColumnVisibility()
    {
        OnPropertyChanged(nameof(ShowRequiredCountColumn));
        OnPropertyChanged(nameof(ShowPenaltyColumn));
        ColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    // True when the current options already represent exactly these days (same count and numbers,
    // in order), so the list can be reused instead of rebuilt.
    private bool SameDays(IReadOnlyList<EventDay> days)
    {
        if (DayOptions.Count != days.Count)
            return false;
        for (var i = 0; i < days.Count; i++)
            if (DayOptions[i].Number != days[i].Number)
                return false;
        return true;
    }

    // Driven by the day ComboBox. Switching the session's day re-raises SessionChanged, which
    // reloads this page; the _syncingDay guard stops LoadAsync's reassignment from re-entering.
    partial void OnSelectedDayChanged(DayOption? value)
    {
        if (_syncingDay || value is null)
            return;
        if (_session.CurrentDay?.Number == value.Number)
            return;

        _ = SwitchDayAsync(value.Day);
    }

    private async Task SwitchDayAsync(EventDay day)
    {
        await _busy.RunAsync(() => _session.SetCurrentDayAsync(day));
    }

    [RelayCommand]
    private async Task AddGroupAsync()
    {
        if (_session.CurrentDay is null)
            return;

        // Add a blank, named-later row (the user types the name in-grid; the debounced save then
        // persists the rename). Mirrors the control-points "add blank row" flow.
        var row = await _busy.RunAsync(() => _editor.AddGroupToDayAsync(string.Empty));
        Groups.Add(CreateRow(row));
        RaiseColumnVisibility();
    }

    [RelayCommand]
    private async Task PullAllGroupsAsync()
    {
        if (_session.CurrentDay is null)
            return;

        await _busy.RunAsync(() => _editor.PullAllGroupsIntoDayAsync());
        // Reload so the merged set renders in the correct order.
        await LoadAsync();
    }

    /// <summary>
    /// Runs the single "Import from XML" action (chosen by the view's file picker): the shared flow
    /// parses the file, shows the two-toggle modal, and imports both control points and groups for
    /// the current day. This page then reloads its groups.
    /// </summary>
    public async Task ImportFromXmlAsync(string xml)
    {
        if (await _importFlow.RunAsync(xml))
            await LoadAsync();
    }

    // The grid's delete button binds to this command. A plain click asks for confirmation first;
    // Ctrl+Click passes skipConfirm via DeleteGroupNoConfirm below. The Delete key on a selected row
    // is routed through DeleteSelectedGroupAsync in the view.
    [RelayCommand]
    private Task DeleteGroupAsync(GroupDayRowViewModel? row) => RemoveGroupAsync(row, skipConfirm: false);

    /// <summary>Deletes a row without the confirmation prompt (Ctrl+Click / Ctrl+Delete).</summary>
    public Task DeleteGroupNoConfirmAsync(GroupDayRowViewModel? row) => RemoveGroupAsync(row, skipConfirm: true);

    /// <summary>Deletes the currently selected group (Delete key); confirms unless skipConfirm.</summary>
    public Task DeleteSelectedGroupAsync(bool skipConfirm) => RemoveGroupAsync(SelectedGroup, skipConfirm);

    private async Task RemoveGroupAsync(GroupDayRowViewModel? row, bool skipConfirm)
    {
        if (row is null)
            return;

        if (!skipConfirm)
        {
            var confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "Groups.Delete.ConfirmTitle",
                messageKey: "Groups.Delete.ConfirmMessage"));
            if (!confirmed)
                return;
        }

        if (_saveTimers.TryGetValue(row.Id, out var cts))
        {
            cts.Cancel();
            _saveTimers.Remove(row.Id);
        }

        await _busy.RunAsync(() => _editor.RemoveGroupFromDayAsync(row.Id, row.GroupId));
        row.PropertyChanged -= OnRowPropertyChanged;
        if (ReferenceEquals(SelectedGroup, row))
            SelectedGroup = null;
        Groups.Remove(row);
        RaiseColumnVisibility();
    }

    // Invoked by a row on every edit (UI thread). Resets that row's debounce timer.
    private void RequestRowSave(GroupDayRowViewModel row)
    {
        if (_saveTimers.TryGetValue(row.Id, out var existing))
            existing.Cancel();

        var cts = new CancellationTokenSource();
        _saveTimers[row.Id] = cts;
        _ = SaveRowDebouncedAsync(row, cts.Token); // fire-and-forget; the UI is never blocked
    }

    private async Task SaveRowDebouncedAsync(GroupDayRowViewModel row, CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounce, token);
            // ToRow() reads VM state, so snapshot it here (UI thread) before offloading the
            // synchronous SQLite write to the pool. Autosave bypasses the busy overlay on purpose.
            var dto = row.ToRow();
            await Task.Run(() => _editor.UpdateGroupDayRowAsync(dto, token), token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer edit (or the page reloaded) — ignore.
        }
        catch
        {
            // Background save failed; never crash the UI over an autosave.
        }
    }

    private void CancelAllTimers()
    {
        foreach (var cts in _saveTimers.Values)
            cts.Cancel();
        _saveTimers.Clear();
    }
}
