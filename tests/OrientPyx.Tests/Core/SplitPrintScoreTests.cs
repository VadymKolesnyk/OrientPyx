using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// On a score course («за вибором») the split slip prints the punches in the order the runner took them — each with
/// its leg time, distance and pace, the scoring ones with their points — and never the controls the runner skipped.
/// The splits panel keeps its list of every allowed control.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class SplitPrintScoreTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static TimeSpan T(int h, int m, int s = 0) => new(h, m, s);

    [Fact]
    public async Task ScoreByTimeSlipPrintsThePassageInOrderTaken()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("slipsbt"), discipline: DisciplineId.ScoreByTime);
        var cps = await c.AddControlsAsync(1, "31", "32", "33", "34", "99");
        var store = Sp.GetRequiredService<IEventStore>();
        foreach (var cp in cps.Where(cp => cp.Code != "99"))
        {
            cp.Points = int.Parse(cp.Code) / 10; // 3 points each
            await store.UpdateControlPointAsync(c.Folder, cp);
        }
        await c.AddGroupAsync("Ч21", "31 32 33 34");
        await c.AddRunnerAsync("Runner", "1", "1001", "Ч21", T(10, 0));
        // 33 then 31, a foreign 99 and 31 again; 32 and 34 skipped. Punches every 5 minutes.
        var readout = await c.AddReadoutAsync(1, "1001", "33 31 99 31", T(10, 0), T(10, 25));
        await c.SelectAsync();

        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var slip = await editor.GetSplitPrintDocumentAsync(readout.Id);

        Assert.NotNull(slip);
        Assert.Equal(["33", "31", "99", "31", "F"], slip.Rows.Select(r => r.Code));
        Assert.Equal(["1", "2", "3", "4", ""], slip.Rows.Select(r => r.Index));
        Assert.True(slip.HasPoints);
        Assert.True(slip.HasGeometry);   // the passage table with a бал column, not the compact one
        Assert.Equal("6", slip.TotalPointsText);

        // The scoring punches carry their points; the foreign and the repeated punch score nothing.
        Assert.Equal(["+3", "+3", null, null, null], slip.Rows.Select(r => r.PointsText));
        Assert.Equal([true, true, false, false, true], slip.Rows.Select(r => r.OnCourse));

        // Every row has its leg time; a leg between two mapped controls has a distance and a pace.
        Assert.All(slip.Rows, r => Assert.Equal("5:00", r.LegText));
        var second = slip.Rows[1];
        Assert.NotEqual(string.Empty, second.DistanceText);
        Assert.NotEqual(string.Empty, second.PaceText);
        Assert.NotEqual(string.Empty, slip.TotalDistanceText);
        Assert.Empty(slip.CorrectOrder);

        // The panel still lists every allowed control, the skipped ones greyed.
        var view = await editor.GetFinishSplitsAsync(readout.Id);
        Assert.NotNull(view);
        Assert.Equal(["33", "31", "32", "34"], view.Entries.Select(e => e.Code));
        Assert.Equal([true, true, false, false], view.Entries.Select(e => e.Visited));
        Assert.Equal(6, view.TotalPoints);
    }

    [Fact]
    public async Task ScoreByCountSlipPrintsThePassageWithoutPoints()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("slipsbc"), discipline: DisciplineId.ScoreByCount);
        await c.AddControlsAsync(1, "31", "32", "33", "34");
        await c.AddGroupAsync("Ч21", "31 32 33 34", s => s.RequiredControlCount = 3);
        await c.AddRunnerAsync("Runner", "1", "1001", "Ч21", T(10, 0));
        var readout = await c.AddReadoutAsync(1, "1001", "34 32", T(10, 0), T(10, 15));
        await c.SelectAsync();

        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var slip = await editor.GetSplitPrintDocumentAsync(readout.Id);

        Assert.NotNull(slip);
        Assert.Equal("MP", slip.StatusText);
        Assert.Equal(["34", "32", "F"], slip.Rows.Select(r => r.Code));
        Assert.False(slip.HasPoints);
        Assert.False(slip.HasGeometry);
        Assert.All(slip.Rows, r => Assert.Equal("5:00", r.LegText));
        Assert.NotEqual(string.Empty, slip.Rows[1].DistanceText);
        // Too few controls is not a broken order: no «правильний порядок» listing every allowed control.
        Assert.Empty(slip.CorrectOrder);
    }
}
