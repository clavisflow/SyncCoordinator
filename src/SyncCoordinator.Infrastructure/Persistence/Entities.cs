using SyncCoordinator.Core;

namespace SyncCoordinator.Infrastructure.Persistence;

public sealed class SystemDefinitionEntity
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string DisplayName { get; set; }
    public required string Provider { get; set; }
    public bool Enabled { get; set; }
    public string? ProtectedConnectionString { get; set; }
    public DateTimeOffset? ConnectionUpdatedAtUtc { get; set; }
}

public sealed class SyncRouteEntity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string SourceSystem { get; set; }
    public required string EntityType { get; set; }
    public DestinationMode DestinationMode { get; set; }
    public string? DestinationSystem { get; set; }
    public ConflictScope ConflictScope { get; set; }
    public ConflictPolicy DefaultConflictPolicy { get; set; }
    public bool Enabled { get; set; }
    public List<RouteFieldPolicyEntity> FieldPolicies { get; set; } = [];
    public List<RouteTableMappingEntity> TableMappings { get; set; } = [];
}

public sealed class RouteTableMappingEntity
{
    public Guid Id { get; set; }
    public Guid RouteId { get; set; }
    public required string DestinationSystem { get; set; }
    public required string SourceSchema { get; set; }
    public required string SourceTable { get; set; }
    public required string DestinationSchema { get; set; }
    public required string DestinationTable { get; set; }
    public SyncRouteEntity Route { get; set; } = null!;
    public List<RouteColumnMappingEntity> Columns { get; set; } = [];
}

public sealed class RouteColumnMappingEntity
{
    public Guid Id { get; set; }
    public Guid TableMappingId { get; set; }
    public required string SourceColumn { get; set; }
    public required string DestinationColumn { get; set; }
    public bool IsKey { get; set; }
    public RouteTableMappingEntity TableMapping { get; set; } = null!;
}

public sealed class RouteFieldPolicyEntity
{
    public Guid Id { get; set; }
    public Guid RouteId { get; set; }
    public required string FieldName { get; set; }
    public ConflictPolicy Policy { get; set; }
    public SyncRouteEntity Route { get; set; } = null!;
}

public sealed class InboxMessageEntity
{
    public Guid SourceMessageId { get; set; }
    public Guid RouteId { get; set; }
    public required string DestinationSystem { get; set; }
    public InboxState State { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset FirstSeenAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public string? LastError { get; set; }
}

public sealed class QueueCheckpointEntity
{
    public required string SystemCode { get; set; }
    public long LastQueueId { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class SyncSnapshotEntity
{
    public Guid RouteId { get; set; }
    public required string DestinationSystem { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class SyncConflictEntity
{
    public Guid Id { get; set; }
    public Guid RouteId { get; set; }
    public Guid SourceMessageId { get; set; }
    public Guid DeliveryMessageId { get; set; }
    public required string SourceSystem { get; set; }
    public required string DestinationSystem { get; set; }
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }
    public ConflictScope Scope { get; set; }
    public required string FieldsJson { get; set; }
    public DateTimeOffset DetectedAtUtc { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
    public SyncRouteEntity Route { get; set; } = null!;
}

public sealed class ConfigurationAuditEntity
{
    public Guid Id { get; set; }
    public required string ConfigurationType { get; set; }
    public required string ConfigurationId { get; set; }
    public required string ConfigurationName { get; set; }
    public required string Action { get; set; }
    public string? BeforeJson { get; set; }
    public required string AfterJson { get; set; }
    public required string ChangedBy { get; set; }
    public DateTimeOffset ChangedAtUtc { get; set; }
}
