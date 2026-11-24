using System.Reflection;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;
using static OrientPyx.Tests.Support.CompetitionBuilder;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// «Перевірка готовності дня» in the app: the dashboard tile opens the list (one row per problem), and «Перейти» opens
/// the page the problem is fixed on with its row selected — the participant on the day grid of a one-day competition
/// (which otherwise shows the roster), shown even when a filter hid it; the group on «Групи».
/// </summary>
[Collection(AppCollection.Name)]
public sealed class DayReadinessUiTests
{
    private readonly HeadlessApp _app;

    public DayReadinessUiTests(HeadlessApp app) => _app = app;

    private static IServiceProvider Core => TestEnvironment.Instance.Services;

    private ShellViewModel Shell => Field<ShellViewModel>("_shell");

    [Fact]
    public async Task TileOpensTheList_AndGoToShowsTheRow()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_ready"));
        await c.AddControlsAsync(1, "31", "32");
        await c.AddGroupAsync("Ч21", "31 32");
        await c.AddGroupAsync("Порожня", "");
        await c.AddRunnerAsync("Без чіпа", "1", "", "Ч21", At(10, 0));
        await c.AddRunnerAsync("Сім-1", "7", "8100002", "Ч21", At(10, 1));
        await c.AddRunnerAsync("Сім-2", "7", "8100003", "Ч21", At(10, 2));
        await c.AddRunnerAsync("Без дистанції", "4", "8100004", "Порожня", At(10, 3));
        await OpenCompetitionAsync(c.Summary.Identifier);

        // The tile counts the problems and opens the list.
        var dashboard = Field<DashboardViewModel>("_dashboard");
        await _app.OnUiAsync(() => dashboard.LoadAsync());
        // What the core reads for the same day (its own session, pointed at this competition).
        await c.SelectAsync();
        var expected = await Core.GetRequiredService<ICompetitionEditorService>().GetDayReadinessAsync(c.Day(1).Id);
        Assert.NotEmpty(expected.Issues);
        Assert.Equal(expected.Errors, dashboard.Info.ReadinessErrors);
        Assert.Equal(expected.Warnings, dashboard.Info.ReadinessWarnings);
        Assert.True(dashboard.ReadinessHasErrors);

        await _app.OnUiAsync(() => dashboard.QuickActionCommand.Execute(DashboardQuickAction.DayReadiness));
        await _app.WaitUntilAsync(() => Shell.SelectedPage is DayReadinessViewModel, what: "readiness page opened");
        var readiness = (DayReadinessViewModel)Shell.SelectedPage!;
        await _app.SettleAsync();
        Assert.Equal(expected.Issues.Count, readiness.Rows.Count);
        Assert.True(readiness.HasErrors);
        await _app.ScreenshotAsync("day-readiness");

        // «Перейти» to a group: «Групи» with that group selected.
        var groupRow = readiness.Rows.Single(r => r.Issue.Kind == ReadinessKind.GroupWithoutCourse);
        await _app.OnUiAsync(() => readiness.GoToCommand.Execute(groupRow));
        await _app.WaitUntilAsync(() => Shell.SelectedPage is GroupsViewModel, what: "groups page opened");
        var groups = (GroupsViewModel)Shell.SelectedPage!;
        await _app.WaitUntilAsync(() => groups.SelectedGroup?.Id == groupRow.Issue.GroupSettingsId, what: "the group selected");

        // A participant hidden by a filter: the day grid opens with only the chip-less runner shown…
        await _app.OnUiAsync(() => _app.Vm.OpenDashboardCommand.Execute(null));
        await _app.SettleAsync();
        await _app.OnUiAsync(() => dashboard.QuickActionCommand.Execute(DashboardQuickAction.ParticipantsWithoutChip));
        await _app.WaitUntilAsync(() => Shell.SelectedPage is ParticipantsViewModel, what: "participants page opened");
        var participants = (ParticipantsViewModel)Shell.SelectedPage!;
        await _app.SettleAsync();
        var table = await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == "DayTable"));
        await _app.WaitUntilAsync(() => table.ActiveFilters.Count == 1 && table.VisibleItems.Count == 1, what: "chip filter on");

        // …and «Перейти» to a runner with a chip (a duplicate number) clears it and selects that runner.
        var numberIssue = expected.Issues.First(i => i.Kind == ReadinessKind.DuplicateNumber);
        await _app.OnUiAsync(() => GoToAsync(numberIssue));
        await _app.WaitUntilAsync(() => participants.SelectedParticipant?.Id == numberIssue.LinkId, what: "the runner selected");
        Assert.True(participants.IsDayMode, "a participant's problem shows on the day grid");
        await _app.WaitUntilAsync(() => table.ActiveFilters.Count == 0, what: "the filter hiding the row cleared");
        Assert.Contains(table.VisibleItems, r => ((ParticipantDayRowViewModel)r!).Id == numberIssue.LinkId);

        // Opened from the menu, the list follows a fix made elsewhere on this machine.
        await c.UpdateGroupSettingsAsync("Порожня", 1, s => s.CourseOrder = "31 32");
        await _app.OnUiAsync(() => _app.Vm.OpenDayReadinessCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        Assert.DoesNotContain(readiness.Rows, r => r.Issue.Kind == ReadinessKind.GroupWithoutCourse);

        AssertNoUiErrors();
    }

    // The route «Перейти» takes (the readiness page raises it; here run while the participants page is the shown one).
    private Task GoToAsync(ReadinessIssue issue)
    {
        var method = typeof(MainWindowViewModel).GetMethod("GoToReadinessIssueAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(_app.Vm, [issue])!;
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
