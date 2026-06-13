using Microsoft.EntityFrameworkCore;
using OrientDesk.BusinessLogic.Entities;

namespace OrientDesk.DataAccess.Persistence;

/// <summary>
/// EF Core context for the shared application database (./data/app.db).
/// Holds configurable paths and the last-session pointer used for startup restore.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<AppSettingsRow> Settings => Set<AppSettingsRow>();
    public DbSet<LastSessionRow> LastSession => Set<LastSessionRow>();

    /// <summary>Application-level sports ranks (розряди), shared across every competition.</summary>
    public DbSet<SportRank> Ranks => Set<SportRank>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Single-row tables; key is not generated (fixed value 1).
        modelBuilder.Entity<AppSettingsRow>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<LastSessionRow>().Property(x => x.Id).ValueGeneratedNever();

        // Rank names are unique (case-insensitive) but blanks are exempt, so a freshly added (still
        // unnamed) row never collides with another blank one. SQLite's NOCASE collation gives the
        // case-insensitivity; a filtered index ('' excluded) gives the blank exemption.
        modelBuilder.Entity<SportRank>()
            .HasIndex(r => r.Name)
            .IsUnique()
            .HasFilter("\"Name\" <> ''");
        modelBuilder.Entity<SportRank>()
            .Property(r => r.Name)
            .UseCollation("NOCASE");
    }
}
