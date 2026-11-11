using System.Reflection;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;
using static OrientPyx.Tests.Support.CompetitionBuilder;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// The dashboard's «На дистанції» tile on a one-day competition (a one-day relay, as reported): the participants
/// page, which otherwise always shows the roster on a single day, opens on the day grid with the three «is empty»
/// filters, lists exactly the runners the tile counts, and the next plain open is the roster again.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class DashboardDrillInTests
{
    private readonly HeadlessApp _app;

    public DashboardDrillInTests(HeadlessApp app) => _app = app;

    private static IServiceProvider Core => TestEnvironment.Instance.Services;

    [Fact]
    public async Task OnCourseTileFiltersTheDayGridOnAOneDayRelay()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_drill"), discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("R", "31", s => s.LegCount = 2);
        foreach (var (name, chip) in new[] { ("A", "17"), ("B", "18"), ("C", "19"), ("D", "20") })
            await c.AddRunnerAsync(name, "", chip, "R");
        await c.AddRelayTeamAsync("R", 101, "Азимут", 1, ("A", 1), ("B", 2));
        await c.AddRelayTeamAsync("R", 102, "Полісся", 1, ("C", 1), ("D", 2));
        await c.AddReadoutAsync(1, "17", "31", At(10, 0), At(10, 30));   // A is back
        await OpenCompetitionAsync(c.Summary.Identifier);

        var dashboard = Field<DashboardViewModel>("_dashboard");
        await _app.OnUiAsync(() => dashboard.LoadAsync());
        var onCourse = dashboard.Info.OnCourse;
        Assert.InRange(onCourse, 1, 3);

        await _app.OnUiAsync(() => dashboard.QuickActionCommand.Execute(DashboardQuickAction.ParticipantsOnCourse));
        await _app.WaitUntilAsync(() => Field<ShellViewModel>("_shell").SelectedPage is ParticipantsViewModel,
            what: "participants page opened");
        var page = (ParticipantsViewModel)Field<ShellViewModel>("_shell").SelectedPage!;
        await _app.SettleAsync();
        Assert.True(page.IsDayMode, "a drill-in opens the day grid even on a one-day competition");

        var table = await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == "DayTable"));
        await _app.WaitUntilAsync(() => table.ActiveFilters.Count == 3, what: "three on-course filters");
        await _app.WaitUntilAsync(() => table.VisibleItems.Count == onCourse, what: $"{onCourse} rows on course");
        Assert.DoesNotContain(table.VisibleItems, r => ((ParticipantDayRowViewModel)r!).FullName == "A");
        await _app.ScreenshotAsync("dashboard-on-course-drill-in");

        // A plain open (sidebar) is the one-day roster again.
        await _app.OnUiAsync(() => _app.Vm.OpenDashboardCommand.Execute(null));
        await _app.SettleAsync();
        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        Assert.True(page.IsRosterMode, "a plain open of a one-day competition shows the roster");

        AssertNoUiErrors();
    }

    private T Field<T>(string name)
    {
        var field = typeof(MainWindowViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (T)field.GetValue(_app.Vm)!;
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

    private void AssertNoUiErrors()
    {
        if (_app.UiErrors.Count == 0)
            return;
        var text = string.Join(Environment.NewLine, _app.UiErrors.Select(e => e.ToString()));
        _app.UiErrors.Clear();
        Assert.Fail("The UI thread reported an exception:" + Environment.NewLine + text);
    }
}
