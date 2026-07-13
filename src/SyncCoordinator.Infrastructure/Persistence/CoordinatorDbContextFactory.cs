using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SyncCoordinator.Infrastructure.Persistence;

public sealed class CoordinatorDbContextFactory : IDesignTimeDbContextFactory<CoordinatorDbContext>
{
    public CoordinatorDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("SYNC_COORDINATOR_DESIGN_CONNECTION") ??
            "Server=(localdb)\\mssqllocaldb;Database=SyncCoordinator;Trusted_Connection=True;MultipleActiveResultSets=true";
        var options = new DbContextOptionsBuilder<CoordinatorDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new CoordinatorDbContext(options);
    }
}
