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
    public DbSet<Participant> Participants => Set<Participant>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<ChipRental> ChipRentals => Set<ChipRental>();
}
