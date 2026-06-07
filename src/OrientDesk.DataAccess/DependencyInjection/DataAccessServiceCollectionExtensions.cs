using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

        // EventStore opens per-competition databases on demand; it holds no shared state.
        services.AddSingleton<IEventStore, EventStore>();
        services.AddSingleton<IEventFolderScanner, EventFolderScanner>();

        return services;
    }

    /// <summary>Ensures the shared app database exists. Event databases are created on demand.</summary>
    public static void InitializeOrientDeskDatabase(this IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        using var appDb = factory.CreateDbContext();
        appDb.Database.EnsureCreated();

        // Lightweight forward-compatibility for app.db files created before new columns
        // were added (no migration pipeline yet). Idempotent: ignore "duplicate column".
        EnsureColumn(appDb, "Settings", "FontScale", "REAL NOT NULL DEFAULT 1.0");
    }

    private static void EnsureColumn(AppDbContext db, string table, string column, string definition)
    {
        // Identifiers/definition are compile-time constants from this assembly, never user input.
        var sql = string.Concat("ALTER TABLE \"", table, "\" ADD COLUMN \"", column, "\" ", definition, ";");
        try
        {
#pragma warning disable EF1002 // constant SQL, no user input
            db.Database.ExecuteSqlRaw(sql);
#pragma warning restore EF1002
        }
        catch
        {
            // Column already exists — nothing to do.
        }
    }
}
