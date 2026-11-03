using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace OrientPyx.Tests.Core;

/// <summary>
/// Day 1 of every prepared competition is switched through every registered strategy — the built-ins and
/// the switched-on plugins' — and the day's outputs are built under each. The original discipline is
/// restored afterwards. Catches a strategy that throws on data shaped for another format.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class DisciplineSweepTests
{
    private readonly ITestOutputHelper _output;

    public DisciplineSweepTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task EveryStrategyJudgesEveryCompetition()
    {
        var env = TestEnvironment.Instance;
        var sp = env.Services;
        var editor = sp.GetRequiredService<ICompetitionEditorService>();
        var session = sp.GetRequiredService<ISessionService>();
        var store = sp.GetRequiredService<IEventStore>();
        var app = sp.GetRequiredService<IAppSettingsService>();
        var provider = sp.GetRequiredService<IDisciplineStrategyProvider>();
        var catalog = sp.GetRequiredService<IPluginCatalog>();

        // Switch the plugins on for the sweep so their disciplines are registered too; restore afterwards.
        var wasEnabled = catalog.Plugins.ToDictionary(p => p.Info.Id, p => catalog.IsEnabled(p.Info.Id));
        foreach (var p in catalog.Plugins)
            await catalog.SetEnabledAsync(p.Info.Id, true);

        try
        {
            var strategies = provider.All.ToList();
            Assert.Contains(strategies, s => s.Id == DisciplineId.SetCourse);
            Assert.Contains(strategies, s => s.Id == DisciplineId.Relay);
            Assert.Contains(strategies, s => s.Id.Value.Contains('.')); // at least one plugin discipline

            foreach (var id in env.EventIds)
            {
                var ev = await env.GetEventAsync(id);
                var days = (await store.GetDaysAsync(ev.FolderPath)).OrderBy(d => d.Number).ToList();
                var day = days[0];
                await session.SelectAsync(ev, day);
                var original = day.DefaultDiscipline;

                try
                {
                    foreach (var strategy in strategies)
                    {
                        day.DefaultDiscipline = strategy.Id;
                        await editor.UpdateDayAsync(day);
                        await session.SetCurrentDayAsync(day);

                        var data = await editor.GetResultProtocolDataAsync(day.Id);
                        var settings = await editor.GetResultProtocolSettingsAsync(day.Id) ?? await app.GetResultProtocolSettingsAsync();
                        Checks.IsDocx(sp.GetRequiredService<IResultProtocolWriter>().Write(
                            sp.GetRequiredService<IResultProtocolBuilder>().Build(data, settings, Labels.Make<ProtocolLabels>())));
                        _ = await editor.GetOnlineResultsSnapshotAsync(day.Id);
                        var splits = await editor.GetDaySplitsExportDataAsync(day.Id);
                        Checks.IsHtml(sp.GetRequiredService<ISplitHtmlWriter>().Write(sp.GetRequiredService<ISplitExportBuilder>()
                            .Build(splits, Labels.Make<SplitExportHeader>(), Labels.Make<SplitExportLabels>())));
                        var monitor = await editor.BuildMonitorPreviewAsync(day.Id, Labels.Make<MonitorFile>(),
                            ResultColumnSelection.Default, Labels.Make<MonitorLabels>());
                        if (monitor is not null)
                            Checks.IsHtml(sp.GetRequiredService<IMonitorHtmlWriter>().Write(monitor));
                        _ = sp.GetRequiredService<IWinnersPrintBuilder>()
                            .BuildForDay(data, new WinnersPrintHeader("c", "t", "d"), Labels.Make<WinnersPrintLabels>(), 3);
                        _ = await editor.GetParticipantDayRowsAsync();
                        foreach (var readout in (await editor.GetFinishReadoutRowsAsync()).Take(20))
                        {
                            _ = await editor.GetFinishSplitsAsync(readout.Id);
                            _ = await editor.GetSplitPrintDocumentAsync(readout.Id);
                        }

                        var statuses = data.Groups.SelectMany(g => g.Rows)
                            .GroupBy(r => r.Result.Status).OrderBy(g => g.Key.ToString())
                            .Select(g => $"{g.Key}={g.Count()}");
                        _output.WriteLine($"{id} / {strategy.Id}: {string.Join(' ', statuses)}");
                    }
                }
                finally
                {
                    day.DefaultDiscipline = original;
                    await editor.UpdateDayAsync(day);
                    await session.SetCurrentDayAsync(day);
                }
            }
        }
        finally
        {
            foreach (var (pluginId, enabled) in wasEnabled)
                await catalog.SetEnabledAsync(pluginId, enabled);
        }
    }
}
