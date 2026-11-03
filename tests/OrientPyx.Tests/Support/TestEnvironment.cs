using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.DataAccess.DependencyInjection;
using OrientPyx.DataAccess.Persistence;
using OrientPyx.Localization.DependencyInjection;

namespace OrientPyx.Tests.Support;

/// <summary>
/// The one test environment of the process: a fresh data root under the temp folder (the real app
/// redirects there too), the three core layers wired through their own DI extensions, the synthetic
/// competition and every <c>.opyx</c> fixture imported into it. Created lazily by the first test that
/// needs it; the headless application (<see cref="HeadlessApp"/>) starts over the same root.
/// </summary>
public sealed class TestEnvironment
{
    private static readonly Lazy<TestEnvironment> Lazy = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    public static TestEnvironment Instance => Lazy.Value;

    /// <summary>Root of the OrientPyx repository (found by walking up to <c>OrientPyx.sln</c>).</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>Where <c>.opyx</c> fixtures and their approved <c>.results.json</c> live. Overridable with
    /// the <c>ORIENTPYX_TEST_FIXTURES</c> environment variable; the default folder is git-ignored.</summary>
    public static string FixturesDir { get; } =
        Environment.GetEnvironmentVariable("ORIENTPYX_TEST_FIXTURES") is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : Path.Combine(RepoRoot, "tests", "fixtures");

    /// <summary>Screenshots and other artifacts of the run: <c>tests/TestResults/&lt;timestamp&gt;</c> (git-ignored).</summary>
    public static string ResultsDir { get; } =
        Path.Combine(RepoRoot, "tests", "TestResults", DateTime.Now.ToString("yyyyMMdd-HHmmss"));

    public string DataRoot { get; }

    public IServiceProvider Services { get; }

    /// <summary>Identifiers of the competitions prepared at start: the synthetic one first, then the fixtures.</summary>
    public IReadOnlyList<string> EventIds { get; }

    public string SyntheticId => SyntheticCompetition.Identifier;

    private TestEnvironment(string dataRoot, IServiceProvider services, IReadOnlyList<string> eventIds)
    {
        DataRoot = dataRoot;
        Services = services;
        EventIds = eventIds;
    }

    private static TestEnvironment Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "OrientPyx.Tests",
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(ResultsDir);
        AppDatabasePaths.UseDataRoot(root);

        var services = new ServiceCollection();
        services.AddOrientPyxLocalization();
        services.AddOrientPyxBusinessLogic();
        services.AddOrientPyxDataAccess();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        provider.InitializeOrientPyxDatabase();

        var ids = new List<string> { SyntheticCompetition.CreateAsync(provider).GetAwaiter().GetResult() };
        ids.AddRange(ImportFixturesAsync(provider).GetAwaiter().GetResult());
        return new TestEnvironment(root, provider, ids);
    }

    private static async Task<List<string>> ImportFixturesAsync(IServiceProvider provider)
    {
        var ids = new List<string>();
        if (!Directory.Exists(FixturesDir))
            return ids;

        var archive = provider.GetRequiredService<IEventArchiveService>();
        foreach (var file in Directory.GetFiles(FixturesDir, "*.opyx").OrderBy(f => f, StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!archive.IsValidIdentifier(id))
                throw new InvalidOperationException($"Fixture name is not a valid competition identifier: {file}");
            await archive.ImportAsync(file, id, overwrite: false);
            ids.Add(id);
        }
        return ids;
    }

    /// <summary>The summary of a prepared competition (scans the events folder like the app does).</summary>
    public async Task<EventSummary> GetEventAsync(string identifier)
    {
        var catalog = Services.GetRequiredService<IEventCatalogService>();
        return await catalog.FindByIdentifierAsync(identifier)
               ?? throw new InvalidOperationException($"Competition '{identifier}' is not in the test data root.");
    }

    /// <summary>Path of the approved results snapshot of a competition: the synthetic one ships with the tests,
    /// a fixture's lives next to its archive.</summary>
    public string GoldenPath(string identifier) =>
        identifier == SyntheticId
            ? Path.Combine(AppContext.BaseDirectory, "Golden", $"{identifier}.results.json")
            : Path.Combine(FixturesDir, $"{identifier}.results.json");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "OrientPyx.sln")))
                return dir.FullName;
        throw new InvalidOperationException("OrientPyx.sln not found above the test binaries.");
    }
}
