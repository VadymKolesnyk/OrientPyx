using Microsoft.EntityFrameworkCore;

namespace OrientDesk.DataAccess.Persistence;

/// <summary>Creates <see cref="EventDbContext"/> instances bound to a specific event folder.</summary>
internal static class EventDbContextFactory
{
    public static EventDbContext Create(string eventFolderPath)
    {
        var options = new DbContextOptionsBuilder<EventDbContext>()
            .UseSqlite(AppDatabasePaths.GetEventConnectionString(eventFolderPath))
            .Options;

        return new EventDbContext(options);
    }
}
