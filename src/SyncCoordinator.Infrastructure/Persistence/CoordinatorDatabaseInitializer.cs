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

        var systemA = NewSystem("A", "System A", "SqlServer");
        var systemB = NewSystem("B", "System B", "MySql");
        var systemC = NewSystem("C", "System C", "SqlServer");
        dbContext.Systems.AddRange(systemA, systemB, systemC);
        dbContext.Routes.AddRange(
            NewRule("Sample: A and C", systemA, "SampleWorkRequest", systemC, SyncDirection.Bidirectional),
            NewRule("Sample: B and C", systemB, "SampleWorkRequest", systemC, SyncDirection.Bidirectional));
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

    private static SyncRouteEntity NewRule(
        string name,
        SystemDefinitionEntity source,
        string entityType,
        SystemDefinitionEntity destination,
        SyncDirection direction) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            SourceSystemId = source.Id,
            SourceSystem = source,
            EntityType = entityType,
            DestinationSystemId = destination.Id,
            DestinationSystem = destination,
            Direction = direction,
            DeploymentState = DatabaseDeploymentState.Draft,
            ConflictScope = ConflictScope.Field,
            DefaultConflictPolicy = ConflictPolicy.HoldAndNotify,
            Enabled = false
        };
}
