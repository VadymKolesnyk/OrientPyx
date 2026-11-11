using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// The monitor and online pages cache the day's results for their preview. A change made on another page of this
/// same app (a group's discipline switched from relay to a set course on «Групи») is this machine's own write, which
/// the change bus does not replay to other pages — so reopening the page must read the day afresh, not show the
/// cached relay layout.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class ResultPreviewRefreshUiTests
{
    private readonly HeadlessApp _app;

    public ResultPreviewRefreshUiTests(HeadlessApp app) => _app = app;

    private static IServiceProvider Core => TestEnvironment.Instance.Services;

    [Fact]
    public async Task ReopeningThePages_ShowsAGroupsNewDiscipline()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_prevref"), discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("R", "31", s => s.LegCount = 2);
        await c.AddRunnerAsync("Бігун А", "1", "", "R");
        await c.AddRunnerAsync("Бігун Б", "2", "", "R");
        await c.AddRelayTeamAsync("R", 101, "Азимут", 1, ("Бігун А", 1), ("Бігун Б", 2));
        await OpenCompetitionAsync(c.Summary.Identifier);

        // Monitor: a relay group shows its team row.
        var monitor = await OpenMonitorAsync();
        await _app.WaitUntilAsync(() => MonitorNames(monitor).Contains("Азимут"), what: "the monitor preview shows the team");
        // Online: a relay group is not among the personal rows the preview shows.
        var online = await OpenOnlineAsync();
        await _app.SettleAsync();
        Assert.DoesNotContain("Бігун А", OnlineNames(online));

        // «Групи»: R becomes a set course — this app's own write.
        await c.UpdateGroupSettingsAsync("R", 1, s => s.DisciplineOverride = DisciplineId.SetCourse);
        await _app.OnUiAsync(() => _app.Vm.OpenDashboardCommand.Execute(null));
        await _app.SettleAsync();

        monitor = await OpenMonitorAsync();
        await _app.WaitUntilAsync(() => MonitorNames(monitor).Contains("Бігун А") && !MonitorNames(monitor).Contains("Азимут"),
            what: "the reopened monitor preview shows runners, not the team");
        online = await OpenOnlineAsync();
        await _app.WaitUntilAsync(() => OnlineNames(online).Contains("Бігун А"),
            what: "the reopened online preview shows the runners");
    }

    private async Task<MonitorResultsViewModel> OpenMonitorAsync()
    {
        await _app.OnUiAsync(() => _app.Vm.OpenMonitorResultsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<MonitorResultsViewModel>(Shell().SelectedPage);
        if (await _app.OnUiAsync(() => page.Files.Count == 0))
        {
            await _app.OnUiAsync(() => page.AddFileCommand.Execute(null));
            await _app.SettleAsync();
        }
        return page;
    }

    private async Task<OnlineResultsViewModel> OpenOnlineAsync()
    {
        await _app.OnUiAsync(() => _app.Vm.OpenOnlineResultsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        return Assert.IsType<OnlineResultsViewModel>(Shell().SelectedPage);
    }

    // Every cell text of the preview (WaitUntilAsync reads it on the UI thread).
    private static List<string> MonitorNames(MonitorResultsViewModel page) =>
        page.SelectedFile?.Preview.Sections.SelectMany(s => s.Rows).SelectMany(r => r.Values).ToList() ?? [];

    private static List<string> OnlineNames(OnlineResultsViewModel page) =>
        page.Preview.Sections.SelectMany(s => s.Rows).SelectMany(r => r.Values).ToList();

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
}
