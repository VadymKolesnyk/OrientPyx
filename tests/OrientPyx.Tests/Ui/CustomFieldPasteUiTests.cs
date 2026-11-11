using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// Ctrl+V onto an additional-field column, on both participant tables: a single value lands in the focused row, a
/// column of values fills down one row each — for a text field, a choice field (an option typed in another case is
/// stored as the option, Р-18; the dropdown must not open) and a flag, and for a field whose key is all digits. The
/// value reaches the event database, not only the cell.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class CustomFieldPasteUiTests
{
    private readonly HeadlessApp _app;

    public CustomFieldPasteUiTests(HeadlessApp app) => _app = app;

    private static IServiceProvider Core => TestEnvironment.Instance.Services;
    private static IEventStore Store => Core.GetRequiredService<IEventStore>();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PastingOntoAdditionalFields_WritesTheRows(bool roster)
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next(roster ? "ui_cfpr" : "ui_cfpd"));
        await c.AddGroupAsync("Ч21", "31");
        var runners = new List<Guid>();
        for (var i = 1; i <= 3; i++)
            runners.Add((await c.AddRunnerAsync($"Бігун 0{i}", i.ToString(), "", "Ч21")).Id);
        var fields = new[]
        {
            new CustomField("klas", "Клас", CustomFieldKind.Text, [], CustomField.UserSource, 0),
            new CustomField("stat", "Стать", CustomFieldKind.Choice, [new ChoiceOption("Ч"), new ChoiceOption("Ж")],
                CustomField.UserSource, 1),
            new CustomField("flag", "Прапор", CustomFieldKind.Bool, [], CustomField.UserSource, 2),
            new CustomField("2025", "2025", CustomFieldKind.Number, [], CustomField.UserSource, 3),
        };
        foreach (var field in fields)
            Assert.True(await Store.AddCustomFieldDefAsync(c.Folder, field.ToDef()));

        var table = await OpenTableAsync(c.Summary.Identifier, roster);

        // A single value: the focused row only.
        await PasteAsync(table, row: 0, "field.klas", "10-А");
        await PasteAsync(table, row: 0, "field.stat", "ж");
        await PasteAsync(table, row: 0, "field.flag", "так");
        await WaitStoredAsync(c.Folder, runners[0], "klas", "10-А");
        await WaitStoredAsync(c.Folder, runners[0], "stat", "Ж");
        await WaitStoredAsync(c.Folder, runners[0], "flag", "1");

        // A column of values: one row each, starting at the focused row.
        await PasteAsync(table, row: 0, "field.klas", "11-Б\r\n9-В\r\n8-Г\r\n");
        await PasteAsync(table, row: 0, "field.stat", "Ч\nч\nЖ");
        await PasteAsync(table, row: 1, "field.flag", "1\n+");
        await PasteAsync(table, row: 0, "field.2025", "1,5\n2\n3");
        string[] klas = ["11-Б", "9-В", "8-Г"], stat = ["Ч", "Ч", "Ж"], flag = ["1", "1", "1"], year = ["1.5", "2", "3"];
        for (var i = 0; i < 3; i++)
        {
            await WaitStoredAsync(c.Folder, runners[i], "klas", klas[i]);
            await WaitStoredAsync(c.Folder, runners[i], "stat", stat[i]);
            await WaitStoredAsync(c.Folder, runners[i], "flag", flag[i]);
            await WaitStoredAsync(c.Folder, runners[i], "2025", year[i]);
        }

        // The dropdowns show the stored options; the interim (red) option of a lower-case paste is gone.
        var shown = await _app.OnUiAsync(() => table.VisibleItems.Cast<object>().Select(r => FieldsOf(r!)["stat"]).ToList());
        Assert.Equal(stat, shown.Select(f => f.Selected.Value));
        Assert.All(shown, f => Assert.DoesNotContain(f.Options, o => o.IsUnknown));
        AssertNoUiErrors();
    }

    // What each paste saw (the focused cell before and after), for the failure message.
    private readonly List<string> _log = [];

    // The focused element and its row, against the row the test meant.
    private static string Describe(Visual? focused, SheetTable table, int row)
    {
        var cell = focused?.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => c.GetType().Name == "SheetCell");
        var name = (object? o) => o switch
        {
            ParticipantRosterRowViewModel r => r.FullName,
            ParticipantDayRowViewModel d => d.FullName,
            _ => o?.GetType().Name ?? "null"
        };
        var text = focused is TextBox tb ? $" TextBox«{tb.Text}»" : focused is null ? " none" : " " + focused.GetType().Name;
        return $"cell={name(cell?.DataContext)} want={name(table.VisibleItems[row])}{text}";
    }

    // Focuses the cell, puts the text on the clipboard, presses Ctrl+V and — when the paste opened the cell's text
    // editor — Enter to commit it.
    private async Task PasteAsync(SheetTable table, int row, string columnKey, string text)
    {
        var window = _app.Window;
        await _app.OnUiAsync(() => window.Clipboard!.SetTextAsync(text));
        await FocusCellAsync(table, row, columnKey);
        var before = await _app.OnUiAsync(() => Describe(window.FocusManager?.GetFocusedElement() as Visual, table, row));
        await _app.OnUiAsync(() => window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v"));
        await _app.OnUiAsync(() => window.KeyRelease(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v"));
        await _app.SettleAsync();
        await _app.SettleAsync();
        var after = await _app.OnUiAsync(() => Describe(window.FocusManager?.GetFocusedElement() as Visual, table, row));
        _log.Add($"{columnKey}@{row}: before {before}; after {after}");
        if (await _app.OnUiAsync(() => window.FocusManager?.GetFocusedElement() is TextBox))
        {
            await _app.OnUiAsync(() => window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null));
            await _app.SettleAsync();
        }
        await _app.SettleAsync();
    }

    // Moves the focus to the row's cell in the column with the arrow keys, as a user would — the field columns sit
    // past the window's right edge until the table scrolls to them. The first call clicks the row's «Номер» cell.
    private async Task FocusCellAsync(SheetTable table, int row, string columnKey)
    {
        var window = _app.Window;
        if (await _app.OnUiAsync(() => FocusedCell(window)) is null)
        {
            var point = await _app.OnUiAsync(() => NumberCellPoint(table, row));
            await _app.OnUiAsync(() => window.MouseDown(point, MouseButton.Left));
            await _app.OnUiAsync(() => window.MouseUp(point, MouseButton.Left));
            await _app.SettleAsync();
        }
        var targetColumn = await _app.OnUiAsync(() => ColumnIndexOf(table, columnKey));
        for (var i = 0; i < 100; i++)
        {
            var (rowAt, columnAt) = await _app.OnUiAsync(() =>
            {
                var cell = FocusedCell(window);
                return cell is null
                    ? (-1, -1)
                    : (table.VisibleItems.ToList().IndexOf(cell.DataContext), (int)cell.GetType().GetProperty("ColumnIndex")!.GetValue(cell)!);
            });
            if (rowAt < 0)
                break;
            (Key Key, PhysicalKey Physical)? step = rowAt < row ? (Key.Down, PhysicalKey.ArrowDown)
                : rowAt > row ? (Key.Up, PhysicalKey.ArrowUp)
                : columnAt < targetColumn ? (Key.Right, PhysicalKey.ArrowRight)
                : columnAt > targetColumn ? (Key.Left, PhysicalKey.ArrowLeft)
                : null;
            if (step is not { } s)
                return;
            await _app.OnUiAsync(() => window.KeyPress(s.Key, RawInputModifiers.None, s.Physical, null));
            await _app.SettleAsync();
        }
        throw new InvalidOperationException($"could not reach {columnKey}@{row} (focus: {await _app.OnUiAsync(() => Describe(window.FocusManager?.GetFocusedElement() as Visual, table, row))})");
    }

    private static Control? FocusedCell(Window window) =>
        (window.FocusManager?.GetFocusedElement() as Visual)?.GetSelfAndVisualAncestors().OfType<Control>()
            .FirstOrDefault(v => v.GetType().Name == "SheetCell");

    // The leaf-column index of the column with the key, read off any realized cell of it.
    private static int ColumnIndexOf(SheetTable table, string columnKey)
    {
        foreach (var d in table.GetVisualDescendants())
        {
            if (d.GetType().Name != "SheetCell")
                continue;
            var column = d.GetType().GetProperty("Column")!.GetValue(d)!;
            if (column.GetType().GetProperty("Key")!.GetValue(column) as string == columnKey)
                return (int)d.GetType().GetProperty("ColumnIndex")!.GetValue(d)!;
        }
        throw new InvalidOperationException($"no realized {columnKey} cell");
    }

    private async Task WaitStoredAsync(string folder, Guid participantId, string key, string expected)
    {
        string? stored = null;
        try
        {
            await _app.WaitUntilAsync(() =>
            {
                stored = Store.GetParticipantFieldValuesAsync(folder).GetAwaiter().GetResult()
                    .FirstOrDefault(v => v.ParticipantId == participantId && v.Key == key)?.Value ?? string.Empty;
                return stored == expected;
            }, TimeSpan.FromSeconds(5), what: $"{key} = {expected}");
        }
        catch (Exception e)
        {
            throw new Xunit.Sdk.XunitException($"{key} of {participantId}: stored «{stored}», expected «{expected}» ({e.Message})" + Environment.NewLine + string.Join(Environment.NewLine, _log));
        }
    }

    private static ParticipantFieldsViewModel FieldsOf(object row) => row switch
    {
        ParticipantRosterRowViewModel r => r.Fields,
        ParticipantDayRowViewModel d => d.Fields,
        _ => throw new InvalidOperationException(row.GetType().Name)
    };

    // The centre of the row's «Номер» cell, in window coordinates (a recycled, hidden container is skipped).
    private static Point NumberCellPoint(SheetTable table, int rowIndex)
    {
        var row = table.VisibleItems[rowIndex];
        foreach (var d in table.GetVisualDescendants())
        {
            if (d.GetType().Name != "SheetCell" || d is not Control cell || !ReferenceEquals(cell.DataContext, row)
                || !cell.IsEffectivelyVisible)
                continue;
            var column = d.GetType().GetProperty("Column")!.GetValue(d)!;
            if (column.GetType().GetProperty("ShowCount")!.GetValue(column) is true)
                return cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), TopLevel.GetTopLevel(table)!)
                       ?? throw new InvalidOperationException("cell not in the tree");
        }
        throw new InvalidOperationException($"no realized «Номер» cell for row {rowIndex}");
    }

    private async Task<SheetTable> OpenTableAsync(string identifier, bool roster)
    {
        await OpenCompetitionAsync(identifier);
        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        var wanted = await _app.OnUiAsync(() => page.DayOptions.First(d => d.IsRoster == roster));
        if (await _app.OnUiAsync(() => page.SelectedDay != wanted))
        {
            await _app.OnUiAsync(() => page.SelectedDay = wanted);
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => (roster ? page.Roster.Count : page.Participants.Count) == 3, what: "rows loaded");
        var name = roster ? "RosterTable" : "DayTable";
        var table = await _app.OnUiAsync(() => _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == name));
        await _app.WaitUntilAsync(() => table.VisibleItems.Count == 3, what: "table rows");
        await _app.SettleAsync();
        await _app.SettleAsync();
        return table;
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
