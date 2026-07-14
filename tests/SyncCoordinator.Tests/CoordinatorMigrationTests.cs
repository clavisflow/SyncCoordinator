using Microsoft.EntityFrameworkCore;
using SyncCoordinator.Infrastructure.Persistence;

namespace SyncCoordinator.Tests;

public sealed class CoordinatorMigrationTests
{
    [Fact]
    public void CoordinatorSchemaHasSingleBaselineMigration()
    {
        using var context = CreateContext();

        var migration = Assert.Single(context.Database.GetMigrations());

        Assert.EndsWith("_InitialCoordinatorSchema", migration, StringComparison.Ordinal);
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
    }

    private static CoordinatorDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoordinatorDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=SyncCoordinatorModelTests;Trusted_Connection=True")
            .Options;
        return new CoordinatorDbContext(options);
    }
}
