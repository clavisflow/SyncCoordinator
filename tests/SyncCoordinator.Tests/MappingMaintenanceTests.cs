using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SyncCoordinator.Core;
using SyncCoordinator.Infrastructure.Persistence;

namespace SyncCoordinator.Tests;

public sealed class MappingMaintenanceTests
{
    [Fact]
    public void NonKeyColumnChangesRequireDatabaseRedeployment()
    {
        var current = new[]
        {
            Column("Id", "CustomerId", isKey: true),
            Column("Name", "CustomerName", isKey: false)
        };
        var incoming = new[]
        {
            Input("Id", "CustomerId", isKey: true),
            Input("Name", "DisplayName", isKey: false)
        };

        Assert.True(EfCoordinatorAdminService.HasPhysicalColumnContractChanged(current, incoming));
    }

    [Fact]
    public void ConflictPolicyOnlyChangesDoNotRequireDatabaseRedeployment()
    {
        var current = new[]
        {
            Column("Id", "CustomerId", isKey: true),
            Column("Name", "CustomerName", isKey: false, ConflictPolicy.HoldAndNotify)
        };
        var incoming = new[]
        {
            Input("Id", "CustomerId", isKey: true),
            Input("Name", "CustomerName", isKey: false, ConflictPolicy.ApplyIncomingAndNotify)
        };

        Assert.False(EfCoordinatorAdminService.HasPhysicalColumnContractChanged(current, incoming));
        Assert.False(EfCoordinatorAdminService.HasValueSemanticsChanged(current, incoming));
    }

    [Fact]
    public void ValueTransformChangesResetCanonicalSnapshotsWithoutChangingPhysicalDeployment()
    {
        var current = new[] { Column("Status", "job_status", isKey: false) };
        var incoming = new[] { Input("Status", "job_status", isKey: false) };
        incoming[0].ForwardTransform.ValueMap.Add(new ValueMapEntryInput
        {
            SourceValue = "Completed",
            TargetValue = "done"
        });

        Assert.False(EfCoordinatorAdminService.HasPhysicalColumnContractChanged(current, incoming));
        Assert.True(EfCoordinatorAdminService.HasValueSemanticsChanged(current, incoming));
    }

    [Fact]
    public void OneWayRuleDropsUnusedReverseSettingsBeforeSaving()
    {
        var input = ValidMappingInput();
        input.Columns[0].ReverseTransform.UseNullFallback = true;
        input.Columns[0].ReverseTransform.NullFallback = "legacy";
        input.FixedValues =
        [
            new FixedValueMappingInput
            {
                Direction = MappingWriteDirection.Forward,
                TargetColumn = "ForwardOnly",
                Value = "forward"
            },
            new FixedValueMappingInput
            {
                Direction = MappingWriteDirection.Reverse,
                TargetColumn = "LegacyReverse",
                Value = "reverse"
            }
        ];

        EfCoordinatorAdminService.RemoveUnusedReverseSettings(input, SyncDirection.OneWay);

        Assert.True(input.Columns[0].ReverseTransform.IsIdentity);
        var fixedValue = Assert.Single(input.FixedValues);
        Assert.Equal(MappingWriteDirection.Forward, fixedValue.Direction);
        Assert.Equal("ForwardOnly", fixedValue.TargetColumn);
    }

    [Fact]
    public void BidirectionalRuleKeepsReverseSettings()
    {
        var input = ValidMappingInput();
        input.Columns[0].ReverseTransform.UseNullFallback = true;
        input.FixedValues =
        [
            new FixedValueMappingInput
            {
                Direction = MappingWriteDirection.Reverse,
                TargetColumn = "ReverseOnly",
                Value = "reverse"
            }
        ];

        EfCoordinatorAdminService.RemoveUnusedReverseSettings(input, SyncDirection.Bidirectional);

        Assert.False(input.Columns[0].ReverseTransform.IsIdentity);
        Assert.Equal(MappingWriteDirection.Reverse, Assert.Single(input.FixedValues).Direction);
    }

    [Fact]
    public async Task SavingValueTransformChangePreservesDeploymentAndDeletesCanonicalSnapshots()
    {
        var databaseName = $"SyncCoordinatorMappingTests_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<CoordinatorDbContext>()
            .UseSqlServer($"Server=(localdb)\\mssqllocaldb;Database={databaseName};Trusted_Connection=True")
            .Options;

        await using var context = new CoordinatorDbContext(options);
        try
        {
            await context.Database.EnsureCreatedAsync();
            var routeId = Guid.NewGuid();
            var source = CreateSystem("source");
            var destination = CreateSystem("destination");
            var route = new SyncRouteEntity
            {
                Id = routeId,
                Name = "Customers",
                SourceSystemId = source.Id,
                DestinationSystemId = destination.Id,
                EntityType = "Customer",
                Direction = SyncDirection.OneWay,
                ConflictScope = ConflictScope.Field,
                DefaultConflictPolicy = ConflictPolicy.HoldAndNotify,
                DeploymentState = DatabaseDeploymentState.Prepared,
                Enabled = true,
                SourceSystem = source,
                DestinationSystem = destination
            };
            var mapping = new RouteTableMappingEntity
            {
                RouteId = routeId,
                SourceSchema = "dbo",
                SourceTable = "Customer",
                DestinationSchema = "dbo",
                DestinationTable = "Customer",
                Route = route,
                Columns =
                [
                    Column("Id", "Id", isKey: true),
                    Column("Status", "Status", isKey: false)
                ]
            };
            foreach (var column in mapping.Columns)
            {
                column.TableMappingId = routeId;
                column.TableMapping = mapping;
            }
            route.TableMapping = mapping;
            context.Systems.AddRange(source, destination);
            context.Routes.Add(route);
            context.SyncSnapshots.Add(new SyncSnapshotEntity
            {
                RouteId = routeId,
                DestinationSystem = destination.Code,
                EntityType = route.EntityType,
                EntityId = "customer-1",
                SourcePayloadJson = "{}",
                DestinationPayloadJson = "{}",
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var input = new TableMappingInput
            {
                RouteId = routeId,
                SourceSchema = mapping.SourceSchema,
                SourceTable = mapping.SourceTable,
                SourceConditionExpression = "{source}.Status <> 'Draft'",
                DestinationSchema = mapping.DestinationSchema,
                DestinationTable = mapping.DestinationTable,
                Columns =
                [
                    Input("Id", "Id", isKey: true),
                    Input("Status", "Status", isKey: false)
                ],
                FixedValues =
                [
                    new FixedValueMappingInput
                    {
                        Direction = MappingWriteDirection.Forward,
                        TargetColumn = "SyncOrigin",
                        Value = "SyncCoordinator"
                    }
                ]
            };
            input.Columns[1].ForwardTransform.ValueMap.Add(new ValueMapEntryInput
            {
                SourceValue = "Completed",
                TargetValue = "done"
            });
            var service = new EfCoordinatorAdminService(
                context,
                new ProtectedConnectionStringService(new EphemeralDataProtectionProvider()),
                new WebhookOutboxWriter(context),
                TimeProvider.System);

            await service.SaveTableMappingAsync(input, CancellationToken.None);

            context.ChangeTracker.Clear();
            var savedRoute = await context.Routes.SingleAsync(x => x.Id == routeId);
            Assert.Equal(DatabaseDeploymentState.Prepared, savedRoute.DeploymentState);
            Assert.False(await context.SyncSnapshots.AnyAsync(x => x.RouteId == routeId));
            Assert.True((await context.RouteColumnMappings.SingleAsync(x =>
                x.TableMappingId == routeId && x.SourceColumn == "Status")).ForwardTransformJson is not null);
            Assert.Equal(
                "{source}.Status <> 'Draft'",
                (await context.RouteTableMappings.SingleAsync(x => x.RouteId == routeId)).SourceConditionExpression);
            Assert.Equal("SyncCoordinator", (await context.RouteFixedValueMappings.SingleAsync(x =>
                x.TableMappingId == routeId && x.TargetColumn == "SyncOrigin")).Value);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task FailedDrainRestoresRouteStateAndKeepsExistingMapping()
    {
        var databaseName = $"SyncCoordinatorMappingTests_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<CoordinatorDbContext>()
            .UseSqlServer($"Server=(localdb)\\mssqllocaldb;Database={databaseName};Trusted_Connection=True")
            .Options;
        var now = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

        await using var context = new CoordinatorDbContext(options);
        try
        {
            await context.Database.EnsureCreatedAsync();
            var routeId = Guid.NewGuid();
            var source = CreateSystem("source");
            var destination = CreateSystem("destination");
            var route = new SyncRouteEntity
            {
                Id = routeId,
                Name = "Customers",
                SourceSystemId = source.Id,
                DestinationSystemId = destination.Id,
                EntityType = "Customer",
                Direction = SyncDirection.OneWay,
                ConflictScope = ConflictScope.Field,
                DefaultConflictPolicy = ConflictPolicy.HoldAndNotify,
                DeploymentState = DatabaseDeploymentState.Prepared,
                Enabled = true,
                SourceSystem = source,
                DestinationSystem = destination
            };
            var mapping = new RouteTableMappingEntity
            {
                RouteId = routeId,
                SourceSchema = "dbo",
                SourceTable = "Customer",
                DestinationSchema = "dbo",
                DestinationTable = "Customer",
                Route = route,
                Columns = [Column("Id", "Id", isKey: true)]
            };
            mapping.Columns[0].TableMappingId = routeId;
            mapping.Columns[0].TableMapping = mapping;
            route.TableMapping = mapping;
            context.Systems.AddRange(source, destination);
            context.Routes.Add(route);
            context.InboxMessages.Add(new InboxMessageEntity
            {
                SourceMessageId = Guid.NewGuid(),
                RouteId = routeId,
                DestinationSystem = destination.Code,
                State = InboxState.Processing,
                AttemptCount = 1,
                FirstSeenAtUtc = now,
                UpdatedAtUtc = now,
                LockedUntilUtc = now.AddDays(1)
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var service = new EfCoordinatorAdminService(
                context,
                new ProtectedConnectionStringService(new EphemeralDataProtectionProvider()),
                new WebhookOutboxWriter(context),
                new AdvancingTimeProvider(now));

            await Assert.ThrowsAsync<ConfigurationValidationException>(() =>
                service.SaveTableMappingAsync(new TableMappingInput
                {
                    RouteId = routeId,
                    SourceSchema = "dbo",
                    SourceTable = "Customer",
                    DestinationSchema = "dbo",
                    DestinationTable = "Customer",
                    Columns = [Input("Id", "Id", isKey: true)]
                }, CancellationToken.None));

            context.ChangeTracker.Clear();
            var savedRoute = await context.Routes.SingleAsync(x => x.Id == routeId);
            Assert.True(savedRoute.Enabled);
            Assert.Null(savedRoute.MappingMaintenanceStartedAtUtc);
            Assert.Single(await context.RouteColumnMappings.Where(x => x.TableMappingId == routeId).ToListAsync());
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    private static RouteColumnMappingEntity Column(
        string source,
        string destination,
        bool isKey,
        ConflictPolicy? conflictPolicy = null) => new()
        {
            Id = Guid.NewGuid(),
            SourceColumn = source,
            DestinationColumn = destination,
            IsKey = isKey,
            ConflictPolicy = conflictPolicy
        };

    private static ColumnMappingInput Input(
        string source,
        string destination,
        bool isKey,
        ConflictPolicy? conflictPolicy = null) => new()
        {
            SourceColumn = source,
            DestinationColumn = destination,
            IsKey = isKey,
            ConflictPolicy = conflictPolicy
        };

    private static TableMappingInput ValidMappingInput() => new()
    {
        RouteId = Guid.NewGuid(),
        SourceSchema = "dbo",
        SourceTable = "Source",
        DestinationSchema = "dbo",
        DestinationTable = "Destination",
        Columns = [Input("Id", "Id", isKey: true)]
    };

    private static SystemDefinitionEntity CreateSystem(string code) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        DisplayName = code,
        Provider = "SqlServer",
        Enabled = true
    };

    private sealed class AdvancingTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private int calls;

        public override DateTimeOffset GetUtcNow() =>
            start.AddSeconds(20 * Interlocked.Increment(ref calls));
    }
}
