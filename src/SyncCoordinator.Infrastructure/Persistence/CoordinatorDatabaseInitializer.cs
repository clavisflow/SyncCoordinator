using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyncCoordinator.Core;

namespace SyncCoordinator.Infrastructure.Persistence;

public sealed class CoordinatorDatabaseOptions
{
    public bool ApplyMigrations { get; set; }
    public bool SeedSampleData { get; set; }
}

public sealed class CoordinatorDatabaseInitializer(
    CoordinatorDbContext dbContext,
    IOptions<CoordinatorDatabaseOptions> options)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.ApplyMigrations)
        {
            return;
        }

        await dbContext.Database.MigrateAsync(cancellationToken);
        if (!options.Value.SeedSampleData || await dbContext.Systems.AnyAsync(cancellationToken))
        {
            return;
        }

        dbContext.Systems.AddRange(
            NewSystem("A", "System A", "SqlServer"),
            NewSystem("B", "System B", "MySql"),
            NewSystem("C", "System C", "SqlServer"));
        dbContext.Routes.AddRange(
            NewRoute("Sample: A requests to C", "A", "SampleWorkRequest", DestinationMode.FixedSystem, "C"),
            NewRoute("Sample: B requests to C", "B", "SampleWorkRequest", DestinationMode.FixedSystem, "C"),
            NewRoute("Sample: C results to origin", "C", "SampleWorkResult", DestinationMode.OriginSystem, null));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static SystemDefinitionEntity NewSystem(string code, string name, string provider) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        DisplayName = name,
        Provider = provider,
        Enabled = true
    };

    private static SyncRouteEntity NewRoute(
        string name,
        string source,
        string entityType,
        DestinationMode mode,
        string? destination) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            SourceSystem = source,
            EntityType = entityType,
            DestinationMode = mode,
            DestinationSystem = destination,
            ConflictScope = ConflictScope.Field,
            DefaultConflictPolicy = ConflictPolicy.HoldAndNotify,
            Enabled = true
        };
}
