using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// The split slip prints the runner's current place in the group out of everyone of the group who has read out
/// (withdrawn runners included). The first slip counts the read-outs logged up to this one; a re-printed slip
/// counts every read-out logged so far, later ones too.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class SplitPrintPlaceTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static TimeSpan T(int h, int m, int s = 0) => new(h, m, s);

    [Fact]
    public async Task SlipPlaceCountsEveryoneReadOut()
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
        await c.AddRunnerAsync("Late", "6", "1006", "Ч21", T(10, 20));

        // Read-out order: First (30:00), Second (32:00), Broken (MP), Fast (25:00) — the fastest reads out last
        // of the four; a runner of another group never counts. Late reads out last of all with 40:00.
        var first = await c.AddReadoutAsync(1, "1001", "31 32", T(10, 0), T(10, 30));
        var second = await c.AddReadoutAsync(1, "1002", "31 32", T(10, 1), T(10, 33));
        var broken = await c.AddReadoutAsync(1, "1004", "31", T(10, 3), T(10, 20));
        var fast = await c.AddReadoutAsync(1, "1003", "31 32", T(10, 10), T(10, 35));
        await c.AddReadoutAsync(1, "1005", "31 32", T(10, 0), T(10, 10));
        await c.AddReadoutAsync(1, "1006", "31 32", T(10, 20), T(11, 0));
        await c.SelectAsync();

        var editor = Sp.GetRequiredService<ICompetitionEditorService>();

        // When First read out nobody else had: 1st of 1 — although Fast later pushes them to 2nd.
        var firstSlip = await editor.GetSplitPrintDocumentAsync(first.Id);
        Assert.NotNull(firstSlip);
        Assert.Equal(1, firstSlip.Place);
        Assert.Equal(1, firstSlip.ReadOutCount);

        // First 30:00 and Second 32:00 had read out → 2nd of 2.
        var secondSlip = await editor.GetSplitPrintDocumentAsync(second.Id);
        Assert.NotNull(secondSlip);
        Assert.Equal(2, secondSlip.Place);
        Assert.Equal(2, secondSlip.ReadOutCount);

        // An MP result has no place line at all.
        var brokenSlip = await editor.GetSplitPrintDocumentAsync(broken.Id);
        Assert.NotNull(brokenSlip);
        Assert.Null(brokenSlip.Place);
        Assert.Null(brokenSlip.ReadOutCount);

        // Fast 25:00 leads; the withdrawn Broken read out before them, so counts in the "з N" → 1st of 4.
        var fastSlip = await editor.GetSplitPrintDocumentAsync(fast.Id);
        Assert.NotNull(fastSlip);
        Assert.Equal(1, fastSlip.Place);
        Assert.Equal(4, fastSlip.ReadOutCount);

        // Printed again later: everyone read out so far counts, Late included → First is 2nd of 5.
        var firstReprint = await editor.GetSplitPrintDocumentAsync(first.Id, reprint: true);
        Assert.NotNull(firstReprint);
        Assert.Equal(2, firstReprint.Place);
        Assert.Equal(5, firstReprint.ReadOutCount);

        var secondReprint = await editor.GetSplitPrintDocumentAsync(second.Id, reprint: true);
        Assert.NotNull(secondReprint);
        Assert.Equal(3, secondReprint.Place);
        Assert.Equal(5, secondReprint.ReadOutCount);

        // The re-print agrees with the final standing the log shows.
        var results = await c.ResultsAsync();
        Assert.Equal(2, results["First"].Place);
    }
}
