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

    /// <summary>Ensures the shared app database exists and is up to date. Event databases are migrated on demand.</summary>
    public static void InitializeOrientDeskDatabase(this IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        using var appDb = factory.CreateDbContext();
        // Applies all pending migrations, creating the database on first run.
        appDb.Database.Migrate();
    }
}
