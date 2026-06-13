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
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupDaySettings> GroupDaySettings => Set<GroupDaySettings>();
    public DbSet<RentalChip> RentalChips => Set<RentalChip>();
    public DbSet<Region> Regions => Set<Region>();
    public DbSet<Club> Clubs => Set<Club>();
    public DbSet<Dussh> Dusshes => Set<Dussh>();
    public DbSet<Participant> Participants => Set<Participant>();
    public DbSet<ParticipantDay> ParticipantDays => Set<ParticipantDay>();

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

        // Nullable enum: null persists as NULL (= inherit the day's default discipline).
        modelBuilder.Entity<GroupDaySettings>()
            .Property(g => g.DisciplineOverride)
            .HasConversion<string>();

        // A chip number identifies one rental chip per competition; the unique index both enforces
        // that and speeds the future join against participant entries by chip number.
        modelBuilder.Entity<RentalChip>()
            .HasIndex(c => c.Number)
            .IsUnique();

        // A region name identifies one region per competition; the unique index enforces that (the
        // service layer additionally guards case-insensitive collisions).
        modelBuilder.Entity<Region>()
            .HasIndex(r => r.Name)
            .IsUnique();

        // A club name is likewise unique per competition.
        modelBuilder.Entity<Club>()
            .HasIndex(c => c.Name)
            .IsUnique();

        // A sports-school (ДЮСШ) name is likewise unique per competition.
        modelBuilder.Entity<Dussh>()
            .HasIndex(d => d.Name)
            .IsUnique();

        // Per-day links are queried by day (the day's grid) and by participant (the cascade-delete
        // and roster aggregation). Number/chip uniqueness is enforced in the service layer rather
        // than by a DB index, because blank values are allowed and must not collide with each other.
        modelBuilder.Entity<ParticipantDay>()
            .HasIndex(p => p.EventDayId);

        modelBuilder.Entity<ParticipantDay>()
            .HasIndex(p => p.ParticipantId);
    }
}
