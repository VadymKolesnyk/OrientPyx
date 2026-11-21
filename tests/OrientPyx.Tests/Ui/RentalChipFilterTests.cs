using System.Reflection;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// The «Чіп» column's «За станом» filter (орендний / не орендний / без чіпа, by the competition's rental-chip base)
/// on both participant tables: the roster's merged all-days column judges every member day's chip (a runner with a
/// rental chip on one day and their own on the other is under both), a day column only that day's chip; by-values
/// keeps working on the chip numbers.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class RentalChipFilterTests
{
    private readonly HeadlessApp _app;

    public RentalChipFilterTests(HeadlessApp app)
    {
        _app = app;
    }

    private static IServiceProvider Core => TestEnvironment.Instance.Services;
    private static IEventStore Store => Core.GetRequiredService<IEventStore>();

    [Fact]
    public async Task ChipColumnFiltersByRentalStatus()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_rentf"), dayCount: 2);
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("Орендар", "1", "1001", "Ч21");
        await c.AddRunnerAsync("Власник", "2", "2002", "Ч21");
        await c.AddRunnerAsync("Безчіповий", "3", "", "Ч21");
        await c.AddRunnerAsync("Змішаний", "4", "1003", "Ч21");
        await c.UpdateLinkAsync("Змішаний", 2, l => l.Chip = "2004");
        // Runs only day 2: on day 1 they have no chip state at all.
        await c.AddRunnerAsync("Другоденний", "5", "", "Ч21", days: [2]);
        await Store.AddRentalChipsAsync(c.Folder,
            [new RentalChip { Number = "1001" }, new RentalChip { Number = "1003" }]);
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var participants = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        if (!participants.IsRosterMode)
        {
            await _app.OnUiAsync(() => participants.SelectedDay = participants.DayOptions.First(d => d.IsRoster));
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => participants.IsRosterMode && participants.Roster.Count == 5, what: "five roster rows");
        var roster = await FindTableAsync("RosterTable");
        await _app.WaitUntilAsync(() => roster.VisibleItems.Count == 5, what: "roster table shows the rows");
        await _app.WaitUntilAsync(() => participants.RentalChips.Count == 2, what: "rental base loaded");

        // The collapsed «Чіп» block: every member day's chip counts.
        const string merged = "block:Chips";
        Assert.Equal(["1", "4"], await StatusAsync(roster, merged, RentalChipStatus.Rental));
        Assert.Equal(["2", "4"], await StatusAsync(roster, merged, RentalChipStatus.NonRental));
        Assert.Equal(["3", "5"], await StatusAsync(roster, merged, RentalChipStatus.NoChip));
        Assert.Equal(["1", "2", "4"], await StatusAsync(roster, merged, RentalChipStatus.Rental, RentalChipStatus.NonRental));
        await _app.ScreenshotAsync("rental-filter-10-roster-rental");
        await ClearAsync(roster, 5);

        // Expanded: one column per day; day 1 has no state for the day-2-only runner. (Filters of different columns
        // add up, so each day column is cleared before the next.)
        var block = await _app.OnUiAsync(() => participants.Blocks.Single(b => b.Field == RosterField.Chips));
        await _app.OnUiAsync(() => participants.ToggleBlockCommand.Execute(block));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => roster.FindColumnByKey("block:Chips:day1") is not null, what: "chip block expanded");
        Assert.Equal(["3"], await StatusAsync(roster, "block:Chips:day1", RentalChipStatus.NoChip));
        Assert.Equal(["1", "4"], await StatusAsync(roster, "block:Chips:day1", RentalChipStatus.Rental));
        await ClearAsync(roster, 5);
        Assert.Equal(["2", "4"], await StatusAsync(roster, "block:Chips:day2", RentalChipStatus.NonRental));
        Assert.Equal(["3", "5"], await StatusAsync(roster, "block:Chips:day2", RentalChipStatus.NoChip));
        await ClearAsync(roster, 5);

        // The day grid (day 2): the row's own chip.
        await _app.OnUiAsync(() => participants.SelectedDay = participants.DayOptions.First(d => !d.IsRoster && d.Day?.Number == 2));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => !participants.IsRosterMode && participants.Participants.Count == 5, what: "day 2 rows");
        var day = await FindTableAsync("DayTable");
        await _app.WaitUntilAsync(() => day.VisibleItems.Count == 5, what: "day table shows the rows");
        const string chip = "id:Participants.Col.Chip";
        Assert.Equal(["1"], await StatusAsync(day, chip, RentalChipStatus.Rental));
        Assert.Equal(["2", "4"], await StatusAsync(day, chip, RentalChipStatus.NonRental));
        Assert.Equal(["3", "5"], await StatusAsync(day, chip, RentalChipStatus.NoChip));
        await _app.ScreenshotAsync("rental-filter-20-day-own");

        // Marking a chip rental moves it over on the next filter pass.
        await _app.OnUiAsync(() => participants.RentalChips.Add("2004"));
        Assert.Equal(["1", "4"], await StatusAsync(day, chip, RentalChipStatus.Rental));

        // The by-values mode of the same column still filters on the chip numbers.
        await _app.OnUiAsync(() => day.SetColumnFilter(chip, new SheetFilter
        {
            ColumnKey = chip, Mode = SheetFilterMode.Values, AllowedValues = ["2002"]
        }));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => day.VisibleItems.Count == 1, what: "chip 2002 alone");
        await ClearAsync(day, 5);
        AssertNoUiErrors();
    }

    // Applies «За станом» with the given kept states and returns the shown rows' numbers, sorted.
    private async Task<string[]> StatusAsync(SheetTable table, string columnKey, params string[] keep)
    {
        await _app.OnUiAsync(() => table.SetColumnFilter(columnKey, new SheetFilter
        {
            ColumnKey = columnKey, Mode = SheetFilterMode.Status, AllowedStatuses = [.. keep]
        }));
        await _app.SettleAsync();
        return await _app.OnUiAsync(() => table.VisibleItems
            .Select(r => r switch
            {
                ParticipantRosterRowViewModel roster => roster.Number,
                ParticipantDayRowViewModel day => day.Number,
                _ => "?"
            })
            .Order(StringComparer.Ordinal)
            .ToArray());
    }

    private async Task ClearAsync(SheetTable table, int rows)
    {
        await _app.OnUiAsync(() => table.ClearAllFilters());
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => table.VisibleItems.Count == rows, what: "filter cleared");
    }

    private async Task<SheetTable> FindTableAsync(string name)
    {
        await _app.WaitUntilAsync(() => _app.Window.GetVisualDescendants().OfType<SheetTable>().Any(t => t.Name == name && t.IsVisible),
            what: $"{name} shown");
        return await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == name));
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
