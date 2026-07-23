using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
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

public enum SyncFieldDirection
{
    Forward = 0,
    Reverse = 1,
    Bidirectional = 2
}

public enum RelatedTableUsage
{
    Projection = 0,
    Eligibility = 1
}

public enum DatabaseDeploymentState
{
    Draft = 0,
    Prepared = 1
}

public enum DatabaseDeploymentTargetStatus
{
    Unknown = 0,
    Applied = 1,
    NotApplied = 2,
    Unavailable = 3
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

public enum ConflictResolutionState
{
    Resolved = 0,
    AwaitingDecision = 1,
    Pending = 2,
    Processing = 3,
    Failed = 4,
    Superseded = 5,
    WaitingForPrevious = 6
}

public enum ManualConflictChoice
{
    Incoming = 0,
    Current = 1,
    Custom = 2
}

public enum InboxState
{
    Processing = 0,
    Completed = 1,
    Held = 2,
    Failed = 3,
    Superseded = 4,
    WaitingForPrevious = 5
}

public enum InboxAcquireResult
{
    Acquired = 0,
    AlreadyCompleted = 1,
    Busy = 2,
    RoutePaused = 3
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
    public bool MappingMaintenance { get; init; }
    public IReadOnlyDictionary<string, ColumnValueMappingDefinition> ValueMappings { get; init; } =
        new Dictionary<string, ColumnValueMappingDefinition>(StringComparer.Ordinal);

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
    string Resolution)
{
    public string? IncomingFieldName { get; init; }
    public string? CurrentFieldName { get; init; }
    public JsonNode? LatestCurrentValue { get; init; }
    public bool CurrentChanged { get; init; }
}

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
    ChangeOperation Operation,
    ConflictScope Scope,
    IReadOnlyList<FieldConflict> Fields,
    bool RequiresDecision,
    SyncSnapshot? Baseline,
    EntityPayload IncomingPayload,
    EntityPayload? CurrentPayload,
    DateTimeOffset DetectedAtUtc);

public sealed record DashboardSummary(
    int EnabledSystems,
    int EnabledRoutes,
    int ProcessingMessages,
    int AttentionConflicts,
    int ValueTransformationErrors,
    int FailedMessages);

public sealed class ManagementSettings
{
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
}

public sealed record ManagementCleanupCounts(
    long CompletedInbox,
    long DeliveredWebhooks,
    long FailedWebhooks,
    long AcknowledgedOperationalEvents,
    long ConfigurationAudits)
{
    public long Total => CompletedInbox + DeliveredWebhooks + FailedWebhooks +
                         AcknowledgedOperationalEvents + ConfigurationAudits;
}

public sealed record ManagementCleanupPreview(
    ManagementCleanupCounts Counts,
    DateTimeOffset CalculatedAtUtc);

public sealed record ManagementCleanupResult(
    ManagementCleanupCounts Deleted,
    DateTimeOffset CompletedAtUtc,
    bool Automatic);

public sealed record ConflictListItem(
    Guid Id,
    string RouteName,
    string SourceSystem,
    string SourceSystemName,
    string DestinationSystem,
    string DestinationSystemName,
    string EntityType,
    string EntityId,
    ChangeOperation Operation,
    DateTimeOffset DetectedAtUtc,
    ConflictResolutionState ResolutionState,
    DateTimeOffset? ResolvedAtUtc);

public sealed record ConflictStateCounts(
    long AwaitingDecision,
    long WaitingForPrevious,
    long Pending,
    long Processing,
    long Failed,
    long Resolved,
    long Superseded);

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
    public bool MappingMaintenance { get; init; }
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
    IReadOnlyList<DisplayText> Changes,
    DatabaseDeploymentTargetStatus Status = DatabaseDeploymentTargetStatus.Unknown);

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

public sealed record ConnectionTestResult(bool Success, string Message, bool HasWarning = false);

public sealed record DatabaseTableInfo(string Schema, string Name)
{
    public string DisplayName => $"{Schema}.{Name}";
}

public sealed record DatabaseColumnInfo(
    string Name,
    string DataType,
    bool IsNullable,
    int Ordinal,
    bool IsPrimaryKey,
    int? MaxLength = null,
    int? NumericPrecision = null,
    int? NumericScale = null)
{
    public ColumnValueContract Contract =>
        new(DataType, IsNullable, MaxLength, NumericPrecision, NumericScale);

    public string DisplayName => $"{Name} ({Contract.DisplayType}{(IsNullable ? ", NULL" : string.Empty)})";
}

public sealed record ColumnValueContract(
    string DataType,
    bool IsNullable,
    int? MaxLength = null,
    int? NumericPrecision = null,
    int? NumericScale = null)
{
    public static ColumnValueContract Unknown { get; } = new(string.Empty, true);

    [JsonIgnore]
    public bool IsKnown => !string.IsNullOrWhiteSpace(DataType);

    [JsonIgnore]
    public string DisplayType => MaxLength is { } maxLength
        ? $"{DataType}({maxLength})"
        : NumericPrecision is { } precision
            ? NumericScale is { } scale
                ? $"{DataType}({precision},{scale})"
                : $"{DataType}({precision})"
            : DataType;
}

public enum StringOverflowBehavior
{
    Reject = 0,
    Truncate = 1
}

public enum NumericScaleBehavior
{
    Reject = 0,
    Round = 1
}

public sealed class ValueMapEntryInput
{
    public string SourceValue { get; set; } = string.Empty;
    public string TargetValue { get; set; } = string.Empty;
}

public sealed class ValueTransformInput
{
    public bool UseNullFallback { get; set; }
    public string NullFallback { get; set; } = string.Empty;
    public StringOverflowBehavior StringOverflow { get; set; } = StringOverflowBehavior.Reject;
    public NumericScaleBehavior NumericScale { get; set; } = NumericScaleBehavior.Reject;
    public bool NormalizeDateTimeToUtc { get; set; }
    public bool RejectUnmappedValues { get; set; }
    public List<ValueMapEntryInput> ValueMap { get; set; } = [];

    [JsonIgnore]
    public bool IsIdentity => !UseNullFallback &&
                              StringOverflow == StringOverflowBehavior.Reject &&
                              NumericScale == NumericScaleBehavior.Reject &&
                              !NormalizeDateTimeToUtc &&
                              !RejectUnmappedValues &&
                              ValueMap.Count == 0;
}

public sealed record ColumnValueMappingDefinition(
    string FieldName,
    string DestinationColumn,
    ColumnValueContract SourceContract,
    ColumnValueContract DestinationContract,
    ValueTransformInput ForwardTransform,
    ValueTransformInput ReverseTransform)
{
    public SyncFieldDirection Direction { get; init; } = SyncFieldDirection.Bidirectional;

    public bool Allows(MappingWriteDirection writeDirection) => Direction switch
    {
        SyncFieldDirection.Forward => writeDirection == MappingWriteDirection.Forward,
        SyncFieldDirection.Reverse => writeDirection == MappingWriteDirection.Reverse,
        SyncFieldDirection.Bidirectional => true,
        _ => false
    };
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
    public string SourceConditionExpression { get; set; } = string.Empty;
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
    public List<RelatedTableInput> RelatedTables { get; set; } = [];
}

public sealed class ColumnMappingInput
{
    public string SourceTableAlias { get; set; } = string.Empty;
    public string SourceColumn { get; set; } = string.Empty;
    public string DestinationColumn { get; set; } = string.Empty;
    public SyncFieldDirection? Direction { get; set; }
    public bool IsKey { get; set; }
    public ConflictPolicy? ConflictPolicy { get; set; }
    public ColumnValueContract SourceContract { get; set; } = ColumnValueContract.Unknown;
    public ColumnValueContract DestinationContract { get; set; } = ColumnValueContract.Unknown;
    public ValueTransformInput ForwardTransform { get; set; } = new();
    public ValueTransformInput ReverseTransform { get; set; } = new();
}

public sealed class RelatedTableInput
{
    public string Schema { get; set; } = string.Empty;
    public string Table { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string JoinExpression { get; set; } = string.Empty;
    public RelatedTableUsage Usage { get; set; }
    public bool DetectChanges { get; set; } = true;
    public string ConditionExpression { get; set; } = string.Empty;
}

public sealed class FixedValueMappingInput
{
    public MappingWriteDirection Direction { get; set; }
    public string TargetColumn { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsKey { get; set; }
    public ColumnValueContract TargetContract { get; set; } = ColumnValueContract.Unknown;
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
    string? LastError,
    Guid? ConflictId);

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
    ChangeOperation Operation,
    ConflictScope Scope,
    IReadOnlyList<FieldConflict> Fields,
    DateTimeOffset DetectedAtUtc,
    ConflictResolutionState ResolutionState,
    string? CurrentVersionToken,
    string? ResolutionComment,
    string? ResolutionLastError,
    string? RequestedBy,
    DateTimeOffset? RequestedAtUtc,
    string? ResolvedBy,
    DateTimeOffset? ResolvedAtUtc,
    Guid? SupersededByConflictId,
    DateTimeOffset? SupersededAtUtc,
    Guid? PreviousConflictId,
    Guid? OldestActiveConflictId,
    Guid? LatestActiveConflictId,
    int OlderActiveConflictCount,
    int NewerActiveConflictCount,
    bool CanResolve);

public sealed class ConflictResolutionInput
{
    public string ExpectedCurrentVersionToken { get; set; } = string.Empty;
    public ManualConflictChoice? DeleteChoice { get; set; }
    public List<FieldResolutionInput> Fields { get; set; } = [];
    public string? Comment { get; set; }
}

public sealed class FieldResolutionInput
{
    public string FieldName { get; set; } = string.Empty;
    public ManualConflictChoice Choice { get; set; }
    public JsonNode? CustomValue { get; set; }
}

public sealed record ConfigurationAuditListItem(
    Guid Id,
    string ConfigurationType,
    string ConfigurationName,
    string Action,
    string ChangedBy,
    DateTimeOffset ChangedAtUtc);

public enum OperationalEventSeverity
{
    Warning = 0,
    Error = 1,
    Critical = 2
}

public static class OperationalEventCategories
{
    public const string Application = "Application";
    public const string Database = "Database";
    public const string Synchronization = "Synchronization";
    public const string Webhook = "Webhook";
}

public static class OperationalEventCodes
{
    public const string ApplicationUiOperationFailed = "application.ui-operation-failed";
    public const string DatabaseConnectionTestFailed = "database.connection-test-failed";
    public const string DatabaseDeploymentFailed = "database.deployment-failed";
    public const string DatabaseVerificationFailed = "database.verification-failed";
    public const string SynchronizationPollingFailed = "synchronization.polling-failed";
    public const string SynchronizationValueValidationHeld = "synchronization.value-validation-held";
    public const string WebhookDeliveryFailed = "webhook.delivery-failed";
}

public sealed record OperationalEventInput(
    OperationalEventSeverity Severity,
    string Category,
    string Code,
    string Source,
    string? Target,
    string? Details,
    string? CorrelationId = null);

public sealed record OperationalEventListItem(
    Guid Id,
    OperationalEventSeverity Severity,
    string Category,
    string Code,
    string Source,
    string? Target,
    string? Details,
    string? CorrelationId,
    DateTimeOffset FirstOccurredAtUtc,
    DateTimeOffset LastOccurredAtUtc,
    int OccurrenceCount,
    DateTimeOffset? AcknowledgedAtUtc,
    string? AcknowledgedBy);

public sealed class ConfigurationValidationException(IReadOnlyList<string> errors)
    : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
