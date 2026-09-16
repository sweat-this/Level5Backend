using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Level5.Infrastructure.Persistence;

/// <summary>
/// Lets `dotnet ef` create a context without booting the whole Api host (which would otherwise
/// fail fast on missing runtime secrets like Jwt:Key that migrations don't need). Design-time
/// only - never used by the running application.
/// </summary>
public sealed class Level5V2DbContextFactory : IDesignTimeDbContextFactory<Level5V2DbContext>
{
    public Level5V2DbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? throw new InvalidOperationException("Set the ConnectionStrings__DefaultConnection environment variable to run EF Core design-time tooling.");

        var optionsBuilder = new DbContextOptionsBuilder<Level5V2DbContext>();
        optionsBuilder.UseNpgsql(connectionString);
        return new Level5V2DbContext(optionsBuilder.Options);
    }
}
