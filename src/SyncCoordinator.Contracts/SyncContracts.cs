using System.Text.Json.Nodes;

namespace SyncCoordinator.Contracts;

public enum ChangeOperation
{
    Upsert = 0,
    Delete = 1
}

public enum ApplyStatus
{
    Applied = 0,
    AlreadyApplied = 1
}

public enum DeletionMode
{
    Physical = 0,
    Logical = 1
}

public sealed record DeletionBehavior(
    DeletionMode Mode,
    string? LogicalDeleteColumn = null,
    string? LogicalDeleteValue = null);

/// <summary>
/// Connector 間の共通表現。Fields には Connector で明示的にマッピングした項目だけを含める。
/// </summary>
public sealed record EntityPayload(IReadOnlyDictionary<string, JsonNode?> Fields)
{
    public static EntityPayload Empty { get; } = new(new Dictionary<string, JsonNode?>());
}

public sealed record ChangeQueueItem(
    long QueueId,
    Guid MessageId,
    string EntityType,
    string EntityId,
    ChangeOperation Operation,
    DateTimeOffset OccurredAtUtc);

public sealed record SyncMessage(
    Guid SourceMessageId,
    string SourceSystem,
    string OriginSystem,
    string EntityType,
    string EntityId,
    ChangeOperation Operation,
    DateTimeOffset OccurredAtUtc,
    EntityPayload Payload);

public sealed record ApplyRequest(
    Guid DeliveryMessageId,
    Guid SourceMessageId,
    string SourceSystem,
    string OriginSystem,
    string EntityType,
    string EntityId,
    ChangeOperation Operation,
    DeletionBehavior? DeletionBehavior,
    EntityPayload Payload);

public sealed record ApplyResult(ApplyStatus Status);
