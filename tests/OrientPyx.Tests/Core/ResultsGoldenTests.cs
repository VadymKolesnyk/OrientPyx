using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace OrientPyx.Tests.Core;

/// <summary>
/// The computed results of every prepared competition must equal the approved snapshot: the synthetic
/// competition's ships in <c>Golden/</c>; a fixture's <c>&lt;id&gt;.results.json</c> lives next to its
/// <c>.opyx</c>. A missing approved snapshot is written and reported (approve it by keeping the file);
/// a mismatch writes <c>&lt;id&gt;.actual.json</c> into the results folder for <c>scripts/diff-results.ps1</c>.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class ResultsGoldenTests
{
    private readonly ITestOutputHelper _output;

    public ResultsGoldenTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task EveryCompetitionMatchesItsApprovedResults()
    {
        var env = TestEnvironment.Instance;
        var editor = env.Services.GetRequiredService<ICompetitionEditorService>();
        var session = env.Services.GetRequiredService<ISessionService>();
        var store = env.Services.GetRequiredService<IEventStore>();

        var mismatches = new List<string>();
        foreach (var id in env.EventIds)
        {
            var ev = await env.GetEventAsync(id);
            var days = await store.GetDaysAsync(ev.FolderPath);
            await session.SelectAsync(ev, days.OrderBy(d => d.Number).First());
            var actual = await ResultsSnapshot.BuildAsync(editor, session, days);

            var golden = env.GoldenPath(id);
            if (!File.Exists(golden))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(golden)!);
                await File.WriteAllTextAsync(golden, actual);
                _output.WriteLine($"{id}: no approved snapshot yet — written to {golden}");
                continue;
            }

            var expected = await File.ReadAllTextAsync(golden);
            if (expected == actual)
            {
                _output.WriteLine($"{id}: matches {golden}");
                continue;
            }

            var actualPath = Path.Combine(TestEnvironment.ResultsDir, $"{id}.actual.json");
            await File.WriteAllTextAsync(actualPath, actual);
            mismatches.Add($"{id}: results differ from {golden} — actual written to {actualPath}");
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }
}
