using System;
using System.Linq;
using System.Threading.Tasks;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Drives the day lock from the UI side: toggling it behind the right confirmation, and turning a
/// refused write (<see cref="BusinessLogic.Models.DayLockedException"/>) into a message the operator
/// understands. Pages hold one of these instead of repeating the flow, so the lock behaves the same
/// next to every day selector.
///
/// Closing a day needs no prompt — it is the safe direction and trivially undone. Opening one does,
/// because that is the moment the protection is deliberately dropped.
/// </summary>
public interface IDayLockService
{
    /// <summary>True when the session's current day is closed for editing.</summary>
    bool IsCurrentDayLocked { get; }

    /// <summary>
    /// Flips the current day's lock, confirming first when unlocking. Returns true when the state
    /// actually changed (the caller then reloads its page so the new state reaches every cell).
    /// </summary>
    Task<bool> ToggleCurrentDayAsync();

    /// <summary>
    /// Shows the "this day is closed" message for a write that was refused. Pass the day number the
    /// exception carried.
    /// </summary>
    Task ShowBlockedAsync(int dayNumber);

    /// <summary>
    /// Explains why the cell the user just tried to edit is read-only, and how to make it editable
    /// again. Shown when they click into a cell held by a closed day, where nothing else would happen.
    /// </summary>
    /// <param name="dayNumber">
    /// The closed day the cell belongs to. Pass 0 for "whatever day the page is on" — the roster shows
    /// every day at once, so a cell there must name its own day rather than the session's.
    /// </param>
    /// <param name="merged">
    /// True when the cell is a merged roster cell writing to several days at once; the message then also
    /// mentions expanding the block to edit the open days individually.
    /// </param>
    Task ExplainLockedCellAsync(int dayNumber = 0, bool merged = false);

    /// <summary>
    /// Checks that every day before the session's current one is already closed, and offers to close
    /// them. Called when a day's work starts (the finish read-out), because that is the moment a
    /// still-open earlier day is dangerous: a stray punch or an edit meant for today would silently
    /// land in a finished day.
    /// </summary>
    /// <returns>
    /// True to carry on (nothing to close, the user closed them, or chose to leave them open); false
    /// when the user cancelled and the caller must not start.
    /// </returns>
    Task<bool> EnsurePreviousDaysClosedAsync();

    /// <summary>
    /// Offers to close a day that is still open when the user runs an export that normally ends the day's
    /// work (the splits). Generating splits is the last step of a finished day, so a day still open at that
    /// point is usually one nobody meant to keep editable.
    ///
    /// The prompt never cancels the export — the user already asked for it, and declining only means
    /// "leave the day open" (an interim split sheet mid-day is a normal thing to want).
    /// </summary>
    /// <param name="day">The day being exported — not necessarily the session day (the splits page picks its own).</param>
    Task OfferCloseBeforeSplitsAsync(EventDay day);
}

public sealed class DayLockService : IDayLockService
{
    private readonly ISessionService _session;
    private readonly ICompetitionEditorService _editor;
    private readonly IDialogService _dialogs;
    private readonly ILocalizationService _localization;
    private readonly IActivityLog _log;

    public DayLockService(
        ISessionService session,
        ICompetitionEditorService editor,
        IDialogService dialogs,
        ILocalizationService localization,
        IActivityLog log)
    {
        _session = session;
        _editor = editor;
        _dialogs = dialogs;
        _localization = localization;
        _log = log;
    }

    public bool IsCurrentDayLocked => _session.IsCurrentDayLocked;

    public async Task<bool> ToggleCurrentDayAsync()
    {
        if (_session.CurrentDay is not { } day)
            return false;

        var unlocking = day.IsLocked;

        // Only dropping the protection asks; closing a day is safe and instantly reversible.
        if (unlocking)
        {
            var confirm = new ConfirmDialogViewModel(
                _localization,
                "DayLock.Confirm.Title",
                "DayLock.Confirm.Message",
                confirmKey: "DayLock.Confirm.Ok")
            {
                MessageArgs = [day.Number]
            };

            if (!await _dialogs.ConfirmAsync(confirm))
                return false;
        }

        var updated = await _editor.SetDayLockedAsync(day.Id, !day.IsLocked);
        if (updated is null)
            return false;

        _session.UpdateCurrentDay(updated);
        _log.Action(updated.IsLocked
            ? $"Day {updated.Number} closed for editing"
            : $"Day {updated.Number} opened for editing");
        return true;
    }

    public async Task<bool> EnsurePreviousDaysClosedAsync()
    {
        if (_session.CurrentDay is not { } current)
            return true;

        var days = await _editor.GetDaysAsync();
        var open = days
            .Where(d => d.Number < current.Number && !d.IsLocked)
            .OrderBy(d => d.Number)
            .ToList();
        if (open.Count == 0)
            return true;

        var numbers = string.Join(", ", open.Select(d => d.Number));
        var dialog = new ConfirmDialogViewModel(
            _localization,
            "DayLock.Previous.Title",
            open.Count == 1 ? "DayLock.Previous.Message.One" : "DayLock.Previous.Message.Many",
            confirmKey: "DayLock.Previous.Close",
            cancelKey: "Common.Cancel")
        {
            // "Leave open" is the third button: the operator may genuinely still be fixing yesterday,
            // so the check warns and steps aside rather than forcing the lock.
            AlternateKey = "DayLock.Previous.Skip",
            MessageArgs = [numbers, current.Number]
        };

        var choice = await _dialogs.ChooseAsync(dialog);
        if (choice == ConfirmDialogResult.Cancel)
            return false;

        if (choice == ConfirmDialogResult.Alternate)
        {
            _log.Action($"Left day(s) {numbers} open while starting day {current.Number}");
            return true;
        }

        foreach (var day in open)
        {
            if (await _editor.SetDayLockedAsync(day.Id, true) is not null)
                _log.Action($"Day {day.Number} closed for editing (before starting day {current.Number})");
        }

        return true;
    }

    public async Task OfferCloseBeforeSplitsAsync(EventDay day)
    {
        if (day.IsLocked)
            return;

        // Two buttons, both of which export: closing the day is the suggestion, not a gate. "Cancel" is
        // deliberately absent — the user already pressed Сформувати, so Esc / closing the dialog means
        // "leave the day open" rather than abandoning the export they asked for.
        var dialog = new ConfirmDialogViewModel(
            _localization,
            "DayLock.Splits.Title",
            "DayLock.Splits.Message",
            confirmKey: "DayLock.Splits.Close",
            cancelKey: "DayLock.Splits.Skip")
        {
            // Closing the day is the recommended answer and is instantly reversible — not a red button.
            IsDestructive = false,
            MessageArgs = [day.Number]
        };

        if (!await _dialogs.ConfirmAsync(dialog))
        {
            _log.Action($"Exported splits for day {day.Number} while leaving it open");
            return;
        }

        if (await _editor.SetDayLockedAsync(day.Id, true) is { } updated)
        {
            day.IsLocked = true;
            _session.UpdateCurrentDay(updated);
            _log.Action($"Day {updated.Number} closed for editing (before exporting splits)");
        }
    }

    public Task ExplainLockedCellAsync(int dayNumber = 0, bool merged = false)
    {
        // A cell that knows its own day wins: in the roster the clicked column is often not the session
        // day. Falling back to the session day covers the per-day grids, which are locked as a whole.
        var day = dayNumber > 0 ? dayNumber : (_session.CurrentDay is { IsLocked: true } d ? d.Number : 0);
        if (day == 0)
            return Task.CompletedTask;

        var dialog = new ConfirmDialogViewModel(
            _localization,
            "DayLock.Explain.Title",
            merged ? "DayLock.Explain.Merged" : "DayLock.Explain.Message",
            confirmKey: "Common.Ok")
        {
            IsMessage = true,
            MessageArgs = [day]
        };

        return _dialogs.ConfirmAsync(dialog);
    }

    public Task ShowBlockedAsync(int dayNumber)
    {
        var dialog = new ConfirmDialogViewModel(
            _localization,
            "DayLock.Blocked.Title",
            "DayLock.Blocked.Message",
            confirmKey: "Common.Ok")
        {
            IsMessage = true,
            MessageArgs = [dayNumber]
        };

        return _dialogs.ConfirmAsync(dialog);
    }
}
