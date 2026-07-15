namespace SyncCoordinator.Demo.Crm.Models;

public sealed record SupportCaseRecord(
    string EntityId,
    string OriginSystem,
    DateTimeOffset UpdatedAtUtc,
    SupportCasePayload Payload);

public sealed record WorkOrderRecord(
    string EntityId,
    string OriginSystem,
    DateTimeOffset UpdatedAtUtc,
    WorkOrderPayload Payload);

public sealed record DashboardSummary(
    int SupportCaseCount,
    int NewSupportCaseCount,
    int OpenWorkOrderCount,
    int CompletedWorkOrderCount);
