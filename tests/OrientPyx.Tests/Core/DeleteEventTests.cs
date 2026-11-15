using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>Deleting a competition («Вибір змагання» → кошик): the whole folder goes, the startup pointer forgets it,
/// and nothing but a competition folder directly under the events path is ever deleted.</summary>
[Collection(AppCollection.Name)]
public sealed class DeleteEventTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static IEventCatalogService Catalog => Sp.GetRequiredService<IEventCatalogService>();

    [Fact]
    public async Task DeletesTheWholeFolderAndForgetsTheLastSession()
    {
        var session = Sp.GetRequiredService<ISessionService>();
        var appStore = Sp.GetRequiredService<IAppStore>();
        var log = Sp.GetRequiredService<IActivityLog>();
        var id = TestIds.Next("del");
        var c = await CompetitionBuilder.CreateAsync(Sp, id, dayCount: 2);
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("Бігун 01", "1", "", "Ч21");
        await File.WriteAllTextAsync(Path.Combine(c.Folder, "day1", "event.csv"), "readout");

        // Opened last: the startup pointer names it, the log writes into its folder, the pools hold its database.
        await c.SelectAsync(2);
        session.Clear();
        Assert.Equal(id, (await appStore.GetLastSessionAsync()).Identifier);

        await Catalog.DeleteEventAsync(c.Summary);
        log.Info("a line after the delete must not re-create the folder");

        Assert.False(Directory.Exists(c.Folder));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(c.Folder)!, ".deleted-*"));
        Assert.Null(await Catalog.FindByIdentifierAsync(id));
        Assert.True(await Catalog.IsIdentifierAvailableAsync(id));
        Assert.Null((await appStore.GetLastSessionAsync()).Identifier);
        Assert.False(await session.TryRestoreLastAsync());

        // Already gone: nothing to do, no error.
        await Catalog.DeleteEventAsync(c.Summary);
    }

    [Fact]
    public async Task KeepsOtherCompetitionsAndRefusesForeignFolders()
    {
        var session = Sp.GetRequiredService<ISessionService>();
        var appStore = Sp.GetRequiredService<IAppStore>();
        var keep = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("del_keep"));
        var gone = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("del_gone"));
        await keep.SelectAsync();
        session.Clear();

        await Catalog.DeleteEventAsync(gone.Summary);

        Assert.False(Directory.Exists(gone.Folder));
        Assert.True(Directory.Exists(keep.Folder));
        Assert.NotNull(await Catalog.FindByIdentifierAsync(keep.Summary.Identifier));
        Assert.Equal(keep.Summary.Identifier, (await appStore.GetLastSessionAsync()).Identifier);

        // Never the events folder itself, a folder inside a competition, a hidden service folder or a path elsewhere.
        var eventsPath = Path.GetDirectoryName(keep.Folder)!;
        foreach (var path in new[]
                 {
                     eventsPath,
                     Path.Combine(keep.Folder, "day1"),
                     Path.Combine(eventsPath, ".deleted-x"),
                     Path.Combine(Path.GetTempPath(), keep.Summary.Identifier),
                     "",
                 })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Catalog.DeleteEventAsync(Pointing(keep.Summary, path)));
        }
        Assert.True(Directory.Exists(Path.Combine(keep.Folder, "day1")));
        Assert.NotNull(await Catalog.FindByIdentifierAsync(keep.Summary.Identifier));
    }

    private static EventSummary Pointing(EventSummary summary, string folderPath) => new()
    {
        Identifier = summary.Identifier,
        Name = summary.Name,
        FolderPath = folderPath,
    };
}
