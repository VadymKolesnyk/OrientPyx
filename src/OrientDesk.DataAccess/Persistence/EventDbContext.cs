using Microsoft.EntityFrameworkCore;
using OrientDesk.BusinessLogic.Entities;

namespace OrientDesk.DataAccess.Persistence;

/// <summary>
/// EF Core context for a single competition's database (./events/&lt;id&gt;/event.db).
/// Opened dynamically per path via <see cref="EventDbContextFactory"/>.
/// </summary>
public class EventDbContext : DbContext
{
    public EventDbContext(DbContextOptions<EventDbContext> options)
        : base(options)
    {
    }

    public DbSet<CompetitionInfo> Competition => Set<CompetitionInfo>();
    public DbSet<EventDay> Days => Set<EventDay>();
    public DbSet<ControlPoint> ControlPoints => Set<ControlPoint>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Store enums by their string name (string-enum at the DB level), not as integers.
        modelBuilder.Entity<ControlPoint>()
            .Property(cp => cp.Type)
            .HasConversion<string>();

        modelBuilder.Entity<EventDay>()
            .Property(d => d.DefaultDiscipline)
            .HasConversion<string>();
    }
}
