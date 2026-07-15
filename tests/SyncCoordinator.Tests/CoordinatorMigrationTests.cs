using Microsoft.EntityFrameworkCore;
using SyncCoordinator.Infrastructure.Persistence;

namespace SyncCoordinator.Tests;

public sealed class CoordinatorMigrationTests
{
    [Fact]
    public void CoordinatorSchemaHasExpectedMigrations()
    {
        using var context = CreateContext();

        Assert.Collection(
            context.Database.GetMigrations(),
            migration => Assert.EndsWith("_InitialCoordinatorSchema", migration, StringComparison.Ordinal),
            migration => Assert.EndsWith("_AddOperationalEvents", migration, StringComparison.Ordinal),
            migration => Assert.EndsWith("_AddMappingMaintenance", migration, StringComparison.Ordinal),
            migration => Assert.EndsWith("_AddValueTransformations", migration, StringComparison.Ordinal));
    }

    [Fact]
    public void OperationalEventsSupportAggregationAndAcknowledgement()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(OperationalEventEntity))!;

        Assert.Equal(4000, entity.FindProperty(nameof(OperationalEventEntity.Details))!.GetMaxLength());
        Assert.Contains(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(OperationalEventEntity.AcknowledgedAtUtc),
                nameof(OperationalEventEntity.LastOccurredAtUtc)
            ]));
        Assert.NotNull(entity.FindProperty(nameof(OperationalEventEntity.OccurrenceCount)));
        Assert.NotNull(entity.FindProperty(nameof(OperationalEventEntity.AcknowledgedBy)));
    }

    [Fact]
    public void TableMappingUsesRouteIdAsItsOneToOneKey()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(RouteTableMappingEntity))!;

        Assert.Equal(
            nameof(RouteTableMappingEntity.RouteId),
            Assert.Single(entity.FindPrimaryKey()!.Properties).Name);
        Assert.True(Assert.Single(entity.GetForeignKeys()).IsUnique);
    }

    [Fact]
    public void ColumnMappingOwnsItsConflictPolicy()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(RouteColumnMappingEntity))!;

        Assert.NotNull(entity.FindProperty(nameof(RouteColumnMappingEntity.ConflictPolicy)));
        Assert.DoesNotContain(
            context.Model.GetEntityTypes(),
            x => x.ClrType.Name == "RouteFieldPolicyEntity");
    }

    [Fact]
    public void RoutesReferenceBothSystemsWithForeignKeys()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(SyncRouteEntity))!;
        var foreignKeyProperties = entity.GetForeignKeys()
            .Select(x => Assert.Single(x.Properties).Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(nameof(SyncRouteEntity.SourceSystemId), foreignKeyProperties);
        Assert.Contains(nameof(SyncRouteEntity.DestinationSystemId), foreignKeyProperties);
        Assert.NotNull(entity.FindProperty(nameof(SyncRouteEntity.MappingMaintenanceStartedAtUtc)));
    }

    private static CoordinatorDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoordinatorDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=SyncCoordinatorModelTests;Trusted_Connection=True")
            .Options;
        return new CoordinatorDbContext(options);
    }
}
