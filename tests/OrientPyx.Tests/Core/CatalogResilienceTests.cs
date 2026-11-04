using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>One unreadable competition folder must neither hide the other competitions nor break the startup
/// restore (seen in the field: an older build scanning a database written by a newer one).</summary>
[Collection(AppCollection.Name)]
public sealed class CatalogResilienceTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    [Fact]
    public async Task UnreadableEventDatabaseIsSkippedNotFatal()
    {
        var catalog = Sp.GetRequiredService<IEventCatalogService>();
        var session = Sp.GetRequiredService<ISessionService>();
        var id = TestIds.Next("good");
        var good = await CompetitionBuilder.CreateAsync(Sp, id);
        var goodSummary = (await catalog.FindByIdentifierAsync(id))!;
        var eventsPath = Path.GetDirectoryName(goodSummary.FolderPath)!;

        var brokenFolder = Path.Combine(eventsPath, TestIds.Next("broken"));
        Directory.CreateDirectory(brokenFolder);
        try
        {
            // Not an SQLite file at all: migrating/reading it throws "file is not a database".
            await File.WriteAllBytesAsync(Path.Combine(brokenFolder, "event.db"), new byte[4096]);

            var events = await catalog.GetEventsAsync();
            Assert.Contains(events, e => e.Identifier == id);
            Assert.DoesNotContain(events, e => e.FolderPath == brokenFolder);

            // The last-session restore walks the same scan and must still find the healthy competition.
            await good.SelectAsync(1);
            session.Clear();
            Assert.True(await session.TryRestoreLastAsync());
            Assert.Equal(id, session.CurrentEvent!.Identifier);
        }
        finally
        {
            // The failed open leaves a pooled connection on the file; release it before deleting.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(brokenFolder, recursive: true); } catch (IOException) { }
        }
    }
}
