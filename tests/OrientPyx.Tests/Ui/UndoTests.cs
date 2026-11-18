using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.Services.Undo;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;
using Xunit.Abstractions;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// Ctrl+Z / Ctrl+Y on «Учасники» through the real app (undo plan §11, «Ui/UndoTests»): a cell edit is undone in the
/// table and in <c>event.db</c> and an edit right after it does not write the undone value back; a paste is one step;
/// the keys leave a text box's own undo alone and close a live combo's edit first; several presses while one runs
/// undo one step; every day table and «Мандатка» has its own history, and an edit made just before a day switch lands
/// on its own day; each «Швидке зняття» is a step of its own; the «Правка» menu acts on the same steps; and every
/// editable column of the day table makes a step (a forgotten write point fails here).
/// </summary>
[Collection(AppCollection.Name)]
public sealed class UndoTests : IAsyncLifetime
{
    // Undo / redo toasts stay up for 15 s: cleared before and after each test, so a stack of them never covers the
    // table of the next test in the shared headless window.
    public Task InitializeAsync() => ClearToastsAsync();

    public Task DisposeAsync() => ClearToastsAsync();

    private Task ClearToastsAsync() => _app.OnUiAsync(() => _app.Vm.Toasts.Items.Clear());

    private const string CoachKey = "id:Participants.Col.Coach";
    private const string GroupKey = "id:Participants.Col.Group";

    private readonly HeadlessApp _app;
    private readonly ITestOutputHelper _output;

    public UndoTests(HeadlessApp app, ITestOutputHelper output)
    {
        _app = app;
        _output = output;
    }

    private static IServiceProvider Core => TestEnvironment.Instance.Services;

    private static IEventStore Store => Core.GetRequiredService<IEventStore>();

    [Fact]
    public async Task TypedCellEditIsUndoneInTableAndStore_RedoneByCtrlY_AndAnEditRightAfterKeepsItUndone()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_undo_cell"), dayCount: 2);
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("Бігун А", "1", "", "Ч21", configure: p => p.Coach = "Тренер А");
        await c.AddRunnerAsync("Бігун Б", "2", "", "Ч21");
        var (page, table) = await OpenDayAsync(c, 1);
        var a = c.Runners["Бігун А"].Id;

        // Type a new coach into the cell (Backspace opens it empty), Enter commits.
        await ClickAsync(table, Row(page, "Бігун А"), CoachKey);
        await KeyAsync(Key.Escape, PhysicalKey.Escape);
        await KeyAsync(Key.Back, PhysicalKey.Backspace);
        await _app.OnUiAsync(() => _app.Window.KeyTextInput("Новий"));
        await KeyAsync(Key.Enter, PhysicalKey.Enter);
        await _app.OnUiAsync(() => page.FlushPendingWritesAsync());
        Assert.Equal("Новий", await CoachAsync(c, a));
        var label = await UndoHeaderAsync();
        Assert.Contains("Тренер", label);
        Assert.Contains("Бігун А", label);

        // Ctrl+Z: the value is back in the row and in the database.
        await CtrlAsync(Key.Z, PhysicalKey.Z, "z");
        await WaitUndoIdleAsync(page);
        Assert.Equal("Тренер А", await _app.OnUiAsync(() => Row(page, "Бігун А").Coach));
        Assert.Equal("Тренер А", await CoachAsync(c, a));

        // Ctrl+Y puts it back; Ctrl+Z again.
        await CtrlAsync(Key.Y, PhysicalKey.Y, "y");
        await WaitUndoIdleAsync(page);
        Assert.Equal("Новий", await _app.OnUiAsync(() => Row(page, "Бігун А").Coach));
        Assert.Equal("Новий", await CoachAsync(c, a));
        await CtrlAsync(Key.Z, PhysicalKey.Z, "z");
        await WaitUndoIdleAsync(page);
        Assert.Equal("Тренер А", await CoachAsync(c, a));

        // Straight after the undo another cell of the same row is edited: the whole row is written, and it must carry
        // the undone value, not the one the row held before the undo (§9.5).
        await RestFocusAsync();
        await _app.OnUiAsync(() => Row(page, "Бігун А").Note = "примітка");
        await _app.OnUiAsync(() => page.FlushPendingWritesAsync());
        var stored = (await Store.GetParticipantsAsync(c.Folder)).Single(p => p.Id == a);
        Assert.Equal("примітка", stored.Note);
        Assert.Equal("Тренер А", stored.Coach);
        Assert.Equal((1, 0), await DepthAsync(page));   // the note's step; the redo is gone (У-5)
        AssertNoUiErrors();
    }

    [Fact]
    public async Task PasteOverFiveRowsIsOneStep()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_undo_paste"), dayCount: 2);
        await c.AddGroupAsync("Ч21", "31");
        for (var i = 1; i <= 5; i++)
            await c.AddRunnerAsync($"Бігун {i}", i.ToString(), "", "Ч21", configure: p => p.Coach = "старий");
        var (page, table) = await OpenDayAsync(c, 1);

        await _app.OnUiAsync(() => _app.Window.Clipboard!.SetTextAsync("т1\nт2\nт3\nт4\nт5"));
        await ClickAsync(table, table.VisibleItems[0]!, CoachKey);
        await KeyAsync(Key.Escape, PhysicalKey.Escape);
        await _app.OnUiAsync(() => _app.Window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v"));
        await _app.SettleAsync();
        await _app.OnUiAsync(() => page.FlushPendingWritesAsync());
        await WaitForAsync(async () => (await Store.GetParticipantsAsync(c.Folder)).Count(p => p.Coach.StartsWith('т')) == 5,
            "five coaches pasted");
        Assert.Equal((1, 0), await DepthAsync(page));
        Assert.Contains("5", await UndoHeaderAsync());

        await CtrlAsync(Key.Z, PhysicalKey.Z, "z");
        await WaitUndoIdleAsync(page);
        Assert.All(await Store.GetParticipantsAsync(c.Folder), p => Assert.Equal("старий", p.Coach));
        Assert.All(await _app.OnUiAsync(() => page.Participants.Select(r => r.Coach).ToList()), v => Assert.Equal("старий", v));
        Assert.Equal((0, 1), await DepthAsync(page));
        AssertNoUiErrors();
    }

    [Fact]
    public async Task CtrlZInATextBoxIsTheBoxsOwn_AndALiveComboIsClosedThenUndone()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_undo_live"), dayCount: 2);
        await c.AddGroupAsync("Ч21", "31");
        await c.AddGroupAsync("Ж21", "31");
        await c.AddRunnerAsync("Бігун А", "1", "", "Ч21", configure: p => p.Coach = "Тренер");
        var (page, table) = await OpenDayAsync(c, 1);
        var window = _app.Window;
        var oldWidth = await _app.OnUiAsync(() => window.Width);
        await _app.OnUiAsync(() => window.Width = 3600);
        await _app.SettleAsync();
        try
        {
            // Typing in a text cell, then Ctrl+Z while its box has the focus: the box undoes its own typing; the page's
            // history is left as it was (the typing is one step, the session's — no undo ran).
            await ClickAsync(table, Row(page, "Бігун А"), CoachKey);
            await KeyAsync(Key.F2, PhysicalKey.F2);
            await KeyAsync(Key.End, PhysicalKey.End);
            await _app.OnUiAsync(() => window.KeyTextInput("X"));
            await _app.SettleAsync();
            Assert.True(await _app.OnUiAsync(() => window.FocusManager?.GetFocusedElement() is TextBox), "the coach editor has the focus");
            var before = await DepthAsync(page);
            await _app.OnUiAsync(() => window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z"));
            await _app.SettleAsync();
            Assert.Equal(before, await DepthAsync(page));
            Assert.Equal(0, (await DepthAsync(page)).Redo);
            await KeyAsync(Key.Escape, PhysicalKey.Escape);          // drop the edit
            await _app.OnUiAsync(() => page.FlushPendingWritesAsync());
            await _app.OnUiAsync(() => History(page).Clear());

            // A live group combo: a choice, then Ctrl+Z with the combo still holding the focus — the edit is closed and
            // the choice undone in one go.
            var link = (await c.GetLinkAsync("Бігун А", 1)).Id;
            var men = c.Groups["Ч21"].Id;
            var women = c.Groups["Ж21"].Id;
            await PickGroupAsync(table, page, "Бігун А", "Ж21");
            Assert.Equal(women, await GroupOfAsync(c, 1, link));
            Assert.Equal((1, 0), await DepthAsync(page));
            await _app.OnUiAsync(() => window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z"));
            await WaitUndoIdleAsync(page);
            Assert.Equal(men, await GroupOfAsync(c, 1, link));
            Assert.Equal("Ч21", await _app.OnUiAsync(() => Row(page, "Бігун А").SelectedGroup.Label));
            Assert.Equal((0, 1), await DepthAsync(page));

            // The next choice in the same combo is a step of its own (not glued to the undone one).
            await PickGroupAsync(table, page, "Бігун А", "Ж21");
            Assert.Equal(women, await GroupOfAsync(c, 1, link));
            Assert.Equal((1, 0), await DepthAsync(page));
            await _app.OnUiAsync(() => window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z"));
            await WaitUndoIdleAsync(page);
            Assert.Equal(men, await GroupOfAsync(c, 1, link));
        }
        finally
        {
            await _app.OnUiAsync(() => window.Width = oldWidth);
        }
        AssertNoUiErrors();
    }

    [Fact]
    public async Task PressesWhileAnUndoRunsAreIgnored()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_undo_burst"), dayCount: 2);
        await c.AddGroupAsync("Ч21", "31");
        foreach (var name in new[] { "А", "Б", "В" })
            await c.AddRunnerAsync(name, "", "", "Ч21", configure: p => p.Coach = "0");
        var (page, _) = await OpenDayAsync(c, 1);

        // Three steps; the last one's write still waits for its debounce, so the first Ctrl+Z below holds the undo for
        // 0.6 s while it lets that write land (У-6).
        foreach (var name in new[] { "А", "Б" })
        {
            await _app.OnUiAsync(() => Row(page, name).Coach = "1");
            await _app.OnUiAsync(() => page.FlushPendingWritesAsync());
        }
        await _app.OnUiAsync(() => Row(page, "В").Coach = "1");
        Assert.Equal((3, 0), await DepthAsync(page));

        // Three Ctrl+Z at once (a held key's auto-repeat): the first one runs, the other two are dropped (У-18).
        await _app.OnUiAsync(() =>
        {
            if (_app.Window.FocusManager?.GetFocusedElement() is TextBox)
                _app.Window.Focus();
            for (var i = 0; i < 3; i++)
                _app.Window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        });
        await WaitUndoIdleAsync(page);
        Assert.Equal((2, 1), await DepthAsync(page));
        var coaches = (await Store.GetParticipantsAsync(c.Folder)).ToDictionary(p => p.FullName, p => p.Coach);
        Assert.Equal(("1", "1", "0"), (coaches["А"], coaches["Б"], coaches["В"]));
        AssertNoUiErrors();
    }

    [Fact]
    public async Task EveryDayAndTheRosterHaveTheirOwnHistory()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_undo_days"), dayCount: 2);
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("Бігун А", "1", "", "Ч21", configure: p => p.Coach = "Тренер");
        var (page, _) = await OpenDayAsync(c, 1);
        var a = c.Runners["Бігун А"].Id;

        await _app.OnUiAsync(() => Row(page, "Бігун А").Coach = "День 1");
        await _app.OnUiAsync(() => page.FlushPendingWritesAsync());
        Assert.True(await CanUndoAsync());

        // Day 2: nothing to undo there (the edit is day 1's).
        await SelectDayAsync(c, page, 2);
        Assert.False(await CanUndoAsync());
        await CtrlAsync(Key.Z, PhysicalKey.Z, "z");
        await WaitUndoIdleAsync(page);
        Assert.Equal("День 1", await CoachAsync(c, a));

        // «Мандатка» is separate from both.
        await SelectDayAsync(c, page, null);
        Assert.False(await CanUndoAsync());
        await _app.OnUiAsync(() => page.Roster.Single(r => r.ParticipantId == a).Note = "з мандатки");
        await _app.OnUiAsync(() => page.FlushPendingWritesAsync());
        Assert.True(await CanUndoAsync());

        // Back on day 1 the edit is undone; the roster's note is not touched.
        await SelectDayAsync(c, page, 1);
        Assert.Contains("Тренер", await UndoHeaderAsync());
        await CtrlAsync(Key.Z, PhysicalKey.Z, "z");
        await WaitUndoIdleAsync(page);
        var stored = (await Store.GetParticipantsAsync(c.Folder)).Single(p => p.Id == a);
        Assert.Equal(("Тренер", "з мандатки"), (stored.Coach, stored.Note));

        // And the roster's own step is still there.
        await SelectDayAsync(c, page, null);
        Assert.True(await CanUndoAsync());
        await CtrlAsync(Key.Z, PhysicalKey.Z, "z");
        await WaitUndoIdleAsync(page);
        Assert.Equal(string.Empty, (await Store.GetParticipantsAsync(c.Folder)).Single(p => p.Id == a).Note);
        AssertNoUiErrors();
    }

    [Fact]
    public async Task AnEditJustBeforeADaySwitchLandsOnItsDayAndInItsHistory()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_undo_switch"), dayCount: 2);
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("Бігун А", "1", "", "Ч21");
        var (page, _) = await OpenDayAsync(c, 1);

        // «Поза конкурсом» (a day field, saved 0.6 s later) and the day switch in the same moment.
        await _app.OnUiAsync(() =>
        {
            Row(page, "Бігун А").OutOfCompetition = true;
            page.SelectedDay = page.DayOptions.First(o => !o.IsRoster && o.Number == 2);
        });
        await WaitDayShownAsync(c, page, 2);
        Assert.True((await c.GetLinkAsync("Бігун А", 1)).OutOfCompetition, "written to day 1");
        Assert.False((await c.GetLinkAsync("Бігун А", 2)).OutOfCompetition, "day 2 untouched");
        Assert.False(await CanUndoAsync());

        await SelectDayAsync(c, page, 1);
        Assert.Contains("Бігун А", await UndoHeaderAsync());
        await CtrlAsync(Key.Z, PhysicalKey.Z, "z");
        await WaitUndoIdleAsync(page);
        Assert.False((await c.GetLinkAsync("Бігун А", 1)).OutOfCompetition);
        Assert.False(await _app.OnUiAsync(() => Row(page, "Бігун А").OutOfCompetition));
        AssertNoUiErrors();
    }

    [Fact]
    public async Task EachQuickWithdrawalIsAStepOfItsOwn()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_undo_qw"), dayCount: 2);
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("Бігун А", "1", "", "Ч21");
        await c.AddRunnerAsync("Бігун Б", "2", "", "Ч21");
        var (page, _) = await OpenDayAsync(c, 1);

        _ = _app.OnUiAsync(() => page.QuickWithdrawalCommand.ExecuteAsync(null));
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is QuickWithdrawalViewModel, what: "quick withdrawal open");
        await _app.SettleAsync();
        await _app.OnUiAsync(() =>
        {
            var dialog = (QuickWithdrawalViewModel)_app.Vm.Dialogs.Current!;
            var first = dialog.Rows[^1];
            first.Number = "1";
            first.SelectedStatus = first.Statuses.First(s => s.Status == FinishStatus.Dns);
            var second = dialog.Rows[^1];
            second.Number = "2";
            second.SelectedStatus = second.Statuses.First(s => s.Status == FinishStatus.Dnf);
            dialog.ConfirmCommand.Execute(null);
        });
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is null, what: "dialog closed");
        await WaitForAsync(async () => (await c.GetLinkAsync("Бігун Б", 1)).ResultStatusOverride == FinishStatus.Dnf, "both saved");
        await _app.SettleAsync();
        Assert.Equal(FinishStatus.Dns, (await c.GetLinkAsync("Бігун А", 1)).ResultStatusOverride);
        Assert.Equal((2, 0), await DepthAsync(page));
        Assert.Contains("DNF", await UndoHeaderAsync());

        await CtrlAsync(Key.Z, PhysicalKey.Z, "z");
        await WaitUndoIdleAsync(page);
        Assert.Null((await c.GetLinkAsync("Бігун Б", 1)).ResultStatusOverride);
        Assert.Equal(FinishStatus.Dns, (await c.GetLinkAsync("Бігун А", 1)).ResultStatusOverride);
        await CtrlAsync(Key.Z, PhysicalKey.Z, "z");
        await WaitUndoIdleAsync(page);
        Assert.Null((await c.GetLinkAsync("Бігун А", 1)).ResultStatusOverride);
        Assert.Equal((0, 2), await DepthAsync(page));
        AssertNoUiErrors();
    }

    [Fact]
    public async Task EditMenuShowsTheStepIsOffWithoutOneAndUndoesTheSameStep()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_undo_menu"), dayCount: 2);
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("Бігун А", "1", "", "Ч21", configure: p => p.Coach = "Тренер");
        var (page, _) = await OpenDayAsync(c, 1);
        var a = c.Runners["Бігун А"].Id;
        var loc = await _app.OnUiAsync(() => page.Localization);

        // Nothing yet: the plain item, off.
        Assert.False(await CanUndoAsync());
        Assert.Equal(loc.Get("Menu.Edit.Undo"), await UndoHeaderAsync());

        await _app.OnUiAsync(() => Row(page, "Бігун А").Coach = "Інший");
        await _app.OnUiAsync(() => page.FlushPendingWritesAsync());
        var header = await UndoHeaderAsync();
        Assert.Equal(string.Format(loc.Get("Menu.Edit.UndoWithLabel"),
            string.Format(loc.Get("Undo.Label.Cell"), loc.Get("Participants.Col.Coach"), "Бігун А")), header);

        // «Правка → Скасувати» undoes the step Ctrl+Z would; then «Повторити: …» names it.
        await _app.OnUiAsync(() => _app.Vm.EditMenu.UndoCommand.ExecuteAsync(null));
        await WaitUndoIdleAsync(page);
        Assert.Equal("Тренер", await CoachAsync(c, a));
        Assert.False(await CanUndoAsync());
        Assert.Contains("Бігун А", await _app.OnUiAsync(() => { _app.Vm.EditMenu.Refresh(); return _app.Vm.EditMenu.RedoHeader; }));
        await _app.OnUiAsync(() => _app.Vm.EditMenu.RedoCommand.ExecuteAsync(null));
        await WaitUndoIdleAsync(page);
        Assert.Equal("Інший", await CoachAsync(c, a));
        AssertNoUiErrors();
    }

    [Fact]
    public async Task DeletedAndAddedRowsAreUndone()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_undo_rows"), dayCount: 2);
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("Бігун А", "1", "", "Ч21");
        await c.AddRunnerAsync("Бігун Б", "2", "", "Ч21");
        var (page, _) = await OpenDayAsync(c, 1);
        var a = c.Runners["Бігун А"].Id;

        // A day row deleted (it runs day 2 too, so only the day link goes): Ctrl+Z brings the row back.
        await _app.OnUiAsync(() => page.DeleteParticipantNoConfirmAsync(Row(page, "Бігун А")));
        await _app.OnUiAsync(() => page.FlushPendingWritesAsync());
        Assert.DoesNotContain(await Store.GetParticipantDaysAsync(c.Folder, c.Day(1).Id), l => l.ParticipantId == a);
        await CtrlAsync(Key.Z, PhysicalKey.Z, "z");
        await WaitUndoIdleAsync(page);
        Assert.Contains(await Store.GetParticipantDaysAsync(c.Folder, c.Day(1).Id), l => l.ParticipantId == a);
        Assert.True(await _app.OnUiAsync(() => page.Participants.Any(r => r.ParticipantId == a)), "the row is back");

        // «Мандатка»: a participant deleted with every day, then a new one added — two steps, undone in turn.
        await SelectDayAsync(c, page, null);
        await _app.OnUiAsync(() => page.DeleteRosterParticipantNoConfirmAsync(page.Roster.Single(r => r.ParticipantId == a)));
        await _app.OnUiAsync(() => page.FlushPendingWritesAsync());
        Assert.DoesNotContain(await Store.GetParticipantsAsync(c.Folder), p => p.Id == a);
        await _app.OnUiAsync(() => page.AddParticipantCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        Assert.Equal(2, (await Store.GetParticipantsAsync(c.Folder)).Count);
        Assert.Equal((2, 0), await DepthAsync(page));

        await CtrlAsync(Key.Z, PhysicalKey.Z, "z");
        await WaitUndoIdleAsync(page);
        Assert.Single(await Store.GetParticipantsAsync(c.Folder));
        await CtrlAsync(Key.Z, PhysicalKey.Z, "z");
        await WaitUndoIdleAsync(page);
        Assert.Contains(await Store.GetParticipantsAsync(c.Folder), p => p.Id == a);
        Assert.True(await _app.OnUiAsync(() => page.Roster.Any(r => r.ParticipantId == a)), "the roster row is back");
        Assert.Contains(await Store.GetParticipantDaysAsync(c.Folder, c.Day(1).Id), l => l.ParticipantId == a);
        Assert.Contains(await Store.GetParticipantDaysAsync(c.Folder, c.Day(2).Id), l => l.ParticipantId == a);
        AssertNoUiErrors();
    }

    // A bulk edit is one step per applied dialog — with the region it made through «+ новий» in the dialog.
    [Fact]
    public async Task BulkEditIsOneStepWithTheRegionItMade()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_undo_bulk"), dayCount: 2);
        await c.AddGroupAsync("Ч21", "31");
        for (var i = 1; i <= 3; i++)
            await c.AddRunnerAsync($"Бігун {i}", i.ToString(), "", "Ч21", configure: p => p.Coach = "старий");
        var (page, _) = await OpenDayAsync(c, 1);
        IReadOnlyList<object?> Rows() => page.Participants.Cast<object?>().ToList();

        // «Тренер» for the three shown rows: one step.
        _ = _app.OnUiAsync(() => page.BulkEditCommand.ExecuteAsync(new BulkEditRequest(Rows(), "Coach")));
        var dialog = await BulkDialogAsync();
        await _app.OnUiAsync(() =>
        {
            dialog.TextValue = "новий";
            dialog.ConfirmCommand.Execute(null);
        });
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is null, what: "bulk edit closed");
        await _app.OnUiAsync(() => page.FlushPendingWritesAsync());
        Assert.All(await Store.GetParticipantsAsync(c.Folder), p => Assert.Equal("новий", p.Coach));
        Assert.Equal((1, 0), await DepthAsync(page));
        Assert.Contains("3", await UndoHeaderAsync());
        await CtrlAsync(Key.Z, PhysicalKey.Z, "z");
        await WaitUndoIdleAsync(page);
        Assert.All(await Store.GetParticipantsAsync(c.Folder), p => Assert.Equal("старий", p.Coach));

        // «Регіон» → «+ новий» in the dialog: the new region and the rows pointing at it are one step.
        _ = _app.OnUiAsync(() => page.BulkEditCommand.ExecuteAsync(new BulkEditRequest(Rows(), "Region")));
        dialog = await BulkDialogAsync();
        await _app.OnUiAsync(() => dialog.SelectedRegion = dialog.RegionOptions.First(o => o.IsAdd));
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is AddRegionViewModel, what: "add-region dialog");
        await _app.OnUiAsync(() =>
        {
            var add = (AddRegionViewModel)_app.Vm.Dialogs.Current!;
            add.Name = "Полтава";
            add.ConfirmCommand.Execute(null);
        });
        // (The dialog service does not show the bulk edit again after the nested dialog — its result is still awaited.)
        await _app.WaitUntilAsync(() => dialog.SelectedRegion is { IsAdd: false, Id: not null },
            what: "the new region picked in the bulk edit");
        await _app.OnUiAsync(() => dialog.ConfirmCommand.Execute(null));
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is null, what: "bulk edit closed");
        await _app.OnUiAsync(() => page.FlushPendingWritesAsync());
        var region = Assert.Single(await Store.GetRegionsAsync(c.Folder));
        Assert.All(await Store.GetParticipantsAsync(c.Folder), p => Assert.Equal(region.Id, p.RegionId));
        Assert.Equal((1, 0), await DepthAsync(page));

        await CtrlAsync(Key.Z, PhysicalKey.Z, "z");
        await WaitUndoIdleAsync(page);
        Assert.Empty(await Store.GetRegionsAsync(c.Folder));
        Assert.All(await Store.GetParticipantsAsync(c.Folder), p => Assert.Null(p.RegionId));
        AssertNoUiErrors();
    }

    private async Task<BulkEditViewModel> BulkDialogAsync()
    {
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is BulkEditViewModel, what: "bulk-edit dialog shown");
        await _app.SettleAsync();
        return await _app.OnUiAsync(() => (BulkEditViewModel)_app.Vm.Dialogs.Current!);
    }

    // Every editable column of the day table: an edit makes a step (CanUndo), and undoing it puts the value back. A
    // column with no entry here fails the test — add its edit, and its write point to the page's history.
    [Fact]
    public async Task EveryEditableDayColumnMakesAnUndoableStep()
    {
        var (c, discount) = await CoverageCompetitionAsync("ui_undo_cols", paymentPerDay: false);
        var (page, _) = await OpenDayAsync(c, 1);

        // By id: the «ПІБ» edit renames the row for a moment.
        ParticipantDayRowViewModel ById(string name) => page.Participants.Single(r => r.ParticipantId == c.Runners[name].Id);
        ParticipantDayRowViewModel A() => ById("Бігун А");
        // column key → (row, read the value, make the edit); null = read-only (no step expected).
        var edits = new Dictionary<string, (Func<ParticipantDayRowViewModel> Row, Func<ParticipantDayRowViewModel, object?> Get,
            Action<ParticipantDayRowViewModel> Set)?>
        {
            ["id:Participants.Col.Number"] = (A, r => r.Number, r => r.Number = "99"),
            ["id:Participants.Col.FullName"] = (A, r => r.FullName, r => r.FullName = "Бігун Інший"),
            ["id:Participants.Col.Rank"] = (A, r => r.Rank, r => r.Rank = "КМС"),
            ["id:Participants.Col.Coach"] = (A, r => r.Coach, r => r.Coach = "Тренер"),
            ["id:Participants.Col.BirthDate"] = (A, r => r.BirthDate, r => r.BirthDate = new DateTimeOffset(2001, 2, 3, 0, 0, 0, CompetitionBuilder.Offset)),
            ["id:Participants.Col.Region"] = (A, r => r.SelectedRegion.Id, r => r.SelectedRegion = r.RegionOptions.First(o => !o.IsAdd && o.Id is not null)),
            ["id:Participants.Col.Club"] = (A, r => r.SelectedClub.Id, r => r.SelectedClub = r.ClubOptions.First(o => !o.IsAdd && o.Id is not null)),
            ["id:Participants.Col.Dussh"] = (A, r => r.SelectedDussh.Id, r => r.SelectedDussh = r.DusshOptions.First(o => !o.IsAdd && o.Id is not null)),
            ["id:Participants.Col.Representative"] = (A, r => r.Representative, r => r.Representative = "Представник"),
            ["id:Participants.Col.FsouCode"] = (A, r => r.FsouCode, r => r.FsouCode = "F-1"),
            ["id:Participants.Col.IsFsouMember"] = (A, r => r.IsFsouMember, r => r.IsFsouMember = !r.IsFsouMember),
            ["id:Participants.Col.Payment"] = (A, r => r.Payment, r => r.Payment = "100"),
            ["id:Participants.Col.Note"] = (A, r => r.Note, r => r.Note = "примітка"),
            ["id:Participants.Col.Group"] = (A, r => r.SelectedGroup.Id, r => r.ComboGroup = r.GroupOptions.First(o => o.Label == "Ж21")),
            ["id:Participants.Col.RelayBib"] = null,
            ["id:Participants.Col.RelayTeamName"] = (() => ById("Естафета 1"), r => r.Relay.TeamName, r => r.Relay.TeamName = "Нова"),
            ["id:Participants.Col.RelayTeam"] = (() => ById("Естафета 2"), r => r.Relay.TeamText, r => r.Relay.TeamText = "501"),
            ["id:Participants.Col.RelayLeg"] = (() => ById("Естафета 3"), r => r.Relay.LegText, r => r.Relay.LegText = "2"),
            ["id:Participants.Col.Chip"] = (A, r => r.Chip, r => r.Chip = "777"),
            ["id:Participants.Col.StartTime"] = (A, r => r.StartTime, r => r.StartTimeText = "10:00:00"),
            ["id:Participants.Col.OutOfCompetition"] = (A, r => r.OutOfCompetition, r => r.OutOfCompetition = !r.OutOfCompetition),
            ["id:Participants.Col.Team"] = (A, r => r.Team, r => r.Team = "Команда"),
            ["id:Participants.Col.ResultStatus"] = (A, r => r.ResultStatusOverride, r => r.SelectedStatus = r.StatusOptions.First(o => o.Status == FinishStatus.Dnf)),
            ["id:Participants.Col.Bonus"] = (A, r => r.Bonus, r => r.BonusText = "5"),
            ["fee:raised"] = (A, r => r.PaysRaisedFee, r => r.PaysRaisedFee = !r.PaysRaisedFee),
            [$"fee:discount:{discount.Id}"] = (A, r => r.DiscountFlags.Single(f => f.DiscountId == discount.Id).IsSelected,
                r => r.DiscountFlags.Single(f => f.DiscountId == discount.Id).IsSelected = true),
            ["field.klas"] = (A, r => r.Fields["klas"].Value, r => r.Fields["klas"].Text = "10-А"),
            ["field.stat"] = (A, r => r.Fields["stat"].Value, r => r.Fields["stat"].Selected = r.Fields["stat"].Options.First(o => o.Value == "Ж")),
            ["field.flag"] = (A, r => r.Fields["flag"].Value, r => r.Fields["flag"].Checked = true),
        };

        var columns = await _app.OnUiAsync(() => page.DayBands.SelectMany(b => b.Columns).ToList());
        var editable = columns.Where(col => col.Kind is not (SheetCellKind.RowResultText or SheetCellKind.TotalFee or SheetCellKind.Actions)).ToList();
        var missing = editable.Where(col => !edits.ContainsKey(col.Key)).Select(col => $"{col.Key} ({col.Kind})").ToList();
        Assert.True(missing.Count == 0, "day columns with no undo coverage: " + string.Join(", ", missing));
        foreach (var expected in new[] { "id:Participants.Col.Team", "id:Participants.Col.Bonus", "id:Participants.Col.RelayLeg", "fee:raised", "field.flag" })
            Assert.Contains(editable, col => col.Key == expected);   // the set-up shows what it means to

        foreach (var column in editable)
        {
            if (edits[column.Key] is not { } edit)
                continue;
            await _app.OnUiAsync(() => History(page).Clear());
            var before = await _app.OnUiAsync(() => edit.Get(edit.Row()));
            await _app.OnUiAsync(() => edit.Set(edit.Row()));
            await _app.OnUiAsync(() => page.FlushPendingWritesAsync());
            await _app.SettleAsync();
            Assert.True(await CanUndoAsync(), $"{column.Key}: the edit made no undo step");
            Assert.NotEqual(before, await _app.OnUiAsync(() => edit.Get(edit.Row())));
            _output.WriteLine($"{column.Key}: {await UndoHeaderAsync()}");

            await _app.OnUiAsync(() => _app.Vm.EditMenu.UndoCommand.ExecuteAsync(null));
            await WaitUndoIdleAsync(page);
            await _app.WaitUntilAsync(() => Equals(edit.Get(edit.Row()), before), TimeSpan.FromSeconds(10),
                what: $"{column.Key} undone (was {before})");
        }
        AssertNoUiErrors();
    }

    // The same for «Мандатка» — its identity columns, every block collapsed (the merged cells fan out to every day) and
    // expanded (a column per day), in per-day payment mode so the «Оплата» / «Підвищений внесок» blocks are there too.
    [Fact]
    public async Task EveryEditableRosterColumnMakesAnUndoableStep()
    {
        var (c, discount) = await CoverageCompetitionAsync("ui_undo_rcols", paymentPerDay: true);
        await c.AddRelayTeamAsync("R", 302, "Стара 2", 2, ("Естафета 3", 1));
        var (page, _) = await OpenDayAsync(c, 1);
        await SelectDayAsync(c, page, null);
        var table = await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == "RosterTable"));

        ParticipantRosterRowViewModel ById(string name) => page.Roster.Single(r => r.ParticipantId == c.Runners[name].Id);
        ParticipantRosterRowViewModel A() => ById("Бігун А");
        GroupOption Women(RosterDayCellViewModel d) => d.GroupOptions.First(o => o.Label == "Ж21");
        var identity = new Dictionary<string, RosterEdit?>
        {
            ["id:Participants.Col.Number"] = new(A, r => r.Number, r => r.Number = "99"),
            ["id:Participants.Col.FullName"] = new(A, r => r.FullName, r => r.FullName = "Бігун Інший"),
            ["id:Participants.Col.Rank"] = new(A, r => r.Rank, r => r.Rank = "КМС"),
            ["id:Participants.Col.Coach"] = new(A, r => r.Coach, r => r.Coach = "Тренер"),
            ["id:Participants.Col.BirthDate"] = new(A, r => r.BirthDate, r => r.BirthDate = new DateTimeOffset(2001, 2, 3, 0, 0, 0, CompetitionBuilder.Offset)),
            ["id:Participants.Col.Region"] = new(A, r => r.SelectedRegion.Id, r => r.SelectedRegion = r.RegionOptions.First(o => !o.IsAdd && o.Id is not null)),
            ["id:Participants.Col.Club"] = new(A, r => r.SelectedClub.Id, r => r.SelectedClub = r.ClubOptions.First(o => !o.IsAdd && o.Id is not null)),
            ["id:Participants.Col.Dussh"] = new(A, r => r.SelectedDussh.Id, r => r.SelectedDussh = r.DusshOptions.First(o => !o.IsAdd && o.Id is not null)),
            ["id:Participants.Col.Representative"] = new(A, r => r.Representative, r => r.Representative = "Представник"),
            ["id:Participants.Col.FsouCode"] = new(A, r => r.FsouCode, r => r.FsouCode = "F-1"),
            ["id:Participants.Col.IsFsouMember"] = new(A, r => r.IsFsouMember, r => r.IsFsouMember = !r.IsFsouMember),
            ["id:Participants.Col.Payment"] = new(A, r => r.Payment, r => r.Payment = "100"),
            ["id:Participants.Col.Note"] = new(A, r => r.Note, r => r.Note = "примітка"),
            ["id:Participants.Col.Team"] = new(A, r => r.Team, r => r.Team = "Команда"),
            ["fee:raised"] = new(A, r => r.PaysRaisedFee, r => r.PaysRaisedFee = !r.PaysRaisedFee),
            [$"fee:discount:{discount.Id}"] = new(A, r => r.DiscountFlags.Single(f => f.DiscountId == discount.Id).IsSelected,
                r => r.DiscountFlags.Single(f => f.DiscountId == discount.Id).IsSelected = true),
            ["field.klas"] = new(A, r => r.Fields["klas"].Value, r => r.Fields["klas"].Text = "10-А"),
            ["field.stat"] = new(A, r => r.Fields["stat"].Value, r => r.Fields["stat"].Selected = r.Fields["stat"].Options.First(o => o.Value == "Ж")),
            ["field.flag"] = new(A, r => r.Fields["flag"].Value, r => r.Fields["flag"].Checked = true),
            // Collapsed blocks: the merged cell writes every (member) day — one step.
            ["block:Groups"] = new(A, r => string.Join(",", r.Days.Select(d => d.SelectedGroup.Id)), r => r.CollapsedGroupValue = Women(r.Days[0])),
            ["block:Chips"] = new(A, r => r.CollapsedChipValue, r => r.CollapsedChipValue = "777"),
            ["block:Payment"] = new(A, r => r.CollapsedPaymentValue, r => r.CollapsedPaymentValue = "100"),
            ["block:RaisedFee"] = new(A, r => r.CollapsedRaisedFee, r => r.CollapsedRaisedFee = !(r.CollapsedRaisedFee ?? false)),
            ["block:OutOfCompetition"] = new(A, r => r.CollapsedOutOfCompetition, r => r.CollapsedOutOfCompetition = !(r.CollapsedOutOfCompetition ?? false)),
            ["block:StartTimes"] = null,                                     // the merged start time is read-only
            ["block:ResultStatus"] = null,                                   // so is the merged status
        };
        // An expanded block's column of one day: «block:<field>:day<N>».
        RosterEdit? DayEdit(string field, int i) => field switch
        {
            "Groups" => new(A, r => r.Days[i].SelectedGroup.Id, r => r.Days[i].ComboGroup = Women(r.Days[i])),
            "Chips" => new(A, r => r.Days[i].Chip, r => r.Days[i].Chip = $"77{i}"),
            "Payment" => new(A, r => r.Days[i].Payment, r => r.Days[i].Payment = "50"),
            "RaisedFee" => new(A, r => r.Days[i].PaysRaisedFee, r => r.Days[i].PaysRaisedFee = !r.Days[i].PaysRaisedFee),
            "StartTimes" => new(A, r => r.Days[i].StartTime, r => r.Days[i].StartTimeText = "10:00:00"),
            "OutOfCompetition" => new(A, r => r.Days[i].OutOfCompetition, r => r.Days[i].OutOfCompetition = !r.Days[i].OutOfCompetition),
            "ResultStatus" => new(A, r => r.Days[i].ResultStatusOverride, r => r.Days[i].SelectedStatus = r.Days[i].StatusOptions.First(o => o.Status == FinishStatus.Dnf)),
            "Bonus" => new(A, r => r.Days[i].Bonus, r => r.Days[i].BonusText = "5"),
            "RelayTeam" => new(() => ById("Естафета 1"), r => r.Days[i].Relay.TeamName, r => r.Days[i].Relay.TeamName = "Нова"),
            "RelayTeamNumber" => new(() => ById("Естафета 2"), r => r.Days[i].Relay.TeamText, r => r.Days[i].Relay.TeamText = "50" + i),
            "RelayLeg" => new(() => ById("Естафета 3"), r => r.Days[i].Relay.LegText, r => r.Days[i].Relay.LegText = "2"),
            "Relay" => null,                                                 // «Номер етапу» follows the team and the leg
            _ => throw new Xunit.Sdk.XunitException($"roster day column block:{field} has no undo coverage"),
        };

        async Task CoverAsync(string state)
        {
            var columns = await _app.OnUiAsync(() => table.ToggleableColumns().ToList());
            var editable = columns.Where(col => col.Kind is not (SheetCellKind.ResultText or SheetCellKind.CollapsedResultText
                or SheetCellKind.TotalFee or SheetCellKind.Actions)).ToList();
            foreach (var column in editable)
            {
                var parts = column.Key.Split(':');
                var edit = parts is ["block", var field, var day] && day.StartsWith("day", StringComparison.Ordinal)
                    ? DayEdit(field, int.Parse(day[3..], System.Globalization.CultureInfo.InvariantCulture) - 1)
                    : identity.TryGetValue(column.Key, out var known) ? known
                    : throw new Xunit.Sdk.XunitException($"roster column {column.Key} ({column.Kind}) has no undo coverage");
                if (edit is not { } e)
                    continue;
                await _app.OnUiAsync(() => History(page).Clear());
                var before = await _app.OnUiAsync(() => e.Get(e.Row()));
                await _app.OnUiAsync(() => e.Set(e.Row()));
                await _app.OnUiAsync(() => page.FlushPendingWritesAsync());
                await _app.SettleAsync();
                Assert.True(await CanUndoAsync(), $"{state} {column.Key}: the edit made no undo step");
                Assert.Equal((1, 0), await DepthAsync(page));                  // a merged cell's days are one step
                Assert.NotEqual(before, await _app.OnUiAsync(() => e.Get(e.Row())));
                _output.WriteLine($"{state} {column.Key}: {await UndoHeaderAsync()}");

                await _app.OnUiAsync(() => _app.Vm.EditMenu.UndoCommand.ExecuteAsync(null));
                await WaitUndoIdleAsync(page);
                await _app.WaitUntilAsync(() => Equals(e.Get(e.Row()), before), TimeSpan.FromSeconds(10),
                    what: $"{state} {column.Key} undone (was {before})");
            }
        }

        await CoverAsync("collapsed");
        var blocks = await _app.OnUiAsync(() => page.Blocks.Where(b => b.IsCollapsed).ToList());
        await _app.OnUiAsync(() =>
        {
            foreach (var block in blocks)
                page.ToggleBlockCommand.Execute(block);
        });
        await _app.SettleAsync();
        try
        {
            await CoverAsync("expanded");
        }
        finally
        {
            // The page is shared by the whole run: fold the blocks back.
            await _app.OnUiAsync(() =>
            {
                foreach (var block in blocks)
                    page.ToggleBlockCommand.Execute(block);
            });
            await _app.SettleAsync();
        }
        AssertNoUiErrors();
    }

    // One roster column's edit for the coverage test: the row, the value it reads, the change it makes.
    private sealed record RosterEdit(
        Func<ParticipantRosterRowViewModel> Row,
        Func<ParticipantRosterRowViewModel, object?> Get,
        Action<ParticipantRosterRowViewModel> Set);

    // Two days, day 1 a rogaine («Команда», «Бонус»), a relay group, a region / club / ДЮСШ, a discount, the raised fee
    // and three additional fields — so every kind of editable column is on the tables.
    private static async Task<(CompetitionBuilder C, EntryFeeDiscount Discount)> CoverageCompetitionAsync(string id, bool paymentPerDay)
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next(id), dayCount: 2);
        await c.SetDisciplineAsync(1, DisciplineId.Rogaine);
        await c.AddControlsAsync(1, "31");
        await c.AddControlsAsync(2, "31");
        await c.AddGroupAsync("Ч21", "31");
        await c.AddGroupAsync("Ж21", "31");
        await c.AddGroupAsync("R", "31", s => { s.DisciplineOverride = DisciplineId.Relay; s.LegCount = 2; });
        await c.AddRunnerAsync("Бігун А", "1", "", "Ч21");
        await c.AddRunnerAsync("Естафета 1", "11", "", "R");
        await c.AddRunnerAsync("Естафета 2", "12", "", "R");
        await c.AddRunnerAsync("Естафета 3", "13", "", "R");
        await c.AddRelayTeamAsync("R", 301, "Стара", 1, ("Естафета 3", 1));
        await Store.AddRegionAsync(c.Folder, new Region { Name = "Київ" });
        await Store.AddClubAsync(c.Folder, new Club { Name = "Азимут" });
        await Store.AddDusshAsync(c.Folder, new Dussh { Name = "ДЮСШ 1" });
        var discount = new EntryFeeDiscount { Name = "Пільга", Percent = 50 };
        await Store.AddEntryFeeDiscountAsync(c.Folder, discount);
        var info = (await Store.GetCompetitionInfoAsync(c.Folder))!;
        info.RaisedFeeEnabled = true;
        await Store.SaveCompetitionInfoAsync(c.Folder, info);
        if (paymentPerDay)
            await Store.SetPaymentPerDayAsync(c.Folder, true, new Dictionary<Guid, string>(), new Dictionary<Guid, string>(),
                new Dictionary<Guid, bool>(), new Dictionary<Guid, bool>());
        foreach (var field in new[]
                 {
                     new CustomField("klas", "Клас", CustomFieldKind.Text, [], CustomField.UserSource, 0),
                     new CustomField("stat", "Стать", CustomFieldKind.Choice, [new ChoiceOption("Ч"), new ChoiceOption("Ж")],
                         CustomField.UserSource, 1),
                     new CustomField("flag", "Прапор", CustomFieldKind.Bool, [], CustomField.UserSource, 2),
                 })
            Assert.True(await Store.AddCustomFieldDefAsync(c.Folder, field.ToDef()));
        return (c, discount);
    }

    // ── Helpers

    private static ParticipantDayRowViewModel Row(ParticipantsViewModel page, string name)
        => page.Participants.Single(r => r.FullName == name);

    // The page's history service (the singleton the shell's keys and menu use).
    private static IUndoHistory History(PageViewModelBase page)
        => (IUndoHistory)typeof(PageViewModelBase).GetProperty("UndoHistory", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(page)!;

    // (undo, redo) steps of the page's current history.
    private Task<(int Undo, int Redo)> DepthAsync(PageViewModelBase page) => _app.OnUiAsync(() =>
    {
        var history = History(page);
        var stacks = (IDictionary)history.GetType().GetField("_stacks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(history)!;
        if (!stacks.Contains(page.CurrentUndoKey))
            return (0, 0);
        var s = stacks[page.CurrentUndoKey]!;
        int Count(string name) => ((ICollection)s.GetType().GetProperty(name)!.GetValue(s)!).Count;
        return (Count("Undo"), Count("Redo"));
    });

    // Waits until no undo / redo runs (its gate is free) and the busy overlay is gone.
    private async Task WaitUndoIdleAsync(PageViewModelBase page)
    {
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() =>
        {
            var history = History(page);
            var gate = (SemaphoreSlim)history.GetType().GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(history)!;
            return gate.CurrentCount == 1 && !_app.Vm.Busy.IsBusy;
        }, what: "undo finished");
        await _app.SettleAsync();
    }

    private Task<bool> CanUndoAsync() => _app.OnUiAsync(() =>
    {
        _app.Vm.EditMenu.Refresh();
        return _app.Vm.EditMenu.CanUndo;
    });

    private Task<string> UndoHeaderAsync() => _app.OnUiAsync(() =>
    {
        _app.Vm.EditMenu.Refresh();
        return _app.Vm.EditMenu.UndoHeader;
    });

    private static async Task<string> CoachAsync(CompetitionBuilder c, Guid participantId)
        => (await Store.GetParticipantsAsync(c.Folder)).Single(p => p.Id == participantId).Coach;

    private static async Task<Guid?> GroupOfAsync(CompetitionBuilder c, int day, Guid linkId)
        => (await Store.GetParticipantDaysAsync(c.Folder, c.Day(day).Id)).Single(l => l.Id == linkId).GroupId;

    // Clicks the row's group cell (its combo goes live), picks the group in the combo itself and closes its dropdown —
    // the combo keeps the focus, its edit still open.
    private async Task PickGroupAsync(SheetTable table, ParticipantsViewModel page, string runner, string group)
    {
        await ClickAsync(table, Row(page, runner), GroupKey);
        if (!await _app.OnUiAsync(() => Cell(table, Row(page, runner), GroupKey).GetVisualDescendants().OfType<ComboBox>().Any()))
            await KeyAsync(Key.F2, PhysicalKey.F2);
        await _app.WaitUntilAsync(() => Cell(table, Row(page, runner), GroupKey).GetVisualDescendants().OfType<ComboBox>().Any(),
            what: "group combo live");
        await _app.OnUiAsync(() =>
        {
            var combo = Cell(table, Row(page, runner), GroupKey).GetVisualDescendants().OfType<ComboBox>().First();
            combo.SelectedItem = Row(page, runner).GroupOptions.First(o => o.Label == group);
            combo.IsDropDownOpen = false;
            combo.Focus();
        });
        await _app.SettleAsync();
        Assert.True(await _app.OnUiAsync(() => _app.Window.FocusManager?.GetFocusedElement() is ComboBox), "the combo keeps the focus");
        await _app.OnUiAsync(() => page.FlushPendingWritesAsync());
    }

    private async Task<(ParticipantsViewModel Page, SheetTable Table)> OpenDayAsync(CompetitionBuilder c, int day)
    {
        await OpenCompetitionAsync(c.Summary.Identifier);
        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        await SelectDayAsync(c, page, day);
        var table = await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == "DayTable"));
        await _app.WaitUntilAsync(() => table.VisibleItems.Count == page.Participants.Count, what: "day table shows the rows");
        await _app.SettleAsync();
        await _app.SettleAsync();
        return (page, table);
    }

    // Moves the keyboard focus off the table, so no cell's editor (and its edit session, which a change would join)
    // is live when a test changes a row in code.
    private async Task RestFocusAsync()
    {
        await _app.OnUiAsync(() => _app.Window.Focus());
        await _app.SettleAsync();
    }

    // Picks a day (null = «Мандатка») on the page and waits for its rows.
    private async Task SelectDayAsync(CompetitionBuilder c, ParticipantsViewModel page, int? day)
    {
        await _app.OnUiAsync(() =>
        {
            var option = page.DayOptions.First(o => day is null ? o.IsRoster : !o.IsRoster && o.Number == day);
            if (!ReferenceEquals(page.SelectedDay, option))
                page.SelectedDay = option;
        });
        if (day is { } number)
            await WaitDayShownAsync(c, page, number);
        else
            await _app.WaitUntilAsync(() => page.IsRosterMode && page.Roster.Count > 0 && page.Participants.Count == 0,
                what: "roster shown");
        await RestFocusAsync();
    }

    private async Task WaitDayShownAsync(CompetitionBuilder c, ParticipantsViewModel page, int day)
    {
        var dayId = c.Day(day).Id;
        await _app.WaitUntilAsync(() => page.IsDayMode && page.Participants.Count > 0 && page.Participants.All(r => r.DayId == dayId),
            what: $"day {day} shown");
        await _app.SettleAsync();
    }

    private async Task ClickAsync(SheetTable table, object row, string columnKey)
    {
        var point = await _app.OnUiAsync(() =>
        {
            var cell = Cell(table, row, columnKey);
            return cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), TopLevel.GetTopLevel(table)!)
                   ?? throw new InvalidOperationException("cell not in the tree");
        });
        await _app.OnUiAsync(() => _app.Window.MouseDown(point, MouseButton.Left));
        await _app.OnUiAsync(() => _app.Window.MouseUp(point, MouseButton.Left));
        await _app.SettleAsync();
    }

    private static Control Cell(SheetTable table, object row, string columnKey)
    {
        foreach (var d in table.GetVisualDescendants())
        {
            if (d.GetType().Name != "SheetCell" || d is not Control cell || !ReferenceEquals(cell.DataContext, row) || !cell.IsVisible)
                continue;
            if (((SheetColumn)d.GetType().GetProperty("Column")!.GetValue(d)!).Key == columnKey)
                return cell;
        }
        throw new InvalidOperationException($"no realized {columnKey} cell");
    }

    private async Task KeyAsync(Key key, PhysicalKey physical)
    {
        await _app.OnUiAsync(() => _app.Window.KeyPress(key, RawInputModifiers.None, physical, null));
        await _app.SettleAsync();
    }

    // Ctrl+<key> on the window, with the keyboard focus outside a text box (where the box's own undo takes it).
    private async Task CtrlAsync(Key key, PhysicalKey physical, string symbol)
    {
        await _app.OnUiAsync(() =>
        {
            if (_app.Window.FocusManager?.GetFocusedElement() is TextBox)
                _app.Window.Focus();
            _app.Window.KeyPress(key, RawInputModifiers.Control, physical, symbol);
        });
        await _app.SettleAsync();
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Condition not met in time: {what}");
            await Task.Delay(50);
        }
    }

    private async Task OpenCompetitionAsync(string identifier)
    {
        await _app.SettleAsync();
        if (_app.Vm.IsEventSelected)
        {
            await _app.OnUiAsync(() => _app.Vm.ChangeEventCommand.ExecuteAsync(null));
            await _app.SettleAsync();
        }
        else if (_app.Vm.CurrentView is CreateEventViewModel create)
        {
            await _app.OnUiAsync(() => create.CancelCommand.Execute(null));
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => _app.Vm.CurrentView is EventSelectionViewModel, what: "picker shown");
        var selection = (EventSelectionViewModel)_app.Vm.CurrentView;
        if (!selection.Events.Any(e => e.Identifier == identifier))
        {
            var items = await selection.FetchEventsAsync();
            await _app.OnUiAsync(() => selection.Populate(items));
        }
        await _app.WaitUntilAsync(() => selection.Events.Any(e => e.Identifier == identifier), what: $"{identifier} listed");
        await _app.OnUiAsync(async () =>
        {
            selection.SelectedEvent = selection.Events.Single(e => e.Identifier == identifier);
            await selection.OpenCommand.ExecuteAsync(null);
        });
        await _app.WaitUntilAsync(() => _app.Vm.IsEventSelected, what: $"{identifier} opened");
        await _app.SettleAsync();
    }

    private ShellViewModel Shell()
    {
        var field = typeof(MainWindowViewModel).GetField("_shell", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (ShellViewModel)field.GetValue(_app.Vm)!;
    }

    private void AssertNoUiErrors()
    {
        if (_app.UiErrors.Count == 0)
            return;
        var text = string.Join(Environment.NewLine, _app.UiErrors.Select(e => e.ToString()));
        _app.UiErrors.Clear();
        Assert.Fail("The UI thread reported an exception:" + Environment.NewLine + text);
    }
}
