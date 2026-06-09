using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OrientDesk.DataAccess.Persistence;

/// <summary>
/// Design-time factory used by the EF Core tools (dotnet ef) to construct an
/// <see cref="EventDbContext"/> for scaffolding migrations. The connection string is a
/// placeholder; at runtime <see cref="EventDbContextFactory"/> binds the real per-event path.
/// </summary>
public sealed class EventDbContextDesignTimeFactory : IDesignTimeDbContextFactory<EventDbContext>
{
    public EventDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<EventDbContext>()
            .UseSqlite("Data Source=event.db")
            .Options;

        return new EventDbContext(options);
    }
}
