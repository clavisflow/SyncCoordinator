namespace SyncCoordinator.Demo.Crm.Models;

public sealed record SyncEntity(
    string EntityId,
    string OriginSystem,
    DateTimeOffset UpdatedAtUtc,
    string PayloadJson);
