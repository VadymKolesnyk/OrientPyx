using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Localization;
using OrientPyx.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace OrientPyx.Tests.Core;

/// <summary>
/// The plugin catalog: the standard plugins are copied from the bundled folder into the one plugin folder
/// under the data root and loaded from there, a second sync leaves up-to-date files alone, every plugin is
/// off until switched on, and switching one on registers its disciplines and strings live.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class PluginCatalogTests
{
    private readonly ITestOutputHelper _output;

    public PluginCatalogTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void StandardPluginsAreSyncedIntoTheDataRootAndLoadedFromThere()
    {
        var env = TestEnvironment.Instance;
        var catalog = env.Services.GetRequiredService<IPluginCatalog>();
        var folder = Path.GetFullPath(Path.Combine(env.DataRoot, "plugins"));

        Assert.Empty(catalog.Errors);
        Assert.Equal([folder], catalog.Folders.Select(Path.GetFullPath));
        Assert.Equal(folder, Path.GetFullPath(catalog.UserFolder));
        Assert.True(Directory.Exists(folder), "the sync did not create <data root>/plugins");

        var ids = catalog.Plugins.Select(p => p.Info.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(["missed-penalty", "weighted"], ids);
        Assert.All(catalog.Plugins, p =>
            Assert.StartsWith(folder, Path.GetFullPath(p.Folder), StringComparison.OrdinalIgnoreCase));

        foreach (var p in catalog.Plugins)
            _output.WriteLine($"{p.Info.Id} {p.Info.Version}: {string.Join(", ", p.Disciplines.Select(d => d.Id))} from {p.Folder}");
    }

    [Fact]
    public void ASecondSyncKeepsUpToDateFiles()
    {
        var env = TestEnvironment.Instance;
        var catalog = env.Services.GetRequiredService<IPluginCatalog>();
        var folder = catalog.Folders[0];
        var before = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.GetLastWriteTimeUtc);
        Assert.NotEmpty(before);

        // SyncBundled is the private step the catalog runs before loading; a second launch runs it again.
        var sync = catalog.GetType().GetMethod("SyncBundled", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var bundled = Path.Combine(TestEnvironment.RepoRoot, "artifacts", "plugins");
        sync.Invoke(catalog, [bundled, folder]);

        foreach (var (file, stamp) in before)
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public async Task PluginsAreOffByDefaultAndSwitchOnLive()
    {
        var env = TestEnvironment.Instance;
        var catalog = env.Services.GetRequiredService<IPluginCatalog>();
        var provider = env.Services.GetRequiredService<IDisciplineStrategyProvider>();
        var localization = env.Services.GetRequiredService<ILocalizationService>();
        foreach (var p in catalog.Plugins)
            foreach (var (culture, strings) in p.Strings)
                localization.AddOverlay(culture, p.Info.Id, strings);

        var weighted = new DisciplineId("weighted.relay");
        var removedVariant = new DisciplineId("weighted.set-course");
        var wasEnabled = catalog.Plugins.ToDictionary(p => p.Info.Id, p => catalog.IsEnabled(p.Info.Id));

        try
        {
            foreach (var p in catalog.Plugins)
                await catalog.SetEnabledAsync(p.Info.Id, false);
            Assert.All(catalog.Plugins, p => Assert.False(catalog.IsEnabled(p.Info.Id)));
            Assert.True(provider.For(weighted).IsUnknown, "a switched-off plugin's discipline must resolve to the unknown strategy");
            Assert.DoesNotContain(provider.All, s => s.Id == weighted);

            await catalog.SetEnabledAsync("weighted", true);
            Assert.True(catalog.IsEnabled("weighted"));
            Assert.False(provider.For(weighted).IsUnknown);
            Assert.Contains(provider.All, s => s.Id == weighted);
            Assert.True(provider.For(removedVariant).IsUnknown, "the removed individual variant must stay unknown");
            Assert.Equal("Зважена естафета (T·K)", localization.Get("weighted.name.relay"));

            await catalog.SetEnabledAsync("weighted", false);
            Assert.True(provider.For(weighted).IsUnknown);
        }
        finally
        {
            foreach (var (id, enabled) in wasEnabled)
                await catalog.SetEnabledAsync(id, enabled);
        }
    }
}
