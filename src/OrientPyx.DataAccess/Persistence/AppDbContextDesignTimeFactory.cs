using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OrientPyx.DataAccess.Persistence;

/// <summary>
/// Design-time factory used by the EF Core tools (dotnet ef) to construct an
/// <see cref="AppDbContext"/> for scaffolding migrations. The connection string is a
/// placeholder and is never used at runtime — the real one is resolved by DI.
/// </summary>
public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=app.db")
            .Options;

        return new AppDbContext(options);
    }
}
