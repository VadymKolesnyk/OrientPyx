using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace OrientPyx.Tests.Core;

/// <summary>Export every prepared competition to <c>.opyx</c>, import it under a new identifier, and check the
/// imported copy computes the same results.</summary>
[Collection(AppCollection.Name)]
public sealed class ArchiveRoundTripTests
{
    private readonly ITestOutputHelper _output;

    public ArchiveRoundTripTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ExportThenImportKeepsResults()
    {
        var env = TestEnvironment.Instance;
        var sp = env.Services;
        var archive = sp.GetRequiredService<IEventArchiveService>();
        var store = sp.GetRequiredService<IEventStore>();
        var session = sp.GetRequiredService<ISessionService>();
        var editor = sp.GetRequiredService<ICompetitionEditorService>();

        foreach (var id in env.EventIds)
        {
            var ev = await env.GetEventAsync(id);
            var days = (await store.GetDaysAsync(ev.FolderPath)).OrderBy(d => d.Number).ToList();
            await session.SelectAsync(ev, days[0]);
            var before = await ResultsSnapshot.BuildAsync(editor, session, days);

            var path = Path.Combine(TestEnvironment.ResultsDir, $"{id}.opyx");
            await store.ReleaseAsync(ev.FolderPath);
            await archive.ExportAsync(id, path);
            var preview = await archive.PreviewImportAsync(path);
            Assert.Equal(id, preview.Identifier);
            Assert.True(preview.IdentifierExists);

            var copyId = $"{id}_rt";
            var imported = await archive.ImportAsync(path, copyId, overwrite: true);
            Assert.Equal(copyId, imported.Competition.Identifier);

            var copyDays = (await store.GetDaysAsync(imported.Competition.FolderPath)).OrderBy(d => d.Number).ToList();
            await session.SelectAsync(imported.Competition, copyDays[0]);
            var after = await ResultsSnapshot.BuildAsync(editor, session, copyDays);
            Assert.Equal(before, after);

            _output.WriteLine($"{id}: {new FileInfo(path).Length / 1024} KB, {preview.MissingPlugins.Count} missing plugins, results identical after import");
        }
    }
}
