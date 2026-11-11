using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// «Старт → Масовий старт»: one start per group written onto every runner; a relay group's leg 1 gets it and the
/// group's mass start and restarts are replaced, later legs keep starting at the handoff or the restart.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class MassStartTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static ICompetitionEditorService Editor => Sp.GetRequiredService<ICompetitionEditorService>();

    private static TimeSpan T(int h, int m, int s = 0) => new(h, m, s);

    private static async Task<CompetitionBuilder> BuildAsync()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("massstart"));
        await c.AddControlsAsync(1, "31", "32", "33");
        await c.AddGroupAsync("Ч21", "31 32 33");
        await c.AddGroupAsync("Ж21", "31 32 33");
        await c.AddGroupAsync("R", "31 32 33", s => { s.DisciplineOverride = DisciplineId.Relay; s.LegCount = 3; });

        await c.AddRunnerAsync("Ч-1", "1", "901", "Ч21", start: T(10, 1));
        await c.AddRunnerAsync("Ч-2", "2", "902", "Ч21", start: T(10, 2));
        await c.AddRunnerAsync("Ж-1", "3", "903", "Ж21", start: T(10, 3));

        foreach (var (name, chip) in new[] { ("A-1", "1011"), ("A-2", "1012"), ("A-3", "1013"), ("B-1", "1021"), ("B-2", "1022"), ("B-3", "1023") })
            await c.AddRunnerAsync(name, "", chip, "R");
        await c.AddRelayTeamAsync("R", 101, "A", 1, ("A-1", 1), ("A-2", 2), ("A-3", 3));
        await c.AddRelayTeamAsync("R", 102, "B", 1, ("B-1", 1), ("B-2", 2), ("B-3", 3));
        await c.UpdateLinkAsync("A-1", 1, l => l.StartTime = T(10, 5));   // a minute from an earlier interval draw
        await c.SelectAsync();
        return c;
    }

    [Fact]
    public async Task DataShowsEachGroupsCurrentStartAndRelayLegs()
    {
        var c = await BuildAsync();
        await c.SetRelaySettingsAsync("R", 1, 3, T(9, 30), """{"2":"10:40:00"}""");

        var data = await Editor.GetMassStartDataAsync(c.Day(1).Id);
        var men = data.Groups.Single(g => g.Name == "Ч21");
        Assert.False(men.IsRelay);
        Assert.Equal(2, men.MemberCount);
        Assert.True(men.MixedStarts);
        Assert.Null(men.CurrentStart);
        Assert.Equal(T(10, 3), data.Groups.Single(g => g.Name == "Ж21").CurrentStart);

        var relay = data.Groups.Single(g => g.Name == "R");
        Assert.True(relay.IsRelay);
        Assert.Equal(2, relay.MemberCount);          // teams
        Assert.Equal(3, relay.LegCount);
        Assert.True(relay.MixedStarts);              // A-1 starts 10:05, B-1 at the group's mass start 9:30
        Assert.Equal(T(9, 30), relay.MassStartTime);
        Assert.Equal(T(10, 40), relay.RestartTimes[2]);
    }

    [Fact]
    public async Task ApplyWritesTheStartOntoEveryRunnerOfTheChosenGroupsOnly()
    {
        var c = await BuildAsync();

        var result = await Editor.ApplyMassStartAsync(c.Day(1).Id,
            [new MassStartAssignment(c.Groups["Ч21"].Id, T(10, 30))]);

        Assert.Equal(new MassStartResult(2, 0), result);
        Assert.Equal(T(10, 30), (await c.GetLinkAsync("Ч-1", 1)).StartTime);
        Assert.Equal(T(10, 30), (await c.GetLinkAsync("Ч-2", 1)).StartTime);
        Assert.Equal(T(10, 3), (await c.GetLinkAsync("Ж-1", 1)).StartTime);   // not chosen — untouched

        var men = (await Editor.GetMassStartDataAsync(c.Day(1).Id)).Groups.Single(g => g.Name == "Ч21");
        Assert.False(men.MixedStarts);
        Assert.Equal(T(10, 30), men.CurrentStart);

        await c.AddReadoutAsync(1, "901", "31 32 33", null, T(11, 0));
        var r = await c.ResultsAsync();
        Assert.Equal(T(0, 30), r["Ч-1"].ResultTime);
    }

    [Fact]
    public async Task ApplyOnARelayStartsLegOneAndReplacesTheRestarts()
    {
        var c = await BuildAsync();
        await c.SetRelaySettingsAsync("R", 1, 3, T(9, 30), """{"2":"10:40:00","3":"11:10:00"}""");

        var result = await Editor.ApplyMassStartAsync(c.Day(1).Id,
            [new MassStartAssignment(c.Groups["R"].Id, T(10, 0), new Dictionary<int, TimeSpan>
            {
                [2] = T(11, 0),
                [4] = T(12, 0),     // no such leg — dropped
            })]);

        Assert.Equal(new MassStartResult(2, 1), result);
        Assert.Equal(T(10, 0), (await c.GetLinkAsync("A-1", 1)).StartTime);   // the earlier drawn minute is replaced
        Assert.Equal(T(10, 0), (await c.GetLinkAsync("B-1", 1)).StartTime);
        Assert.Null((await c.GetLinkAsync("A-2", 1)).StartTime);              // later legs start at the handoff

        var settings = await c.GetGroupSettingsAsync("R", 1);
        Assert.Equal(T(10, 0), settings.MassStartTime);
        Assert.Equal(3, settings.LegCount);
        var restarts = RelayRestartTimes.Parse(settings.RestartTimesJson);
        Assert.Equal([2], restarts.Keys.ToList());                           // leg 3's restart is cleared
        Assert.Equal(T(11, 0), restarts[2]);

        // A: legs hand over; B: leg 1 never finishes, so leg 2 runs from the 11:00 restart.
        await c.AddReadoutAsync(1, "1011", "31 32 33", null, T(10, 20));
        await c.AddReadoutAsync(1, "1012", "31 32 33", null, T(10, 50));
        await c.AddReadoutAsync(1, "1022", "31 32 33", null, T(11, 25));
        var r = await c.ResultsAsync();
        Assert.Equal(T(0, 20), r["A-1"].ResultTime);
        Assert.Equal(T(0, 30), r["A-2"].ResultTime);
        Assert.Equal(T(0, 25), r["B-2"].ResultTime);
        Assert.True(r["B-2"].Leg!.Restarted);
    }

    [Fact]
    public async Task ApplyIsRefusedOnAClosedDay()
    {
        var c = await BuildAsync();
        await Editor.SetDayLockedAsync(c.Day(1).Id, true);

        await Assert.ThrowsAsync<DayLockedException>(() => Editor.ApplyMassStartAsync(c.Day(1).Id,
            [new MassStartAssignment(c.Groups["Ч21"].Id, T(10, 30))]));
        Assert.Equal(T(10, 1), (await c.GetLinkAsync("Ч-1", 1)).StartTime);
    }
}
