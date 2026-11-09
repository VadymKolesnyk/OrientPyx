using OrientPyx.DataAccess.Persistence;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// Installed Windows builds keep their data outside the Velopack install folder (an uninstall deletes that folder):
/// the old <c>%LocalAppData%\OrientPyx\data-root</c> is moved to <c>%LocalAppData%\OrientPyx-data</c> once, and a
/// path stored under the old root reads as the same path under the new one.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class DataRootTests
{
    private static string Scratch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OrientPyx.DataRootTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void TheLegacyRootIsMovedWhole()
    {
        var scratch = Scratch();
        var legacy = Path.Combine(scratch, "OrientPyx", "data-root");
        var target = Path.Combine(scratch, "OrientPyx-data");
        Directory.CreateDirectory(Path.Combine(legacy, "events", "kubok"));
        File.WriteAllText(Path.Combine(legacy, "events", "kubok", "event.db"), "db");
        Directory.CreateDirectory(Path.Combine(legacy, "data"));
        File.WriteAllText(Path.Combine(legacy, "data", "app.db"), "app");

        var notes = new List<string>();
        Assert.Equal(target, AppDatabasePaths.MoveLegacyDataRoot(legacy, target, notes.Add));
        Assert.False(Directory.Exists(legacy));
        Assert.Equal("db", File.ReadAllText(Path.Combine(target, "events", "kubok", "event.db")));
        Assert.Equal("app", File.ReadAllText(Path.Combine(target, "data", "app.db")));
        Assert.Single(notes);
    }

    [Fact]
    public void AnExistingTargetWinsAndNothingToMoveIsFine()
    {
        var scratch = Scratch();
        var legacy = Path.Combine(scratch, "legacy");
        var target = Path.Combine(scratch, "target");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "old.txt"), "old");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "new.txt"), "new");

        Assert.Equal(target, AppDatabasePaths.MoveLegacyDataRoot(legacy, target));
        Assert.True(File.Exists(Path.Combine(legacy, "old.txt")));        // left alone, never merged
        Assert.False(File.Exists(Path.Combine(target, "old.txt")));

        var fresh = Path.Combine(scratch, "fresh");
        Assert.Equal(fresh, AppDatabasePaths.MoveLegacyDataRoot(Path.Combine(scratch, "missing"), fresh));
    }

    [Fact]
    public void AnEmptyTargetLeftoverDoesNotBlockTheMove()
    {
        var scratch = Scratch();
        var legacy = Path.Combine(scratch, "legacy");
        var target = Path.Combine(scratch, "target");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "a.txt"), "a");
        Directory.CreateDirectory(target);

        Assert.Equal(target, AppDatabasePaths.MoveLegacyDataRoot(legacy, target));
        Assert.True(File.Exists(Path.Combine(target, "a.txt")));
    }

    [Fact]
    public void AStoredPathUnderTheLegacyRootFollowsTheMove()
    {
        var legacy = Path.Combine(Path.GetTempPath(), "OrientPyx.DataRootTests", "legacy-root");
        AppDatabasePaths.UseLegacyDataRoots(legacy);
        try
        {
            Assert.Equal(Path.Combine(AppDatabasePaths.BaseDirectory, "events"),
                AppDatabasePaths.RemapLegacyPath(Path.Combine(legacy, "events")));
            Assert.Equal(AppDatabasePaths.BaseDirectory, AppDatabasePaths.RemapLegacyPath(legacy));
            var elsewhere = Path.Combine(Path.GetTempPath(), "OrientPyx.DataRootTests", "elsewhere", "events");
            Assert.Equal(elsewhere, AppDatabasePaths.RemapLegacyPath(elsewhere));
            Assert.Equal("events", AppDatabasePaths.RemapLegacyPath("events"));
        }
        finally
        {
            AppDatabasePaths.UseLegacyDataRoots();
        }
    }
}
