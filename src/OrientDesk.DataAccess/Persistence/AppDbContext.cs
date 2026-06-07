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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Single-row tables; key is not generated (fixed value 1).
        modelBuilder.Entity<AppSettingsRow>().Property(x => x.Id).ValueGeneratedNever();
        modelBuilder.Entity<LastSessionRow>().Property(x => x.Id).ValueGeneratedNever();
    }
}
