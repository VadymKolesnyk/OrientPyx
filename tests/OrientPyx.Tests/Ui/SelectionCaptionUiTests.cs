using System.Reflection;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// «Учасники»: the selected row shows on the left of the table's status bar — the bib and / or the relay leg number,
/// then the name — in the day table and in «Мандатка», follows the selection and an edit of the row, and is blank
/// with nothing selected.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class SelectionCaptionUiTests
{
    private readonly HeadlessApp _app;

    public SelectionCaptionUiTests(HeadlessApp app) => _app = app;

    private static IServiceProvider Core => TestEnvironment.Instance.Services;

    [Fact]
    public async Task TheSelectedRowShowsInTheStatusBar()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_caption"), dayCount: 2, discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("R", "31", s => s.LegCount = 3);
        await c.AddRunnerAsync("Іваненко Іван", "17", "", "R");
        await c.AddRunnerAsync("Петренко Петро", "", "", "R");
        await c.AddRunnerAsync("Сидоренко Сидір", "25", "", "R");
        await c.AddRelayTeamAsync("R", 101, "Азимут", 1, ("Іваненко Іван", 1), ("Петренко Петро", 2));
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);

        // The day table: bib / leg number — name; a runner with only a leg number or only a bib shows just that one.
        await _app.OnUiAsync(() => page.SelectedDay = page.DayOptions.First(d => !d.IsRoster && d.Day?.Number == 1));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => page.IsDayMode && page.Participants.Count == 3, what: "three day rows");
        var day = await TableAsync("DayTable");
        ParticipantDayRowViewModel Row(string name) => page.Participants.ToList().First(r => r.FullName == name);

        await _app.OnUiAsync(() => page.SelectedParticipant = Row("Іваненко Іван"));
        await _app.WaitUntilAsync(() => CaptionOf(day) == "17 / 101.1 — Іваненко Іван", what: "bib + leg number caption");
        await _app.OnUiAsync(() => page.SelectedParticipant = Row("Петренко Петро"));
        await _app.WaitUntilAsync(() => CaptionOf(day) == "101.2 — Петренко Петро", what: "leg-number-only caption");
        await _app.OnUiAsync(() => page.SelectedParticipant = Row("Сидоренко Сидір"));
        await _app.WaitUntilAsync(() => CaptionOf(day) == "25 — Сидоренко Сидір", what: "bib-only caption");
        await _app.ScreenshotAsync("selection-caption-day");

        // An edit of the selected row shows at once.
        await _app.OnUiAsync(() => Row("Сидоренко Сидір").Number = "26");
        await _app.WaitUntilAsync(() => CaptionOf(day) == "26 — Сидоренко Сидір", what: "caption follows the edit");

        // Nothing selected ⇒ nothing shown.
        await _app.OnUiAsync(() => page.SelectedParticipant = null);
        await _app.WaitUntilAsync(() => CaptionOf(day) is null, what: "caption hidden");

        // «Мандатка»: the merged relay number over the relay days («Д1: 101.1» — day 2 has no team).
        await _app.OnUiAsync(() => page.SelectedDay = page.DayOptions.First(d => d.IsRoster));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => page.IsRosterMode && page.Roster.Count == 3, what: "three roster rows");
        var roster = await TableAsync("RosterTable");
        var ivan = page.Roster.ToList().First(r => r.FullName == "Іваненко Іван");
        await _app.OnUiAsync(() => page.SelectedRosterRow = ivan);
        await _app.WaitUntilAsync(() => CaptionOf(roster) == $"17 / {ivan.CollapsedRelayBib} — Іваненко Іван"
            && ivan.CollapsedRelayBib.Contains("101.1"), what: "roster caption");
        await _app.ScreenshotAsync("selection-caption-roster");

        AssertNoUiErrors();
    }

    private async Task<SheetTable> TableAsync(string name) => await _app.OnUiAsync(() =>
        _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == name));

    // The status bar's selected-row text, or null while it is hidden (read on the UI thread: WaitUntilAsync's condition).
    private static string? CaptionOf(SheetTable table)
    {
        var text = table.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_StatusRow");
        return text.IsVisible ? text.Text : null;
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
