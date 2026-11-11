using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// A leg typed in a participant table through real input (click «Етап», type) that swaps two teammates: the OTHER
/// runner's row must show its new leg on screen at once — the visible cell text, not just the row VM — even while the
/// operator has already moved on and is typing in another cell (which holds the page's full reload back).
/// </summary>
[Collection(AppCollection.Name)]
public sealed class RelayCellsRefreshUiTests
{
    private const string LegKey = "id:Participants.Col.RelayLeg";
    private const string BibKey = "id:Participants.Col.RelayBib";
    private const string NumberKey = "id:Participants.Col.Number";

    private readonly HeadlessApp _app;

    public RelayCellsRefreshUiTests(HeadlessApp app) => _app = app;

    private static IServiceProvider Core => TestEnvironment.Instance.Services;
    private static IEventStore Store => Core.GetRequiredService<IEventStore>();

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SwappedTeammateRowShowsItsNewLegAtOnce(bool roster, bool big)
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_relayswap"), dayCount: 2, discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("R", "31", s => s.LegCount = 3);
        var rowCount = 4;
        if (big)
        {
            // Ten teams of three with bibs, the swapped team in the middle; the table sorted by «Номер етапу».
            for (var t = 0; t < 10; t++)
            {
                var names = t == 4 ? new[] { "A", "B", "C" } : new[] { $"X{t}a", $"X{t}b", $"X{t}c" };
                for (var i = 0; i < 3; i++)
                    await c.AddRunnerAsync(names[i], (t * 3 + i + 1).ToString(), "", "R");
            }
            await c.AddRunnerAsync("D", "99", "", "R");
            for (var t = 0; t < 10; t++)
            {
                if (t == 4)
                    await c.AddRelayTeamAsync("R", 101, "Азимут", 1, ("A", 1), ("B", 2), ("C", 3));
                else
                    await c.AddRelayTeamAsync("R", 200 + t, $"Команда {t}", 1, ($"X{t}a", 1), ($"X{t}b", 2), ($"X{t}c", 3));
            }
            rowCount = 31;
        }
        else
        {
            foreach (var name in new[] { "A", "B", "C", "D" })
                await c.AddRunnerAsync(name, "", "", "R");
            await c.AddRelayTeamAsync("R", 101, "Азимут", 1, ("A", 1), ("B", 2), ("C", 3));
        }
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        await _app.OnUiAsync(() => page.SelectedDay = page.DayOptions.First(d => d.IsRoster == roster));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => roster ? page.IsRosterMode && page.Roster.Count == rowCount : page.IsDayMode && page.Participants.Count == rowCount,
            what: "all rows");
        var table = await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == (roster ? "RosterTable" : "DayTable")));
        await _app.WaitUntilAsync(() => table.VisibleItems.Count == rowCount, what: "table shows the rows");
        await _app.SettleAsync();
        await _app.SettleAsync();

        // Both days run a relay, so the roster's relay blocks span two days: expand them to get day 1's own cells.
        if (roster)
        {
            await _app.OnUiAsync(() =>
            {
                foreach (var block in page.Blocks.Where(b => b.Field is RosterField.Relay or RosterField.RelayLeg && b.IsCollapsed).ToList())
                    page.ToggleBlockCommand.Execute(block);
            });
            await _app.SettleAsync();
            await _app.SettleAsync();
        }
        var legKey = roster ? "block:RelayLeg:day1" : LegKey;
        var bibKey = roster ? "block:Relay:day1" : BibKey;
        if (big)
        {
            await _app.OnUiAsync(() => table.SetSortLevels([(bibKey, false)]));
            await _app.SettleAsync();
            await _app.SettleAsync();
        }
        object Row(string name) => roster
            ? page.Roster.ToList().Single(r => r.FullName == name)
            : page.Participants.ToList().Single(r => r.FullName == name);

        // The window is widened so the relay columns are on screen.
        var window = _app.Window;
        var oldWidth = await _app.OnUiAsync(() => window.Width);
        await _app.OnUiAsync(() => window.Width = 3600);
        await _app.SettleAsync();
        await _app.SettleAsync();
        try
        {
            Assert.Equal("1", await _app.OnUiAsync(() => ShownText(table, Row("A"), legKey)));
            Assert.Equal("2", await _app.OnUiAsync(() => ShownText(table, Row("B"), legKey)));

            // Click A's «Етап», type 2.
            await ClickAsync(table, Row("A"), legKey);
            await _app.OnUiAsync(() => window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null));
            await _app.SettleAsync();
            await _app.OnUiAsync(() => window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null));
            await _app.OnUiAsync(() => window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null));
            await _app.OnUiAsync(() => window.KeyTextInput("2"));
            await _app.SettleAsync();
            Assert.Equal("[edit]2", await _app.OnUiAsync(() => ShownText(table, Row("A"), legKey)));

            // Leave it for C's «Номер» and start typing there: the edit is saved (A and B swap), while the cell being
            // typed in holds the page's full reload back — B's row must show its new leg anyway.
            await ClickAsync(table, Row("C"), NumberKey);
            await _app.OnUiAsync(() => window.KeyPress(Key.D5, RawInputModifiers.None, PhysicalKey.Digit5, "5"));
            await _app.SettleAsync();
            await _app.WaitUntilAsync(() => LegOf(c, "A") == 2 && LegOf(c, "B") == 1, what: "A and B swapped in the store");
            // (The condition runs on the UI thread.)
            await _app.WaitUntilAsync(() => ShownText(table, Row("B"), legKey) == "1",
                TimeSpan.FromSeconds(5), what: "B's row shows leg 1 while another cell is typed in");
            Assert.True(await _app.OnUiAsync(() => page.IsEditingCell), "the typing in C's «Номер» is still in progress");

            var shown = await _app.OnUiAsync(() => (
                ShownText(table, Row("A"), legKey), ShownText(table, Row("A"), bibKey),
                ShownText(table, Row("B"), legKey), ShownText(table, Row("B"), bibKey)));
            await _app.ScreenshotAsync($"relay-swap-{(roster ? "roster" : "day")}{(big ? "-big" : "")}");
            Assert.Equal(("2", "101.2", "1", "101.1"), shown);

            // Escape drops the number typed in C's cell.
            await _app.OnUiAsync(() => window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null));
            await _app.SettleAsync();
            AssertNoUiErrors();
        }
        finally
        {
            await _app.OnUiAsync(() => window.Width = oldWidth);
        }
    }

    private async Task ClickAsync(SheetTable table, object row, string columnKey)
    {
        var point = await _app.OnUiAsync(() => CellCenter(table, row, columnKey));
        await _app.OnUiAsync(() => _app.Window.MouseDown(point, MouseButton.Left));
        await _app.OnUiAsync(() => _app.Window.MouseUp(point, MouseButton.Left));
        await _app.SettleAsync();
    }

    // The text the cell shows: the live editor's text while editing, else the resting label's.
    private static string ShownText(SheetTable table, object row, string columnKey)
    {
        var cell = Cell(table, row, columnKey);
        var box = cell.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        if (box is not null)
            return "[edit]" + box.Text;
        return string.Concat(cell.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text));
    }

    private static Point CellCenter(SheetTable table, object row, string columnKey)
    {
        var cell = Cell(table, row, columnKey);
        var centre = new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2);
        return cell.TranslatePoint(centre, TopLevel.GetTopLevel(table)!) ?? throw new InvalidOperationException("cell not in the tree");
    }

    private static Control Cell(SheetTable table, object row, string columnKey)
    {
        var matches = new List<Control>();
        foreach (var d in table.GetVisualDescendants())
        {
            if (d.GetType().Name != "SheetCell" || d is not Control cell || !ReferenceEquals(cell.DataContext, row) || !cell.IsVisible)
                continue;
            var column = (SheetColumn)d.GetType().GetProperty("Column")!.GetValue(d)!;
            if (column.Key == columnKey)
                matches.Add(cell);
        }
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"no realized {columnKey} cell"),
            _ => throw new InvalidOperationException($"{matches.Count} realized {columnKey} cells for one row")
        };
    }

    private static int? LegOf(CompetitionBuilder c, string runner) =>
        Store.GetParticipantDaysAsync(c.Folder, c.Day(1).Id).GetAwaiter().GetResult()
            .Single(l => l.ParticipantId == c.Runners[runner].Id).Leg;

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
