using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// «Бали» and «Очки» on the monitor and online are two columns: «Бали» is a scored format's sum of control values,
/// «Очки» the points a rule awards — never one standing in for the other. Columns a day cannot fill («Наростаючий»
/// without a relay, «Бали» without a scored group) are listed as inapplicable, and the monitor leaves them out.
/// </summary>
public sealed class ScoreAndPointsColumnsTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static TimeSpan T(int h, int m, int s = 0) => new(h, m, s);

    [Fact]
    public async Task Rogaine_ShowsScoreApartFromPoints_AndHidesItInASetCourseGroup()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("scorecols"), discipline: DisciplineId.Rogaine);
        var cps = await c.AddControlsAsync(1, "31", "42");
        var store = Sp.GetRequiredService<IEventStore>();
        foreach (var cp in cps) { cp.Points = int.Parse(cp.Code[..1]); await store.UpdateControlPointAsync(c.Folder, cp); }
        await c.AddGroupAsync("Open", "31 42", s => s.TimeLimitSeconds = 3600);
        await c.AddGroupAsync("Ч21", "31 42", s => s.DisciplineOverride = DisciplineId.SetCourse);
        await c.AddRunnerAsync("Рогейнер", "1", "7011", "Open", T(10, 0));
        await c.AddRunnerAsync("Класик", "2", "7021", "Ч21", T(10, 0));
        await c.AddReadoutAsync(1, "7011", "31 42", T(10, 0), T(10, 50));
        await c.AddReadoutAsync(1, "7021", "31 42", T(10, 0), T(10, 30));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();

        // No points rule anywhere: «Бали» 7, «Очки» blank (they used to show the score).
        var selection = new ResultColumnSelection(["full_name", "score", "points", "cumulative"])
        {
            Version = ResultColumnSelection.CurrentVersion,
        };
        var monitor = await editor.BuildMonitorPreviewAsync(c.Day(1).Id, Labels.Make<MonitorFile>(), selection,
            Labels.Make<MonitorLabels>());
        Assert.NotNull(monitor);
        Assert.Equal([ResultColumn.FullName, ResultColumn.Score, ResultColumn.Points],
            monitor.Columns.Select(col => col.Column));                 // «Наростаючий»: no relay on the screen
        var open = monitor.Groups.Single(g => g.Name == "Open");
        Assert.Equal(["Рогейнер", "7", ""], open.Cells.Single().Values);
        Assert.Empty(open.HiddenColumns);
        var classic = monitor.Groups.Single(g => g.Name == "Ч21");
        Assert.Equal([1], classic.HiddenColumns);                      // a set course has no «Бали»

        var online = await editor.GetOnlineResultsSnapshotAsync(c.Day(1).Id);
        var row = online.Rows.Single(r => r.FullName == "Рогейнер");
        Assert.Equal(7, row.Score);
        Assert.Null(row.Points);
        Assert.Null(online.Rows.Single(r => r.FullName == "Класик").Score);
        Assert.Equal(["cumulative"], online.InapplicableColumns);
    }

    [Fact]
    public async Task SetCourseDay_MarksCumulativeAndScoreInapplicable()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("noscorecols"));
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("A", "1", "1001", "Ч21", T(10, 0));
        await c.AddReadoutAsync(1, "1001", "31", T(10, 0), T(10, 20));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();

        var online = await editor.GetOnlineResultsSnapshotAsync(c.Day(1).Id);
        Assert.Equal(["cumulative", "score"], online.InapplicableColumns);

        var monitor = await editor.BuildMonitorPreviewAsync(c.Day(1).Id, Labels.Make<MonitorFile>(),
            ResultColumnSelection.Default, Labels.Make<MonitorLabels>());
        Assert.DoesNotContain(monitor!.Columns, col => col.Column is ResultColumn.Score or ResultColumn.Cumulative);
        Assert.Contains(monitor.Columns, col => col.Column == ResultColumn.Points);
    }
}
