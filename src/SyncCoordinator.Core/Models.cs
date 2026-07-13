using System.Text.Json.Nodes;
using SyncCoordinator.Contracts;

namespace SyncCoordinator.Core;

public enum DestinationMode
{
    FixedSystem = 0,
    OriginSystem = 1
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

public sealed record SyncRouteDefinition(
    Guid Id,
    string Name,
    string SourceSystem,
    string EntityType,
    DestinationMode DestinationMode,
    string? DestinationSystem,
    ConflictScope ConflictScope,
    ConflictPolicy DefaultConflictPolicy,
    bool Enabled,
    IReadOnlyDictionary<string, ConflictPolicy> FieldPolicies);

public sealed record SyncSnapshot(
    Guid RouteId,
    string DestinationSystem,
    string EntityType,
    string EntityId,
    EntityPayload Payload);

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
    string DestinationSystem,
    string EntityType,
    string EntityId,
    DateTimeOffset DetectedAtUtc,
    bool Resolved);

public sealed record RouteListItem(
    Guid Id,
    string Name,
    string SourceSystem,
    string Destination,
    string EntityType,
    bool Enabled,
    ConflictScope ConflictScope,
    ConflictPolicy ConflictPolicy);

public sealed record SystemListItem(
    Guid Id,
    string Code,
    string DisplayName,
    string Provider,
    bool Enabled,
    bool ConnectionConfigured = false);

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
    Guid Id,
    Guid RouteId,
    string RouteName,
    string SourceSystem,
    string DestinationSystem,
    string SourceTable,
    string DestinationTable,
    int ColumnCount);

public sealed class TableMappingInput
{
    public Guid? Id { get; set; }
    public Guid RouteId { get; set; }
    public string DestinationSystem { get; set; } = string.Empty;
    public string SourceSchema { get; set; } = string.Empty;
    public string SourceTable { get; set; } = string.Empty;
    public string DestinationSchema { get; set; } = string.Empty;
    public string DestinationTable { get; set; } = string.Empty;
    public List<ColumnMappingInput> Columns { get; set; } = [];
}

public sealed class ColumnMappingInput
{
    public string SourceColumn { get; set; } = string.Empty;
    public string DestinationColumn { get; set; } = string.Empty;
    public bool IsKey { get; set; }
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
    public string EntityType { get; set; } = string.Empty;
    public DestinationMode DestinationMode { get; set; }
    public string? DestinationSystem { get; set; }
    public ConflictScope ConflictScope { get; set; }
    public ConflictPolicy DefaultConflictPolicy { get; set; }
    public bool Enabled { get; set; } = true;
    public List<FieldPolicyInput> FieldPolicies { get; set; } = [];
}

public sealed class FieldPolicyInput
{
    public string FieldName { get; set; } = string.Empty;
    public ConflictPolicy Policy { get; set; }
}

public sealed record InboxListItem(
    Guid SourceMessageId,
    string RouteName,
    string DestinationSystem,
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
    string DestinationSystem,
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
