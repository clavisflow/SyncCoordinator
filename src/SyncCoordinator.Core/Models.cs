using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SyncCoordinator.Contracts;

namespace SyncCoordinator.Core;

public enum SyncDirection
{
    OneWay = 0,
    Bidirectional = 1
}

public enum MappingWriteDirection
{
    Forward = 0,
    Reverse = 1
}

public enum DatabaseDeploymentState
{
    Draft = 0,
    Prepared = 1
}

public enum ConflictPolicy
{
    HoldAndNotify = 0,
    ApplyIncomingAndNotify = 1,
    KeepCurrentAndNotify = 2,
    MergeAndNotify = 3
}

public enum ConflictScope
{
    Field = 0,
    Record = 1
}

public enum InboxState
{
    Processing = 0,
    Completed = 1,
    Held = 2,
    Failed = 3
}

public enum InboxAcquireResult
{
    Acquired = 0,
    AlreadyCompleted = 1,
    Busy = 2
}

public sealed record SyncRouteDefinition(
    Guid Id,
    string Name,
    string SourceSystem,
    string DestinationSystem,
    string EntityType,
    SyncDirection Direction,
    DeletionBehavior? SourceDeletionBehavior,
    DeletionBehavior? DestinationDeletionBehavior,
    ConflictScope ConflictScope,
    ConflictPolicy DefaultConflictPolicy,
    bool Enabled,
    IReadOnlyDictionary<string, ConflictPolicy> FieldPolicies)
{
    public bool OperationallyPaused { get; init; }

    public string? ResolveDestination(string currentSystem, string originSystem)
    {
        if (string.Equals(currentSystem, SourceSystem, StringComparison.OrdinalIgnoreCase))
        {
            return DestinationSystem;
        }

        return Direction == SyncDirection.Bidirectional &&
               string.Equals(currentSystem, DestinationSystem, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(originSystem, SourceSystem, StringComparison.OrdinalIgnoreCase)
            ? SourceSystem
            : null;
    }

    public DeletionBehavior? ResolveDeletionBehavior(string destinationSystem) =>
        string.Equals(destinationSystem, DestinationSystem, StringComparison.OrdinalIgnoreCase)
            ? DestinationDeletionBehavior
            : string.Equals(destinationSystem, SourceSystem, StringComparison.OrdinalIgnoreCase)
                ? SourceDeletionBehavior
                : null;
}

public sealed record SyncSnapshot(
    Guid RouteId,
    string DestinationSystem,
    string EntityType,
    string EntityId,
    EntityPayload? SourcePayload,
    EntityPayload? DestinationPayload);

public sealed record FieldConflict(
    string FieldName,
    JsonNode? BaseValue,
    JsonNode? IncomingValue,
    JsonNode? CurrentValue,
    JsonNode? AdoptedValue,
    ConflictPolicy Policy,
    string Resolution);

public sealed record ConflictResolution(
    EntityPayload AdoptedPayload,
    bool AdoptedExists,
    IReadOnlyList<FieldConflict> Conflicts,
    bool ShouldApply,
    bool IsHeld);

public sealed record ConflictHistory(
    Guid Id,
    Guid RouteId,
    Guid SourceMessageId,
    Guid DeliveryMessageId,
    string SourceSystem,
    string DestinationSystem,
    string EntityType,
    string EntityId,
    ConflictScope Scope,
    IReadOnlyList<FieldConflict> Fields,
    DateTimeOffset DetectedAtUtc);

public sealed record DashboardSummary(
    int EnabledSystems,
    int EnabledRoutes,
    int ProcessingMessages,
    int HeldMessages,
    int FailedMessages,
    int UnresolvedConflicts);

public sealed record ConflictListItem(
    Guid Id,
    string RouteName,
    string SourceSystem,
    string SourceSystemName,
    string DestinationSystem,
    string DestinationSystemName,
    string EntityType,
    string EntityId,
    DateTimeOffset DetectedAtUtc,
    bool Resolved);

public sealed record RouteListItem(
    Guid Id,
    string Name,
    string SourceSystem,
    string SourceSystemName,
    string DestinationSystem,
    string DestinationSystemName,
    SyncDirection Direction,
    DatabaseDeploymentState DeploymentState,
    bool Enabled,
    ConflictScope ConflictScope,
    ConflictPolicy ConflictPolicy)
{
    public bool OperationallyPaused { get; init; }
}

public sealed record DatabaseDeploymentPlan(
    Guid RouteId,
    string RouteName,
    DatabaseDeploymentState State,
    bool Enabled,
    bool DirectApplyAllowed,
    IReadOnlyList<DatabaseDeploymentTarget> Targets,
    IReadOnlyList<DisplayText> Warnings);

public sealed record DisplayText(string ResourceKey, IReadOnlyList<string> Arguments)
{
    public static DisplayText Create(string resourceKey, params string[] arguments) =>
        new(resourceKey, arguments);
}

public static class WebhookEventTypes
{
    public const string SyncUpserted = "sync.upserted";
    public const string SyncDeleted = "sync.deleted";
    public const string ConflictDetected = "conflict.detected";
    public const string SyncFailed = "sync.failed";
    public const string SystemPaused = "system.paused";
    public const string SystemResumed = "system.resumed";
    public const string Test = "webhook.test";

    public static readonly IReadOnlyList<string> All =
    [SyncUpserted, SyncDeleted, ConflictDetected, SyncFailed, SystemPaused, SystemResumed, Test];
}

public static class WebhookEventId
{
    public static Guid Create(string eventType, params object?[] identityParts)
    {
        var identity = eventType + "\n" + string.Join("\n", identityParts.Select(x => x?.ToString() ?? string.Empty));
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity))[..16]);
    }
}

public sealed record WebhookEventNotification(
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    Guid? RouteId = null,
    string? RouteName = null,
    string? SourceSystem = null,
    string? DestinationSystem = null,
    string? EntityType = null,
    string? EntityId = null,
    Guid? SourceMessageId = null,
    Guid? DeliveryMessageId = null,
    string? SystemCode = null,
    string? SystemName = null);

public sealed class WebhookEndpointInput
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool SignatureEnabled { get; set; } = true;
    public bool RegenerateSecret { get; set; }
    public List<string> EventTypes { get; set; } = WebhookEventTypes.All.ToList();
}

public sealed record WebhookEndpointListItem(
    Guid Id,
    string Name,
    string Url,
    bool Enabled,
    bool SignatureEnabled,
    bool SecretConfigured,
    IReadOnlyList<string> EventTypes,
    DateTimeOffset UpdatedAtUtc);

public sealed record WebhookEndpointSaveResult(Guid Id, string? NewSecret);

public sealed record WebhookDeliveryListItem(
    Guid Id,
    Guid EventId,
    string EndpointName,
    string EventType,
    string State,
    int AttemptCount,
    int? HttpStatusCode,
    string? LastError,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? DeliveredAtUtc);

public sealed record DatabaseDeploymentTarget(
    string SystemCode,
    string SystemName,
    string Provider,
    string DatabaseName,
    DisplayText DirectionLabel,
    string Script,
    IReadOnlyList<DisplayText> Changes);

public sealed record DatabaseDeploymentResult(
    bool Success,
    DisplayText Message,
    DatabaseDeploymentState State,
    bool Enabled);

public sealed record SystemListItem(
    Guid Id,
    string Code,
    string DisplayName,
    string Provider,
    bool Enabled,
    bool ConnectionConfigured = false,
    DateTimeOffset? PausedAtUtc = null)
{
    public bool IsPaused => PausedAtUtc is not null;
}

public sealed class DatabaseConnectionInput
{
    public Guid SystemId { get; set; }
    public string Server { get; set; } = string.Empty;
    public int? Port { get; set; }
    public string Database { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool IntegratedSecurity { get; set; }
    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; }
    public bool HasStoredPassword { get; set; }
}

public sealed record ConnectionTestResult(bool Success, string Message);

public sealed record DatabaseTableInfo(string Schema, string Name)
{
    public string DisplayName => $"{Schema}.{Name}";
}

public sealed record DatabaseColumnInfo(
    string Name,
    string DataType,
    bool IsNullable,
    int Ordinal,
    bool IsPrimaryKey)
{
    public string DisplayName => $"{Name} ({DataType}{(IsNullable ? ", NULL" : string.Empty)})";
}

public sealed record TableMappingListItem(
    Guid RouteId,
    string RouteName,
    string SourceSystem,
    string SourceSystemName,
    string DestinationSystem,
    string DestinationSystemName,
    string SourceTable,
    string DestinationTable,
    int ColumnCount);

public sealed class TableMappingInput
{
    public Guid RouteId { get; set; }
    public string SourceSchema { get; set; } = string.Empty;
    public string SourceTable { get; set; } = string.Empty;
    public string DestinationSchema { get; set; } = string.Empty;
    public string DestinationTable { get; set; } = string.Empty;
    public bool SyncDeletes { get; set; }
    public DeletionMode SourceDeletionMode { get; set; } = DeletionMode.Physical;
    public string SourceLogicalDeleteColumn { get; set; } = string.Empty;
    public string SourceLogicalDeleteValue { get; set; } = string.Empty;
    public DeletionMode DestinationDeletionMode { get; set; } = DeletionMode.Physical;
    public string DestinationLogicalDeleteColumn { get; set; } = string.Empty;
    public string DestinationLogicalDeleteValue { get; set; } = string.Empty;
    public List<ColumnMappingInput> Columns { get; set; } = [];
    public List<FixedValueMappingInput> FixedValues { get; set; } = [];
}

public sealed class ColumnMappingInput
{
    public string SourceColumn { get; set; } = string.Empty;
    public string DestinationColumn { get; set; } = string.Empty;
    public bool IsKey { get; set; }
    public ConflictPolicy? ConflictPolicy { get; set; }
}

public sealed class FixedValueMappingInput
{
    public MappingWriteDirection Direction { get; set; }
    public string TargetColumn { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class SystemConfigurationInput
{
    public Guid? Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Provider { get; set; } = "SqlServer";
    public bool Enabled { get; set; } = true;
}

public sealed class RouteConfigurationInput
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public string DestinationSystem { get; set; } = string.Empty;
    public SyncDirection Direction { get; set; }
    public ConflictScope ConflictScope { get; set; }
    public ConflictPolicy DefaultConflictPolicy { get; set; }
    public bool Enabled { get; set; } = true;
    public DatabaseDeploymentState DeploymentState { get; set; }
}

public sealed record InboxListItem(
    Guid SourceMessageId,
    string RouteName,
    string DestinationSystem,
    string DestinationSystemName,
    InboxState State,
    int AttemptCount,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? LastError);

public sealed record CheckpointListItem(
    string SystemCode,
    string SystemName,
    long LastQueueId,
    DateTimeOffset? UpdatedAtUtc);

public sealed record ConflictDetails(
    Guid Id,
    string RouteName,
    Guid SourceMessageId,
    Guid DeliveryMessageId,
    string SourceSystem,
    string SourceSystemName,
    string DestinationSystem,
    string DestinationSystemName,
    string EntityType,
    string EntityId,
    ConflictScope Scope,
    IReadOnlyList<FieldConflict> Fields,
    DateTimeOffset DetectedAtUtc,
    DateTimeOffset? ResolvedAtUtc);

public sealed record ConfigurationAuditListItem(
    Guid Id,
    string ConfigurationType,
    string ConfigurationName,
    string Action,
    string ChangedBy,
    DateTimeOffset ChangedAtUtc);

public sealed class ConfigurationValidationException(IReadOnlyList<string> errors)
    : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
