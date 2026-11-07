using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Tests.Support;
using Xunit.Abstractions;

namespace OrientPyx.Tests.Core.Network;

/// <summary>
/// A station working on a host over the loopback the way an operator did at a real competition — mark one runner
/// «Мандатку пройдено», reload the roster and the day grid — must not leave memory behind on either side: the host of
/// that competition grew to several GB within an hour.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class NetworkMemoryTests : IClassFixture<LoopbackHostFixture>
{
    private const int Runners = 400;
    private const int Rounds = 40;

    private readonly LoopbackHostFixture _f;
    private readonly ITestOutputHelper _output;

    public NetworkMemoryTests(LoopbackHostFixture fixture, ITestOutputHelper output)
    {
        _f = fixture;
        _output = output;
    }

    [Fact]
    public async Task StationEditsAndReloads_DoNotKeepMemory()
    {
        var c = await CompetitionBuilder.CreateAsync(_f.HostServices, TestIds.Next("net_mem"), dayCount: 2);
        for (var g = 0; g < 20; g++)
            await c.AddGroupAsync($"Ч{10 + g * 2}", "31-32-33");
        for (var i = 1; i <= Runners; i++)
            await c.AddRunnerAsync($"Бігун {i:000}", i.ToString(), (100000 + i).ToString(), $"Ч{10 + (i % 20) * 2}");

        var catalog = _f.StationServices.GetRequiredService<IEventCatalogService>();
        var summary = (await catalog.GetEventsAsync()).Single(e => e.Identifier == c.Summary.Identifier);
        var stationStore = _f.StationServices.GetRequiredService<IEventStore>();
        var days = (await stationStore.GetDaysAsync(summary.FolderPath)).OrderBy(d => d.Number).ToList();
        var session = _f.StationServices.GetRequiredService<ISessionService>();
        await session.SelectAsync(summary, days[0]);
        _f.Station.NotifyCurrentEvent(summary.Identifier);
        var editor = _f.StationServices.GetRequiredService<ICompetitionEditorService>();
        var ids = (await editor.GetParticipantRosterAsync()).Select(r => r.ParticipantId).ToList();

        async Task RoundAsync(int i)
        {
            await editor.SetParticipantsCheckedInAsync([ids[i % ids.Count]], i % 2 == 0);
            await editor.GetParticipantRosterAsync();
            await editor.GetParticipantDayRowsAsync();
            await editor.GetDashboardAsync();
        }

        for (var i = 0; i < 5; i++)
            await RoundAsync(i);
        var (managed0, set0) = Measure();

        var watch = Stopwatch.StartNew();
        for (var i = 1; i <= Rounds; i++)
        {
            await RoundAsync(i);
            if (i % 10 == 0)
            {
                var (managed, set) = Measure();
                _output.WriteLine($"after {i} rounds: managed {(managed - managed0) / 1048576.0:+0.0;-0.0} MB, private {(set - set0) / 1048576.0:+0.0;-0.0} MB");
            }
        }
        _output.WriteLine($"{Rounds} rounds in {watch.Elapsed.TotalSeconds:0.0} s");

        var (managedEnd, _) = Measure();
        var perRound = (managedEnd - managed0) / (double)Rounds;
        _output.WriteLine($"per round: {perRound / 1024.0:0} KB managed");
        Assert.True(perRound < 200 * 1024, $"a station round keeps {perRound / 1024.0:0} KB");
    }

    private static (long Managed, long Private) Measure()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        var managed = GC.GetTotalMemory(forceFullCollection: true);
        using var process = Process.GetCurrentProcess();
        return (managed, process.PrivateMemorySize64);
    }
}
