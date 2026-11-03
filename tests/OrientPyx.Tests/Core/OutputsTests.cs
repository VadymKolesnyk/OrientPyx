using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace OrientPyx.Tests.Core;

/// <summary>
/// Every page's data and every export the app offers builds for every prepared competition and day:
/// result / start / summary protocols and the statement as .docx, splits and monitor as HTML, winners
/// printout, online snapshot, draw and start-order data, quick withdrawal, relay teams, and the splits and
/// split printout of every read-out.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class OutputsTests
{
    private readonly ITestOutputHelper _output;

    public OutputsTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task CompetitionLevelDataAndDocumentsBuild()
    {
        var env = TestEnvironment.Instance;
        var sp = env.Services;
        var editor = sp.GetRequiredService<ICompetitionEditorService>();
        var session = sp.GetRequiredService<ISessionService>();
        var store = sp.GetRequiredService<IEventStore>();
        var app = sp.GetRequiredService<IAppSettingsService>();

        foreach (var id in env.EventIds)
        {
            var ev = await env.GetEventAsync(id);
            var days = (await store.GetDaysAsync(ev.FolderPath)).OrderBy(d => d.Number).ToList();
            await session.SelectAsync(ev, days[0]);

            var dashboard = await editor.GetDashboardAsync();
            Assert.True(dashboard.HasSelection);
            Assert.NotNull(await editor.GetInfoAsync());
            var roster = await editor.GetParticipantRosterAsync();
            Assert.Equal(dashboard.ParticipantTotal, roster.Count);
            _ = await editor.GetControlPointsAsync();
            _ = await editor.GetGroupsAsync();
            _ = await editor.GetRentalChipsAsync();
            _ = await editor.GetCustomFieldsAsync();
            _ = await editor.GetRelayTeamsDataAsync();

            var summaryData = await editor.GetSummaryProtocolDataAsync();
            var summarySettings = await editor.GetSummaryProtocolSettingsAsync() ?? await app.GetSummaryProtocolSettingsAsync();
            var summaryBuilder = sp.GetRequiredService<ISummaryProtocolBuilder>();
            var summaryDoc = summaryBuilder.Build(summaryData, summarySettings, Labels.Make<SummaryProtocolLabels>());
            Checks.IsDocx(sp.GetRequiredService<ISummaryProtocolWriter>().Write(summaryDoc));
            _ = summaryBuilder.RankGroups(summaryData, summarySettings);

            var statement = await editor.GetStatementDataAsync(null, roster.Select(r => r.ParticipantId).ToList());
            var statementSettings = await editor.GetStatementSettingsAsync() ?? await app.GetStatementSettingsAsync();
            var statementDoc = sp.GetRequiredService<IStatementBuilder>()
                .Build(statement, statementSettings, Labels.Make<StatementLabels>(), "all");
            Checks.IsDocx(sp.GetRequiredService<IResultProtocolWriter>().Write(statementDoc));

            _output.WriteLine($"{id}: {roster.Count} participants, {days.Count} days, statement rows {statement.Rows.Count}");
        }
    }

    [Fact]
    public async Task DayLevelDataAndDocumentsBuild()
    {
        var env = TestEnvironment.Instance;
        var sp = env.Services;
        var editor = sp.GetRequiredService<ICompetitionEditorService>();
        var session = sp.GetRequiredService<ISessionService>();
        var store = sp.GetRequiredService<IEventStore>();
        var app = sp.GetRequiredService<IAppSettingsService>();
        var docx = sp.GetRequiredService<IResultProtocolWriter>();

        foreach (var id in env.EventIds)
        {
            var ev = await env.GetEventAsync(id);
            var days = (await store.GetDaysAsync(ev.FolderPath)).OrderBy(d => d.Number).ToList();
            await session.SelectAsync(ev, days[0]);

            foreach (var day in days)
            {
                await session.SetCurrentDayAsync(day);

                _ = await editor.GetDayDisciplinesAsync(day.Id);
                var groupRows = await editor.GetGroupDayRowsAsync();
                var rows = await editor.GetParticipantDayRowsAsync();
                var results = await editor.GetDayResultsByParticipantAsync(day.Id);
                Assert.Equal(rows.Count, results.Count);

                var data = await editor.GetResultProtocolDataAsync(day.Id);
                var settings = await editor.GetResultProtocolSettingsAsync(day.Id) ?? await app.GetResultProtocolSettingsAsync();
                Checks.IsDocx(docx.Write(sp.GetRequiredService<IResultProtocolBuilder>().Build(data, settings, Labels.Make<ProtocolLabels>())));

                foreach (var kind in Enum.GetValues<StartProtocolKind>())
                {
                    var startData = await editor.GetStartProtocolDataAsync(day.Id);
                    var startSettings = await editor.GetStartProtocolSettingsAsync(day.Id, kind) ?? StartProtocolSettings.Default(kind);
                    Checks.IsDocx(docx.Write(sp.GetRequiredService<IStartProtocolBuilder>()
                        .Build(startData, startSettings, kind, Labels.Make<StartProtocolLabels>())));
                }

                var splits = await editor.GetDaySplitsExportDataAsync(day.Id);
                Checks.IsHtml(sp.GetRequiredService<ISplitHtmlWriter>().Write(sp.GetRequiredService<ISplitExportBuilder>()
                    .Build(splits, Labels.Make<SplitExportHeader>(), Labels.Make<SplitExportLabels>())));

                var monitor = await editor.BuildMonitorPreviewAsync(day.Id, Labels.Make<MonitorFile>(),
                    ResultColumnSelection.Default, Labels.Make<MonitorLabels>());
                if (monitor is not null)
                    Checks.IsHtml(sp.GetRequiredService<IMonitorHtmlWriter>().Write(monitor));
                foreach (var file in await editor.BuildMonitorDocumentsAsync(day.Id, Labels.Make<MonitorLabels>()))
                    Checks.IsHtml(sp.GetRequiredService<IMonitorHtmlWriter>().Write(file.Document));

                var online = await editor.GetOnlineResultsSnapshotAsync(day.Id);
                _ = sp.GetRequiredService<IWinnersPrintBuilder>()
                    .BuildForDay(data, new WinnersPrintHeader("c", "t", "d"), Labels.Make<WinnersPrintLabels>(), 3);
                _ = await editor.GetDrawPrepDataAsync(day.Id);
                _ = await editor.GetStartOrderDataAsync(day.Id);
                _ = await editor.GetQuickWithdrawalDataAsync(day.Id);

                var readouts = await editor.GetFinishReadoutRowsAsync();
                foreach (var readout in readouts)
                {
                    _ = await editor.GetFinishSplitsAsync(readout.Id);
                    _ = await editor.GetSplitPrintDocumentAsync(readout.Id);
                }

                _output.WriteLine($"{id} day {day.Number} ({day.DefaultDiscipline}): groups {groupRows.Count}, rows {rows.Count}, " +
                                  $"read-outs {readouts.Count}, online rows {online.Rows.Count}, extra columns {data.ExtraColumns.Count}");
            }
        }
    }
}
