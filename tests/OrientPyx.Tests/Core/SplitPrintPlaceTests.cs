using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// The split slip prints the runner's place in the group <b>at the moment of the finish</b>: later finishers
/// are not counted, so a re-printed slip shows what the runner saw at the finish, not the final standing.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class SplitPrintPlaceTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static TimeSpan T(int h, int m, int s = 0) => new(h, m, s);

    [Fact]
    public async Task SlipPlaceCountsOnlyThoseFinishedByThen()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("slip"));
        await c.AddControlsAsync(1, "31", "32");
        await c.AddGroupAsync("Ч21", "31 32");
        await c.AddGroupAsync("Ж21", "31 32");
        await c.AddRunnerAsync("First", "1", "1001", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Second", "2", "1002", "Ч21", T(10, 1));
        await c.AddRunnerAsync("Fast", "3", "1003", "Ч21", T(10, 10));
        await c.AddRunnerAsync("Broken", "4", "1004", "Ч21", T(10, 3));
        await c.AddRunnerAsync("Other", "5", "1005", "Ж21", T(10, 0));

        // Finish order: First 10:30 (30:00), Second 10:33 (32:00), Fast 10:35 (25:00) — the fastest finishes
        // last; Broken (MP) and a runner of another group never take a place.
        var first = await c.AddReadoutAsync(1, "1001", "31 32", T(10, 0), T(10, 30));
        var second = await c.AddReadoutAsync(1, "1002", "31 32", T(10, 1), T(10, 33));
        var fast = await c.AddReadoutAsync(1, "1003", "31 32", T(10, 10), T(10, 35));
        var broken = await c.AddReadoutAsync(1, "1004", "31", T(10, 3), T(10, 20));
        await c.AddReadoutAsync(1, "1005", "31 32", T(10, 0), T(10, 10));
        await c.SelectAsync();

        var editor = Sp.GetRequiredService<ICompetitionEditorService>();

        // At 10:30 only First had finished: 1st of 1 — although Fast later pushes them to 2nd.
        var firstSlip = await editor.GetSplitPrintDocumentAsync(first.Id);
        Assert.NotNull(firstSlip);
        Assert.Equal(1, firstSlip.Place);
        Assert.Equal(1, firstSlip.PlacedCount);

        // At 10:33 First 30:00 and Second 32:00 had finished → 2nd of 2.
        var secondSlip = await editor.GetSplitPrintDocumentAsync(second.Id);
        Assert.NotNull(secondSlip);
        Assert.Equal(2, secondSlip.Place);
        Assert.Equal(2, secondSlip.PlacedCount);

        // At 10:35 all three had finished: Fast 25:00 leads → 1st of 3.
        var fastSlip = await editor.GetSplitPrintDocumentAsync(fast.Id);
        Assert.NotNull(fastSlip);
        Assert.Equal(1, fastSlip.Place);
        Assert.Equal(3, fastSlip.PlacedCount);

        // An MP result has no place line at all.
        var brokenSlip = await editor.GetSplitPrintDocumentAsync(broken.Id);
        Assert.NotNull(brokenSlip);
        Assert.Null(brokenSlip.Place);
        Assert.Null(brokenSlip.PlacedCount);

        // The final standing (what the log shows) differs from the slip of the first finisher.
        var results = await c.ResultsAsync();
        Assert.Equal(2, results["First"].Place);
    }
}
