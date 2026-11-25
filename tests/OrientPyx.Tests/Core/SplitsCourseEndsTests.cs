using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// The first and last legs of the splits (finish panel, slip, HTML splits) run from the course's own start and to its
/// own finish: a day may have several — groups starting or finishing in different places — told by their control-point
/// type whatever they are called. A course naming none runs from the day's first start to its first finish.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class SplitsCourseEndsTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static TimeSpan T(int h, int m) => new(h, m, 0);

    [Fact]
    public async Task FirstAndLastLegsMeasureFromTheCoursesOwnStartAndFinish()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("ends"));
        var dayId = c.Day(1).Id;
        ControlPoint Cp(int order, string code, ControlPointType type, double lat, double lon) =>
            new() { EventDayId = dayId, Order = order, Code = code, Type = type, Latitude = lat, Longitude = lon };
        var start1 = Cp(0, "Ста1", ControlPointType.Start, 50.400, 30.500);
        var start2 = Cp(1, "Ста2", ControlPointType.Start, 50.500, 30.600);
        var c31 = Cp(2, "31", ControlPointType.Regular, 50.450, 30.550);
        var c32 = Cp(3, "32", ControlPointType.Regular, 50.460, 30.560);
        var finish1 = Cp(4, "Фін1", ControlPointType.Finish, 50.470, 30.570);
        var finish2 = Cp(5, "Фін2", ControlPointType.Finish, 50.300, 30.300);
        await Sp.GetRequiredService<IEventStore>().AddControlPointsAsync(c.Folder, [start1, start2, c31, c32, finish1, finish2]);

        await c.AddGroupAsync("A", "Ста1 31 32 Фін1");
        await c.AddGroupAsync("B", "Ста2 31 32 Фін2");
        await c.AddGroupAsync("C", "31 32");
        var readouts = new Dictionary<string, FinishReadout>();
        foreach (var (group, chip) in new[] { ("A", "1001"), ("B", "1002"), ("C", "1003") })
        {
            await c.AddRunnerAsync($"R{group}", chip, chip, group, T(10, 0));
            readouts[group] = await c.AddReadoutAsync(1, chip, "31 32", T(10, 0), T(10, 20));
        }

        var r = await c.ResultsAsync();
        Assert.All(r.Values, x => Assert.Equal(FinishStatus.Ok, x.Status));     // start/finish never required

        var distance = Sp.GetRequiredService<ICourseDistanceCalculator>();
        decimal Km(ControlPoint from, ControlPoint to) => distance.TotalKilometres(
            [new GeoPoint(from.Latitude, from.Longitude), new GeoPoint(to.Latitude, to.Longitude)]);
        static (decimal? First, decimal? Last) Legs(SplitsView view) => (
            view.Passage.First(p => p.Kind == PassageKind.Control).DisplayLegKm,
            view.Passage.Single(p => p.Kind == PassageKind.Finish).DisplayLegKm);

        var expected = new Dictionary<string, (decimal?, decimal?)>
        {
            ["A"] = (Km(start1, c31), Km(c32, finish1)),
            ["B"] = (Km(start2, c31), Km(c32, finish2)),
            ["C"] = (Km(start1, c31), Km(c32, finish1)),     // names none: the day's first start and finish
        };
        Assert.NotEqual(expected["A"], expected["B"]);

        // The finish panel (the slip prints the same view).
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        foreach (var (group, readout) in readouts)
            Assert.Equal(expected[group], Legs((await editor.GetFinishSplitsAsync(readout.Id))!));

        // The HTML splits of the day.
        var export = await editor.GetDaySplitsExportDataAsync(dayId);
        foreach (var group in export.Groups)
            Assert.Equal(expected[group.Name], Legs(group.Rows.Single().Splits));
    }
}
