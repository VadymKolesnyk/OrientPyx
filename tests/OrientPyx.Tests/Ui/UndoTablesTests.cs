using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// Ctrl+Z / Ctrl+Y on the smaller table pages (undo plan, stage 4) through the real app: an edit made on the page is
/// undone — back in the page and in <c>event.db</c> — and redone; a deleted row comes back with what went with it (a
/// region's participants, a discount's links); on «КП» an edit made just before a day switch lands on its own day and
/// is undone there, not on the day switched to.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class UndoTablesTests : IAsyncLifetime
{
    // Undo / redo toasts stay up for 15 s: cleared before and after each test, so a stack of them never covers the
    // table of the next test in the shared headless window.
    public Task InitializeAsync() => ClearToastsAsync();

    public Task DisposeAsync() => ClearToastsAsync();

    private Task ClearToastsAsync() => _app.OnUiAsync(() => _app.Vm.Toasts.Items.Clear());

    private readonly HeadlessApp _app;

    public UndoTablesTests(HeadlessApp app) => _app = app;

    private static IServiceProvider Core => TestEnvironment.Instance.Services;

    private static IEventStore Store => Core.GetRequiredService<IEventStore>();

    [Fact]
    public async Task ChipEditIsUndoneAndRedoneByTheKeysAndADeletedChipComesBack()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_undo_chip"));
        var chip = new RentalChip { Number = "8001", Note = "стара" };
        await Store.AddRentalChipAsync(c.Folder, chip);
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenChipsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<ChipsViewModel>(Shell().SelectedPage);
        await _app.WaitUntilAsync(() => page.Chips.Any(r => r.Id == chip.Id), what: "chip row shown");

        // An edit, saved after the debounce.
        await _app.OnUiAsync(() => page.Chips.Single(r => r.Id == chip.Id).Note = "нова");
        await _app.OnUiAsync(() => page.FlushPendingWritesAsync());
        Assert.Equal("нова", await ChipNoteAsync(c, chip.Id));
        var label = await _app.OnUiAsync(() => { _app.Vm.EditMenu.Refresh(); return _app.Vm.EditMenu.UndoHeader; });
        Assert.Contains("8001", label);

        // Ctrl+Z on the window: the note is back on the page and in the database; Ctrl+Y puts the edit back.
        await PressAsync(Key.Z, PhysicalKey.Z, "z");
        await _app.WaitUntilAsync(() => page.Chips.Single(r => r.Id == chip.Id).Note == "стара", what: "note undone on the page");
        await _app.SettleAsync();
        Assert.Equal("стара", await ChipNoteAsync(c, chip.Id));

        await PressAsync(Key.Y, PhysicalKey.Y, "y");
        await _app.WaitUntilAsync(() => page.Chips.Single(r => r.Id == chip.Id).Note == "нова", what: "note redone on the page");
        await _app.SettleAsync();
        Assert.Equal("нова", await ChipNoteAsync(c, chip.Id));

        // A delete (the row leaves the table at once, the database follows in the background) is undone as a whole.
        await _app.OnUiAsync(() => page.DeleteChipNoConfirmAsync(page.Chips.Single(r => r.Id == chip.Id)));
        await WaitForAsync(async () => (await Store.GetRentalChipsAsync(c.Folder)).Count == 0, "chip deleted");
        await UndoAsync();
        await _app.WaitUntilAsync(() => page.Chips.Any(r => r.Id == chip.Id), what: "deleted chip back on the page");
        Assert.Equal("нова", await ChipNoteAsync(c, chip.Id));
        AssertNoUiErrors();
    }

    [Fact]
    public async Task ControlPointEditBeforeADaySwitchLandsOnItsDayAndIsUndoneThere()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_undo_cp"), dayCount: 2);
        await c.AddControlsAsync(1, "31", "32");
        await c.AddControlsAsync(2, "41");
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenControlPointsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<ControlPointsViewModel>(Shell().SelectedPage);
        await SwitchDayAsync(page, 1, "32");

        // «Проблемний КП» ticked and the day switched at once, well inside the 0.6 s debounce: the write is not
        // dropped by the switch and goes to day 1.
        await _app.OnUiAsync(() =>
        {
            page.Points.Single(p => p.Code == "32").IsDisabled = true;
            page.SelectedDay = page.DayOptions.Single(o => o.Number == 2);
        });
        await _app.WaitUntilAsync(() => page.Points.Count == 1 && page.Points[0].Code == "41", what: "day 2 shown");
        await _app.SettleAsync();
        Assert.True(await IsDisabledAsync(c, 1, "32"));
        Assert.False(await IsDisabledAsync(c, 2, "41"));

        // Day 2 has a history of its own: nothing to undo there.
        Assert.False(await _app.OnUiAsync(() => { _app.Vm.EditMenu.Refresh(); return _app.Vm.EditMenu.CanUndo; }));

        // Back on day 1 the step is there and undoes the toggle.
        await SwitchDayAsync(page, 1, "32");
        await UndoAsync();
        await _app.WaitUntilAsync(() => !page.Points.Single(p => p.Code == "32").IsDisabled, what: "toggle undone on the page");
        Assert.False(await IsDisabledAsync(c, 1, "32"));
        AssertNoUiErrors();
    }

    [Fact]
    public async Task DeletedRegionComesBackWithItsParticipantsInOneStep()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_undo_reg"));
        var region = new Region { Name = "Київ" };
        await Store.AddRegionAsync(c.Folder, region);
        await c.AddGroupAsync("Ч21", "31");
        var runner = await c.AddRunnerAsync("Іваненко Іван", "1", "", "Ч21", configure: p => p.RegionId = region.Id);
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenRegionsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<RegionsViewModel>(Shell().SelectedPage);
        await _app.WaitUntilAsync(() => page.Regions.Any(r => r.Id == region.Id), what: "region row shown");

        // A rename, then the delete (which also clears the region from the participant).
        await _app.OnUiAsync(() => page.Regions.Single(r => r.Id == region.Id).Name = "Київська");
        await _app.OnUiAsync(() => page.FlushPendingWritesAsync());
        Assert.Equal("Київська", (await Store.GetRegionsAsync(c.Folder)).Single().Name);

        await _app.OnUiAsync(() => page.DeleteRegionNoConfirmAsync(page.Regions.Single(r => r.Id == region.Id)));
        await WaitForAsync(async () => (await Store.GetRegionsAsync(c.Folder)).Count == 0, "region deleted");
        Assert.Null(await RegionOfAsync(c, runner.Id));

        // One Ctrl+Z brings back the region and the participant's reference to it.
        await UndoAsync();
        await _app.WaitUntilAsync(() => page.Regions.Any(r => r.Id == region.Id), what: "region back on the page");
        Assert.Equal("Київська", (await Store.GetRegionsAsync(c.Folder)).Single().Name);
        Assert.Equal(region.Id, await RegionOfAsync(c, runner.Id));
        Assert.Equal(1, await _app.OnUiAsync(() => page.Regions.Single(r => r.Id == region.Id).ParticipantCount));

        // The next one undoes the rename.
        await UndoAsync();
        await _app.WaitUntilAsync(() => page.Regions.Single(r => r.Id == region.Id).Name == "Київ", what: "rename undone on the page");
        Assert.Equal("Київ", (await Store.GetRegionsAsync(c.Folder)).Single().Name);
        AssertNoUiErrors();
    }

    [Fact]
    public async Task EntryFeeEditsAndADeletedDiscountAreUndone()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_undo_fee"));
        var group = await c.AddGroupAsync("Ч21", "31");
        var runner = await c.AddRunnerAsync("Петренко Петро", "1", "", "Ч21");
        var discount = new EntryFeeDiscount { Name = "Студенти", Percent = 50 };
        await Store.AddEntryFeeDiscountAsync(c.Folder, discount);
        await Store.SetParticipantDiscountAsync(c.Folder, runner.Id, discount.Id, on: true);
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenEntryFeesCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<EntryFeesViewModel>(Shell().SelectedPage);
        await _app.WaitUntilAsync(() => page.GroupFees.Any(r => r.Id == group.Id) && page.Discounts.Any(r => r.Id == discount.Id),
            what: "fee tables shown");

        // The group's fee, then the discount's percent, then the discount deleted (its participant link goes with it).
        await _app.OnUiAsync(() => page.GroupFees.Single(r => r.Id == group.Id).FeeText = "150");
        await _app.OnUiAsync(() => page.Discounts.Single(r => r.Id == discount.Id).PercentText = "30");
        await _app.OnUiAsync(() => page.FlushPendingWritesAsync());
        Assert.Equal(150m, await GroupFeeAsync(c, group.Id));
        Assert.Equal(30m, (await Store.GetEntryFeeDiscountsAsync(c.Folder)).Single(d => d.Id == discount.Id).Percent);

        await _app.OnUiAsync(() => page.DeleteDiscountNoConfirmAsync(page.Discounts.Single(r => r.Id == discount.Id)));
        await WaitForAsync(async () => (await Store.GetEntryFeeDiscountsAsync(c.Folder)).All(d => d.Id != discount.Id), "discount deleted");
        Assert.Empty(await LinksAsync(c, discount.Id));

        // Ctrl+Z: the discount is back with its link; again: its percent; again: the group's fee.
        await UndoAsync();
        await _app.WaitUntilAsync(() => page.Discounts.Any(r => r.Id == discount.Id), what: "discount back on the page");
        Assert.Equal(30m, (await Store.GetEntryFeeDiscountsAsync(c.Folder)).Single(d => d.Id == discount.Id).Percent);
        Assert.Single(await LinksAsync(c, discount.Id));

        await UndoAsync();
        await _app.WaitUntilAsync(() => page.Discounts.Single(r => r.Id == discount.Id).PercentText == "50", what: "percent undone");
        Assert.Equal(50m, (await Store.GetEntryFeeDiscountsAsync(c.Folder)).Single(d => d.Id == discount.Id).Percent);

        await UndoAsync();
        await _app.WaitUntilAsync(() => page.GroupFees.Single(r => r.Id == group.Id).FeeText == "", what: "fee undone");
        Assert.Null(await GroupFeeAsync(c, group.Id));
        AssertNoUiErrors();
    }

    // ── Helpers

    private static async Task<string> ChipNoteAsync(CompetitionBuilder c, Guid id)
        => (await Store.GetRentalChipsAsync(c.Folder)).Single(x => x.Id == id).Note;

    private static async Task<bool> IsDisabledAsync(CompetitionBuilder c, int day, string code)
        => (await Store.GetControlPointsAsync(c.Folder, c.Day(day).Id)).Single(p => p.Code == code).IsDisabled;

    private static async Task<Guid?> RegionOfAsync(CompetitionBuilder c, Guid participantId)
        => (await Store.GetParticipantsAsync(c.Folder)).Single(p => p.Id == participantId).RegionId;

    private static async Task<decimal?> GroupFeeAsync(CompetitionBuilder c, Guid groupId)
        => (await Store.GetGroupsAsync(c.Folder)).Single(g => g.Id == groupId).EntryFee;

    private static async Task<List<ParticipantDiscount>> LinksAsync(CompetitionBuilder c, Guid discountId)
        => (await Store.GetParticipantDiscountsAsync(c.Folder)).Where(l => l.DiscountId == discountId).ToList();

    // Picks the day on the page (if it isn't shown already) and waits for its rows.
    private async Task SwitchDayAsync(ControlPointsViewModel page, int day, string code)
    {
        await _app.OnUiAsync(() =>
        {
            if (page.SelectedDay?.Number != day)
                page.SelectedDay = page.DayOptions.Single(o => o.Number == day);
        });
        await _app.WaitUntilAsync(() => page.Points.Any(p => p.Code == code), what: $"day {day} shown");
        await _app.SettleAsync();
    }

    // The «Правка» menu's «Скасувати» / «Повторити» (awaited: the revert and the page's reload are done after it).
    private async Task UndoAsync(bool redo = false)
    {
        var can = await _app.OnUiAsync(() =>
        {
            _app.Vm.EditMenu.Refresh();
            return redo ? _app.Vm.EditMenu.CanRedo : _app.Vm.EditMenu.CanUndo;
        });
        Assert.True(can, redo ? "nothing to redo" : "nothing to undo");
        await _app.OnUiAsync(() => redo
            ? _app.Vm.EditMenu.RedoCommand.ExecuteAsync(null)
            : _app.Vm.EditMenu.UndoCommand.ExecuteAsync(null));
        await _app.SettleAsync();
    }

    // Ctrl+<key> on the window, with the keyboard focus outside a text box (where the box's own undo would take it).
    private async Task PressAsync(Key key, PhysicalKey physical, string symbol)
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
