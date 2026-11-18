using Microsoft.EntityFrameworkCore;
using OrientPyx.DataAccess.Persistence.Undo;

namespace OrientPyx.DataAccess.Persistence;

/// <summary>Creates <see cref="EventDbContext"/> instances bound to a specific event folder.</summary>
internal static class EventDbContextFactory
{
    public static EventDbContext Create(string eventFolderPath)
    {
        var options = new DbContextOptionsBuilder<EventDbContext>()
            .UseSqlite(AppDatabasePaths.GetEventConnectionString(eventFolderPath))
            // The undo journal (Ctrl+Z): every write under an undo step is recorded with its old values.
            .AddInterceptors(UndoRecorder.Interceptors)
            .Options;

        var db = new EventDbContext(options);
        UndoRecorder.Attach(db, eventFolderPath);
        return db;
    }
}
