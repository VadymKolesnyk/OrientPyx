using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// An edit on one runner that moves the ranking — «Бонус», «П/К», the status — re-numbers the places of EVERY runner of
/// the group at once, in the day grid and in «Мандатка», also when several edits follow each other faster than the
/// recompute (each one recomputes the day; an older recompute finishing late must not land over a newer one).
/// </summary>
[Collection(AppCollection.Name)]
public sealed class LiveResultsRefreshUiTests
{
    private readonly HeadlessApp _app;

    public LiveResultsRefreshUiTests(HeadlessApp app) => _app = app;

    private static IServiceProvider Core => TestEnvironment.Instance.Services;

    private static TimeSpan T(int h, int m, int s = 0) => CompetitionBuilder.At(h, m, s);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BonusRenumbersTheWholeGroup_EvenForEditsInQuickSuccession(bool roster)
    {
        var (page, c) = await OpenAsync("ui_livebonus", roster);
        Assert.Equal(("1", "2", "3"), await PlacesAsync(page, c));

        // C (6 points) + 4 = 10: first, the two nines move down.
        await _app.OnUiAsync(() => SetBonus(page, c, "C", "4"));
        await WaitPlacesAsync(page, c, ("2", "3", "1"), "C's bonus re-ranks A and B");

        // Two edits at once (typing down the column, a paste): both recomputes race, the last state must win.
        await _app.OnUiAsync(() =>
        {
            SetBonus(page, c, "A", "5");   // 14
            SetBonus(page, c, "B", "6");   // 15
        });
        await WaitPlacesAsync(page, c, ("2", "1", "3"), "both bonuses shown");
        await _app.SettleAsync();
        await _app.SettleAsync();
        Assert.Equal(("2", "1", "3"), await PlacesAsync(page, c));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutOfCompetitionAndStatusRenumberTheOthers(bool roster)
    {
        var (page, c) = await OpenAsync("ui_liveooc", roster);
        Assert.Equal(("1", "2", "3"), await PlacesAsync(page, c));

        // «П/К» on the leader: no place for A, B and C move up (the day grid saves it with the debounced row save).
        await _app.OnUiAsync(() =>
        {
            if (roster)
                RosterCell(page, c, "A").OutOfCompetition = true;
            else
                DayRow(page, "A").OutOfCompetition = true;
        });
        await WaitPlacesAsync(page, c, ("П/К", "1", "2"), "A out of competition");

        // A disqualified runner leaves the ranking too.
        await _app.OnUiAsync(() =>
        {
            if (roster)
            {
                var cell = RosterCell(page, c, "B");
                cell.SelectedStatus = cell.StatusOptions.First(o => o.Status == FinishStatus.Dsq);
            }
            else
            {
                var row = DayRow(page, "B");
                row.SelectedStatus = row.StatusOptions.First(o => o.Status == FinishStatus.Dsq);
            }
        });
        await WaitPlacesAsync(page, c, ("П/К", "", "1"), "B disqualified");
    }

    // A score-by-time day: A and B take all three controls (9 points; A faster), C two (6 points).
    private async Task<(ParticipantsViewModel Page, CompetitionBuilder C)> OpenAsync(string id, bool roster)
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next(id), dayCount: 2, discipline: DisciplineId.ScoreByTime);
        var store = Core.GetRequiredService<IEventStore>();
        foreach (var cp in await c.AddControlsAsync(1, "31", "32", "33"))
        {
            cp.Points = 3;
            await store.UpdateControlPointAsync(c.Folder, cp);
        }
        await c.AddGroupAsync("Ч21", "31 32 33", s => s.TimeLimitSeconds = 60 * 60);
        await c.AddRunnerAsync("A", "1", "1001", "Ч21", T(10, 0), days: [1]);
        await c.AddRunnerAsync("B", "2", "1002", "Ч21", T(10, 0), days: [1]);
        await c.AddRunnerAsync("C", "3", "1003", "Ч21", T(10, 0), days: [1]);
        await c.AddReadoutAsync(1, "1001", "31 32 33", T(10, 0), T(10, 20));
        await c.AddReadoutAsync(1, "1002", "31 32 33", T(10, 0), T(10, 25));
        await c.AddReadoutAsync(1, "1003", "31 32", T(10, 0), T(10, 15));

        await OpenCompetitionAsync(c.Summary.Identifier);
        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        await _app.OnUiAsync(() => page.SelectedDay = page.DayOptions.First(d => d.IsRoster == roster && (roster || d.Number == 1)));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => roster
                ? page.IsRosterMode && page.Roster.Count == 3
                : page.IsDayMode && page.Participants.Count == 3 && page.Participants.All(r => r.DayId == c.Day(1).Id),
            what: "the three runners shown");
        await _app.SettleAsync();
        return (page, c);
    }

    private static void SetBonus(ParticipantsViewModel page, CompetitionBuilder c, string runner, string bonus)
    {
        if (page.IsRosterMode)
            RosterCell(page, c, runner).BonusText = bonus;
        else
            DayRow(page, runner).BonusText = bonus;
    }

    private static ParticipantDayRowViewModel DayRow(ParticipantsViewModel page, string runner)
        => page.Participants.Single(r => r.FullName == runner);

    private static RosterDayCellViewModel RosterCell(ParticipantsViewModel page, CompetitionBuilder c, string runner)
        => page.Roster.Single(r => r.FullName == runner).Days.Single(d => d.DayId == c.Day(1).Id);

    private static (string A, string B, string C) Places(ParticipantsViewModel page, CompetitionBuilder c)
    {
        string Of(string runner) => page.IsRosterMode ? RosterCell(page, c, runner).PlaceText : DayRow(page, runner).PlaceText;
        return (Of("A"), Of("B"), Of("C"));
    }

    private Task<(string, string, string)> PlacesAsync(ParticipantsViewModel page, CompetitionBuilder c)
        => _app.OnUiAsync(() => Places(page, c));

    private async Task WaitPlacesAsync(ParticipantsViewModel page, CompetitionBuilder c, (string, string, string) expected, string what)
    {
        try
        {
            await _app.WaitUntilAsync(() => Places(page, c) == expected, TimeSpan.FromSeconds(10), what);
        }
        catch
        {
            Assert.Equal(expected, await PlacesAsync(page, c)); // says what is shown instead
            throw;
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
}
