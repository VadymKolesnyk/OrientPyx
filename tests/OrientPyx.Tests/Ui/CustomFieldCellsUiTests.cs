using System.Reflection;
using Avalonia.Controls;
using Avalonia.Media;
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
/// Additional-field cells on the roster show their stored value while resting (before the cell is entered): the
/// label carries the value and a visible brush — a valid value in the normal text colour, a value outside the
/// choice list in red.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class CustomFieldCellsUiTests
{
    private readonly HeadlessApp _app;

    public CustomFieldCellsUiTests(HeadlessApp app) => _app = app;

    private static IServiceProvider Core => TestEnvironment.Instance.Services;
    private static IEventStore Store => Core.GetRequiredService<IEventStore>();

    [Fact]
    public async Task RestingCellsShowTheStoredValues()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_cf"));
        await c.AddGroupAsync("Ч21", "31");
        var first = await c.AddRunnerAsync("Бігун 01", "1", "", "Ч21");
        var second = await c.AddRunnerAsync("Бігун 02", "2", "", "Ч21");
        var choice = new CustomField("stat", "Стать", CustomFieldKind.Choice, [new ChoiceOption("Ч"), new ChoiceOption("Ж")],
            CustomField.UserSource, 0);
        var text = new CustomField("klas", "Клас", CustomFieldKind.Text, [], CustomField.UserSource, 1);
        Assert.True(await Store.AddCustomFieldDefAsync(c.Folder, choice.ToDef()));
        Assert.True(await Store.AddCustomFieldDefAsync(c.Folder, text.ToDef()));
        await Store.SetParticipantFieldValueAsync(c.Folder, first.Id, "stat", "Ч");
        await Store.SetParticipantFieldValueAsync(c.Folder, second.Id, "stat", "X"); // outside the list → red
        await Store.SetParticipantFieldValueAsync(c.Folder, first.Id, "klas", "10-А");
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var participants = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        if (!participants.IsRosterMode)
        {
            await _app.OnUiAsync(() => participants.SelectedDay = participants.DayOptions.First(d => d.IsRoster));
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => participants.IsRosterMode && participants.Roster.Count == 2, what: "two roster rows");
        var table = await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == "RosterTable"));
        await _app.WaitUntilAsync(() => table.VisibleItems.Count == 2, what: "roster table shows the rows");
        await _app.SettleAsync();
        await _app.SettleAsync();
        await _app.ScreenshotAsync("custom-fields-10-resting");

        var rows = await _app.OnUiAsync(() => table.VisibleItems.Cast<ParticipantRosterRowViewModel>().OrderBy(r => r.Number).ToList());

        var (chText, chColor) = await _app.OnUiAsync(() => RestingLabel(table, rows[0], "field.stat"));
        Assert.Equal("Ч", chText);
        AssertVisible(chColor);
        Assert.NotEqual(Danger, chColor);

        var (badText, badColor) = await _app.OnUiAsync(() => RestingLabel(table, rows[1], "field.stat"));
        Assert.Equal("X", badText);
        Assert.Equal(Danger, badColor);

        var (klasText, klasColor) = await _app.OnUiAsync(() => RestingLabel(table, rows[0], "field.klas"));
        Assert.Equal("10-А", klasText);
        AssertVisible(klasColor);
        AssertNoUiErrors();
    }

    private static readonly Color Danger = Color.Parse("#DC2626");

    private static void AssertVisible(Color? color)
    {
        Assert.NotNull(color); // a null brush draws nothing — the bug this test guards
        Assert.True(color.Value.A > 0, $"the label's colour {color} is transparent");
    }

    // The text and colour of the resting label (the cell's TextBlock) of a row's column; null colour = no brush.
    private static (string? Text, Color? Color) RestingLabel(SheetTable table, object row, string columnKey)
    {
        foreach (var d in table.GetVisualDescendants())
        {
            if (d.GetType().Name != "SheetCell" || d is not Control cell || !ReferenceEquals(cell.DataContext, row))
                continue;
            var column = d.GetType().GetProperty("Column")!.GetValue(d)!;
            if (column.GetType().GetProperty("Key")!.GetValue(column) as string != columnKey)
                continue;
            var label = cell.GetVisualDescendants().OfType<TextBlock>().First();
            return (label.Text, label.Foreground is ISolidColorBrush solid && solid.Opacity > 0 ? solid.Color : null);
        }
        throw new InvalidOperationException($"no realized {columnKey} cell");
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
