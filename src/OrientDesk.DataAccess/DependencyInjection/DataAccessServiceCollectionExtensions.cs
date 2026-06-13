using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.DataAccess.FileSystem;
using OrientDesk.DataAccess.Persistence;

namespace OrientDesk.DataAccess.DependencyInjection;

public static class DataAccessServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared app database (./data/app.db), the per-event store/factory,
    /// and the events-folder scanner.
    /// </summary>
    public static IServiceCollection AddOrientDeskDataAccess(this IServiceCollection services)
    {
        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseSqlite(AppDatabasePaths.GetAppConnectionString()));

        services.AddSingleton<IAppStore, AppStore>();

        // Per-launch diagnostic log under ./events/logs (actions + exceptions).
        services.AddSingleton<IActivityLog, FileActivityLog>();

        // EventStore opens per-competition databases on demand; it holds no shared state.
        services.AddSingleton<IEventStore, EventStore>();
        services.AddSingleton<IEventFolderScanner, EventFolderScanner>();

        return services;
    }

    /// <summary>Ensures the shared app database exists and is up to date. Event databases are migrated on demand.</summary>
    public static void InitializeOrientDeskDatabase(this IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        using var appDb = factory.CreateDbContext();
        // Applies all pending migrations, creating the database on first run.
        appDb.Database.Migrate();

        // Seed the default sports ranks once (first run, or first run after the ranks table is added).
        var store = services.GetRequiredService<IAppStore>();
        store.SeedRanksIfEmptyAsync(DefaultRanks).GetAwaiter().GetResult();
    }

    /// <summary>
    /// The canonical Ukrainian-orienteering rank set seeded on first run (МСМК → б/р ю), each with its
    /// default points. Editable afterwards on the Ranks page; this is only the starting list.
    /// </summary>
    private static readonly IReadOnlyList<SportRank> DefaultRanks =
    [
        new() { Name = "МСМК",  Points = 150, Order = 0 },
        new() { Name = "ЗМС",   Points = 150, Order = 1 },
        new() { Name = "МСУ",   Points = 100, Order = 2 },
        new() { Name = "КМСУ",  Points = 30,  Order = 3 },
        new() { Name = "I",     Points = 10,  Order = 4 },
        new() { Name = "II",    Points = 3,   Order = 5 },
        new() { Name = "III",   Points = 1,   Order = 6 },
        new() { Name = "I-ю",   Points = 3,   Order = 7 },
        new() { Name = "II-ю",  Points = 1,   Order = 8 },
        new() { Name = "III-ю", Points = 0.5, Order = 9 },
        new() { Name = "б/р",   Points = 0.5, Order = 10 },
        new() { Name = "б/р ю", Points = 0.3, Order = 11 },
    ];
}
