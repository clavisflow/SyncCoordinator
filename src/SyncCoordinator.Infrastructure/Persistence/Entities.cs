using SyncCoordinator.Contracts;
using SyncCoordinator.Core;

namespace SyncCoordinator.Infrastructure.Persistence;

public sealed class AdminAccountEntity
{
    public Guid Id { get; set; }
    public required string UserName { get; set; }
    public required string PasswordHash { get; set; }
    public int SessionVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ManagementSettingsEntity
{
    public int Id { get; set; }
    public bool GlobalPaused { get; set; }
    public int PollingIntervalSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 100;
    public int CompletedInboxRetentionDays { get; set; } = 90;
    public int DeliveredWebhookRetentionDays { get; set; } = 30;
    public int FailedWebhookRetentionDays { get; set; } = 90;
    public int AcknowledgedOperationalEventRetentionDays { get; set; } = 90;
    public int ConfigurationAuditRetentionDays { get; set; } = 365;
    public DateTimeOffset? LastAutomaticCleanupAtUtc { get; set; }
    public DateTimeOffset? LastManualCleanupAtUtc { get; set; }
    public DateTimeOffset? AutomaticCleanupLeaseUntilUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class SystemDefinitionEntity
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string DisplayName { get; set; }
    public required string Provider { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset? PausedAtUtc { get; set; }
    public string? ProtectedConnectionString { get; set; }
    public DateTimeOffset? ConnectionUpdatedAtUtc { get; set; }
}

public sealed class SyncRouteEntity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public Guid SourceSystemId { get; set; }
    public required string EntityType { get; set; }
    public Guid DestinationSystemId { get; set; }
    public SyncDirection Direction { get; set; }
    public ConflictScope ConflictScope { get; set; }
    public ConflictPolicy DefaultConflictPolicy { get; set; }
    public DatabaseDeploymentState DeploymentState { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset? MappingMaintenanceStartedAtUtc { get; set; }
    public SystemDefinitionEntity SourceSystem { get; set; } = null!;
    public SystemDefinitionEntity DestinationSystem { get; set; } = null!;
    public RouteTableMappingEntity? TableMapping { get; set; }
}

public sealed class RouteTableMappingEntity
{
    public Guid RouteId { get; set; }
    public required string SourceSchema { get; set; }
    public required string SourceTable { get; set; }
    public required string DestinationSchema { get; set; }
    public required string DestinationTable { get; set; }
    public bool SyncDeletes { get; set; }
    public DeletionMode SourceDeletionMode { get; set; }
    public string? SourceLogicalDeleteColumn { get; set; }
    public string? SourceLogicalDeleteValue { get; set; }
    public DeletionMode DestinationDeletionMode { get; set; }
    public string? DestinationLogicalDeleteColumn { get; set; }
    public string? DestinationLogicalDeleteValue { get; set; }
    public SyncRouteEntity Route { get; set; } = null!;
    public List<RouteColumnMappingEntity> Columns { get; set; } = [];
    public List<RouteFixedValueMappingEntity> FixedValues { get; set; } = [];
}

public sealed class RouteColumnMappingEntity
{
    public Guid Id { get; set; }
    public Guid TableMappingId { get; set; }
    public required string SourceColumn { get; set; }
    public required string DestinationColumn { get; set; }
    public bool IsKey { get; set; }
    public ConflictPolicy? ConflictPolicy { get; set; }
    public string SourceDataType { get; set; } = string.Empty;
    public bool SourceIsNullable { get; set; } = true;
    public int? SourceMaxLength { get; set; }
    public int? SourceNumericPrecision { get; set; }
    public int? SourceNumericScale { get; set; }
    public string DestinationDataType { get; set; } = string.Empty;
    public bool DestinationIsNullable { get; set; } = true;
    public int? DestinationMaxLength { get; set; }
    public int? DestinationNumericPrecision { get; set; }
    public int? DestinationNumericScale { get; set; }
    public string? ForwardTransformJson { get; set; }
    public string? ReverseTransformJson { get; set; }
    public RouteTableMappingEntity TableMapping { get; set; } = null!;
}

public sealed class RouteFixedValueMappingEntity
{
    public Guid Id { get; set; }
    public Guid TableMappingId { get; set; }
    public MappingWriteDirection Direction { get; set; }
    public required string TargetColumn { get; set; }
    public required string Value { get; set; }
    public string TargetDataType { get; set; } = string.Empty;
    public bool TargetIsNullable { get; set; } = true;
    public int? TargetMaxLength { get; set; }
    public int? TargetNumericPrecision { get; set; }
    public int? TargetNumericScale { get; set; }
    public RouteTableMappingEntity TableMapping { get; set; } = null!;
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
    public string? SourcePayloadJson { get; set; }
    public string? DestinationPayloadJson { get; set; }
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
    public ChangeOperation Operation { get; set; }
    public ConflictScope Scope { get; set; }
    public required string FieldsJson { get; set; }
    public bool HadBaseline { get; set; }
    public string? BaselineSourcePayloadJson { get; set; }
    public string? BaselineDestinationPayloadJson { get; set; }
    public required string IncomingPayloadJson { get; set; }
    public string? CurrentPayloadJson { get; set; }
    public DateTimeOffset DetectedAtUtc { get; set; }
    public ConflictResolutionState ResolutionState { get; set; }
    public string? ResolutionRequestJson { get; set; }
    public string? ResolutionComment { get; set; }
    public string? RequestedBy { get; set; }
    public DateTimeOffset? RequestedAtUtc { get; set; }
    public DateTimeOffset? ResolutionLockedUntilUtc { get; set; }
    public int ResolutionAttemptCount { get; set; }
    public string? ResolutionLastError { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
    public Guid? PreviousConflictId { get; set; }
    public Guid? SupersededByConflictId { get; set; }
    public DateTimeOffset? SupersededAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
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

public sealed class OperationalEventEntity
{
    public Guid Id { get; set; }
    public OperationalEventSeverity Severity { get; set; }
    public required string Category { get; set; }
    public required string Code { get; set; }
    public required string Source { get; set; }
    public string? Target { get; set; }
    public string? Details { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset FirstOccurredAtUtc { get; set; }
    public DateTimeOffset LastOccurredAtUtc { get; set; }
    public int OccurrenceCount { get; set; }
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
    public string? AcknowledgedBy { get; set; }
}

public sealed class WebhookEndpointEntity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Url { get; set; }
    public bool Enabled { get; set; }
    public bool SignatureEnabled { get; set; }
    public string? ProtectedSecret { get; set; }
    public required string EventTypesJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public List<WebhookDeliveryEntity> Deliveries { get; set; } = [];
}

public sealed class WebhookEventEntity
{
    public Guid Id { get; set; }
    public required string EventType { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public required string PayloadJson { get; set; }
    public List<WebhookDeliveryEntity> Deliveries { get; set; } = [];
}

public sealed class WebhookDeliveryEntity
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Guid EndpointId { get; set; }
    public required string State { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAtUtc { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public DateTimeOffset? DeliveredAtUtc { get; set; }
    public int? HttpStatusCode { get; set; }
    public string? LastError { get; set; }
    public WebhookEventEntity Event { get; set; } = null!;
    public WebhookEndpointEntity Endpoint { get; set; } = null!;
}
