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
}
