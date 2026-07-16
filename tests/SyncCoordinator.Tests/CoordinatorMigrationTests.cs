using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SyncCoordinator.Core;
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
            migration => Assert.EndsWith("_AddValueTransformations", migration, StringComparison.Ordinal),
            migration => Assert.EndsWith("_AddManagementSettings", migration, StringComparison.Ordinal),
            migration => Assert.EndsWith("_RemoveResolvedConflictRetention", migration, StringComparison.Ordinal),
            migration => Assert.EndsWith("_AddManualConflictResolution", migration, StringComparison.Ordinal),
            migration => Assert.EndsWith("_SupersedeOverlappingConflicts", migration, StringComparison.Ordinal),
            migration => Assert.EndsWith("_HybridConflictChainResolution", migration, StringComparison.Ordinal),
            migration => Assert.EndsWith("_ExpandInboxStateLength", migration, StringComparison.Ordinal));
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
    public void ManagementSettingsUseASingleExplicitKey()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(ManagementSettingsEntity))!;

        Assert.Equal(nameof(ManagementSettingsEntity.Id), Assert.Single(entity.FindPrimaryKey()!.Properties).Name);
        Assert.Equal(ValueGenerated.Never, entity.FindProperty(nameof(ManagementSettingsEntity.Id))!.ValueGenerated);
        Assert.NotNull(entity.FindProperty(nameof(ManagementSettingsEntity.LastAutomaticCleanupAtUtc)));
        Assert.NotNull(entity.FindProperty(nameof(ManagementSettingsEntity.AutomaticCleanupLeaseUntilUtc)));
        Assert.Null(entity.FindProperty("ResolvedConflictRetentionDays"));
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

    [Fact]
    public void ConflictsPersistResolutionStateAndConcurrencyToken()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(SyncConflictEntity))!;

        Assert.True(entity.FindProperty(nameof(SyncConflictEntity.RowVersion))!.IsConcurrencyToken);
        Assert.NotNull(entity.FindProperty(nameof(SyncConflictEntity.IncomingPayloadJson)));
        Assert.NotNull(entity.FindProperty(nameof(SyncConflictEntity.ResolutionRequestJson)));
        Assert.NotNull(entity.FindProperty(nameof(SyncConflictEntity.SupersededByConflictId)));
        Assert.NotNull(entity.FindProperty(nameof(SyncConflictEntity.SupersededAtUtc)));
        Assert.NotNull(entity.FindProperty(nameof(SyncConflictEntity.PreviousConflictId)));
        Assert.Contains(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(SyncConflictEntity.ResolutionState),
                nameof(SyncConflictEntity.DetectedAtUtc)
            ]));
        var recordChainIndex = Assert.Single(entity.GetIndexes(), index =>
            index.GetDatabaseName() == "IX_SyncConflict_RecordChain" &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(SyncConflictEntity.RouteId),
                nameof(SyncConflictEntity.DestinationSystem),
                nameof(SyncConflictEntity.EntityType),
                nameof(SyncConflictEntity.EntityId)
            ]));
        Assert.False(recordChainIndex.IsUnique);
        Assert.Null(recordChainIndex.GetFilter());
    }

    [Fact]
    public void InboxStateColumnFitsEveryPersistedStateName()
    {
        using var context = CreateContext();
        var property = context.Model.FindEntityType(typeof(InboxMessageEntity))!
            .FindProperty(nameof(InboxMessageEntity.State))!;
        var maxLength = property.GetMaxLength() ?? throw new InvalidOperationException();

        Assert.Equal(24, maxLength);
        Assert.All(Enum.GetNames<InboxState>(), name => Assert.True(name.Length <= maxLength));
    }

    private static CoordinatorDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoordinatorDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=SyncCoordinatorModelTests;Trusted_Connection=True")
            .Options;
        return new CoordinatorDbContext(options);
    }
}
