using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SyncCoordinator.Contracts;
using SyncCoordinator.Core;

namespace SyncCoordinator.Infrastructure.Persistence;

public sealed class EfCoordinatorStore(
    CoordinatorDbContext dbContext,
    WebhookOutboxWriter webhookOutbox,
    TimeProvider timeProvider) : ICoordinatorStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<bool> IsSystemPausedAsync(string systemCode, CancellationToken cancellationToken) =>
        dbContext.Systems.AsNoTracking()
            .AnyAsync(x => x.Code == systemCode && x.PausedAtUtc != null, cancellationToken);

    public async Task<long> GetCheckpointAsync(string systemCode, CancellationToken cancellationToken) =>
        await dbContext.QueueCheckpoints
            .Where(x => x.SystemCode == systemCode)
            .Select(x => (long?)x.LastQueueId)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;

    public async Task AdvanceCheckpointAsync(
        string systemCode,
        long queueId,
        CancellationToken cancellationToken)
    {
        var checkpoint = await dbContext.QueueCheckpoints.FindAsync([systemCode], cancellationToken);
        if (checkpoint is null)
        {
            dbContext.QueueCheckpoints.Add(new QueueCheckpointEntity
            {
                SystemCode = systemCode,
                LastQueueId = queueId,
                UpdatedAtUtc = timeProvider.GetUtcNow()
            });
        }
        else if (queueId > checkpoint.LastQueueId)
        {
            checkpoint.LastQueueId = queueId;
            checkpoint.UpdatedAtUtc = timeProvider.GetUtcNow();
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SyncRouteDefinition>> GetRoutesAsync(
        string sourceSystem,
        string originSystem,
        string entityType,
        CancellationToken cancellationToken)
    {
        var routes = await dbContext.Routes
            .AsNoTracking()
            .Include(x => x.SourceSystem)
            .Include(x => x.DestinationSystem)
            .Include(x => x.TableMapping).ThenInclude(x => x!.Columns)
            .Where(x => (x.Enabled || x.MappingMaintenanceStartedAtUtc != null) &&
                        x.EntityType == entityType &&
                        (x.SourceSystem.Code == sourceSystem ||
                         x.Direction == SyncDirection.Bidirectional &&
                         x.DestinationSystem.Code == sourceSystem &&
                         x.SourceSystem.Code == originSystem))
            .ToListAsync(cancellationToken);

        return routes.Select(x =>
        {
            var mapping = x.TableMapping ??
                          throw new InvalidOperationException($"有効な同期ルール '{x.Name}' にテーブルマッピングがありません。");
            return new SyncRouteDefinition(
                x.Id,
                x.Name,
                x.SourceSystem.Code,
                x.DestinationSystem.Code,
                x.EntityType,
                x.Direction,
                ToDeletionBehavior(mapping.SyncDeletes, mapping.SourceDeletionMode, mapping.SourceLogicalDeleteColumn, mapping.SourceLogicalDeleteValue),
                ToDeletionBehavior(mapping.SyncDeletes, mapping.DestinationDeletionMode, mapping.DestinationLogicalDeleteColumn, mapping.DestinationLogicalDeleteValue),
                x.ConflictScope,
                x.DefaultConflictPolicy,
                x.Enabled,
                mapping.Columns
                    .Where(y => y.ConflictPolicy is not null)
                    .ToDictionary(y => y.SourceColumn, y => y.ConflictPolicy!.Value, StringComparer.Ordinal))
            {
                OperationallyPaused = x.SourceSystem.PausedAtUtc is not null ||
                                      x.DestinationSystem.PausedAtUtc is not null ||
                                      x.MappingMaintenanceStartedAtUtc is not null,
                MappingMaintenance = x.MappingMaintenanceStartedAtUtc is not null,
                ValueMappings = mapping.Columns.ToDictionary(
                    column => column.SourceColumn,
                    column => new ColumnValueMappingDefinition(
                        column.SourceColumn,
                        column.DestinationColumn,
                        SourceContract(column),
                        DestinationContract(column),
                        DeserializeTransform(column.ForwardTransformJson),
                        DeserializeTransform(column.ReverseTransformJson)),
                    StringComparer.Ordinal)
            };
        }).ToArray();
    }

    private static DeletionBehavior? ToDeletionBehavior(
        bool enabled,
        DeletionMode mode,
        string? logicalDeleteColumn,
        string? logicalDeleteValue) =>
        enabled ? new DeletionBehavior(mode, logicalDeleteColumn, logicalDeleteValue) : null;

    public async Task<InboxAcquireResult> TryBeginInboxAsync(
        Guid sourceMessageId,
        Guid routeId,
        string destinationSystem,
        CancellationToken cancellationToken)
    {
        var routeState = await dbContext.Routes.AsNoTracking()
            .Where(x => x.Id == routeId)
            .Select(x => new { x.Enabled, x.MappingMaintenanceStartedAtUtc })
            .SingleOrDefaultAsync(cancellationToken);
        if (routeState is null ||
            !routeState.Enabled ||
            routeState.MappingMaintenanceStartedAtUtc is not null)
        {
            return InboxAcquireResult.RoutePaused;
        }

        var key = new object[] { sourceMessageId, routeId, destinationSystem };
        var item = await dbContext.InboxMessages.FindAsync(key, cancellationToken);
        var now = timeProvider.GetUtcNow();

        if (item is null)
        {
            dbContext.InboxMessages.Add(new InboxMessageEntity
            {
                SourceMessageId = sourceMessageId,
                RouteId = routeId,
                DestinationSystem = destinationSystem,
                State = InboxState.Processing,
                AttemptCount = 1,
                FirstSeenAtUtc = now,
                UpdatedAtUtc = now,
                LockedUntilUtc = now.AddMinutes(5)
            });
        }
        else
        {
            if (item.State is InboxState.Completed or InboxState.Held)
            {
                return InboxAcquireResult.AlreadyCompleted;
            }
            if (item.State == InboxState.Processing && item.LockedUntilUtc > now)
            {
                return InboxAcquireResult.Busy;
            }

            item.State = InboxState.Processing;
            item.AttemptCount++;
            item.UpdatedAtUtc = now;
            item.LockedUntilUtc = now.AddMinutes(5);
            item.LastError = null;
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return InboxAcquireResult.Acquired;
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            return InboxAcquireResult.Busy;
        }
    }

    public Task CompleteInboxAsync(
        Guid sourceMessageId,
        Guid routeId,
        string destinationSystem,
        InboxState state,
        WebhookEventNotification? webhookEvent,
        CancellationToken cancellationToken) =>
        UpdateInboxAsync(sourceMessageId, routeId, destinationSystem, state, null, webhookEvent, cancellationToken);

    public Task FailInboxAsync(
        Guid sourceMessageId,
        Guid routeId,
        string destinationSystem,
        string errorDetails,
        WebhookEventNotification webhookEvent,
        CancellationToken cancellationToken) =>
        UpdateInboxAsync(sourceMessageId, routeId, destinationSystem, InboxState.Failed, errorDetails, webhookEvent, cancellationToken);

    public Task HoldInboxAsync(
        Guid sourceMessageId,
        Guid routeId,
        string destinationSystem,
        string errorDetails,
        WebhookEventNotification webhookEvent,
        CancellationToken cancellationToken) =>
        UpdateInboxAsync(sourceMessageId, routeId, destinationSystem, InboxState.Held, errorDetails, webhookEvent, cancellationToken);

    public async Task<SyncSnapshot?> GetSnapshotAsync(
        Guid routeId,
        string destinationSystem,
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.SyncSnapshots.AsNoTracking().SingleOrDefaultAsync(
            x => x.RouteId == routeId &&
                 x.DestinationSystem == destinationSystem &&
                 x.EntityType == entityType &&
                 x.EntityId == entityId,
            cancellationToken);
        return entity is null
            ? null
            : new SyncSnapshot(
                routeId,
                destinationSystem,
                entityType,
                entityId,
                DeserializePayload(entity.SourcePayloadJson),
                DeserializePayload(entity.DestinationPayloadJson));
    }

    public async Task SaveSnapshotAsync(SyncSnapshot snapshot, CancellationToken cancellationToken)
    {
        var key = new object[]
        {
            snapshot.RouteId, snapshot.DestinationSystem, snapshot.EntityType, snapshot.EntityId
        };
        var entity = await dbContext.SyncSnapshots.FindAsync(key, cancellationToken);
        if (entity is null)
        {
            entity = new SyncSnapshotEntity
            {
                RouteId = snapshot.RouteId,
                DestinationSystem = snapshot.DestinationSystem,
                EntityType = snapshot.EntityType,
                EntityId = snapshot.EntityId,
                SourcePayloadJson = SerializePayload(snapshot.SourcePayload),
                DestinationPayloadJson = SerializePayload(snapshot.DestinationPayload),
                UpdatedAtUtc = timeProvider.GetUtcNow()
            };
            dbContext.SyncSnapshots.Add(entity);
        }
        else
        {
            entity.SourcePayloadJson = SerializePayload(snapshot.SourcePayload);
            entity.DestinationPayloadJson = SerializePayload(snapshot.DestinationPayload);
            entity.UpdatedAtUtc = timeProvider.GetUtcNow();
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveConflictAsync(
        ConflictHistory conflict,
        WebhookEventNotification webhookEvent,
        CancellationToken cancellationToken)
    {
        if (!await dbContext.SyncConflicts.AnyAsync(x => x.Id == conflict.Id, cancellationToken))
        {
            dbContext.SyncConflicts.Add(new SyncConflictEntity
            {
                Id = conflict.Id,
                RouteId = conflict.RouteId,
                SourceMessageId = conflict.SourceMessageId,
                DeliveryMessageId = conflict.DeliveryMessageId,
                SourceSystem = conflict.SourceSystem,
                DestinationSystem = conflict.DestinationSystem,
                EntityType = conflict.EntityType,
                EntityId = conflict.EntityId,
                Scope = conflict.Scope,
                FieldsJson = JsonSerializer.Serialize(conflict.Fields, JsonOptions),
                DetectedAtUtc = conflict.DetectedAtUtc
            });
        }
        await webhookOutbox.AddAsync(webhookEvent, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task UpdateInboxAsync(
        Guid sourceMessageId,
        Guid routeId,
        string destinationSystem,
        InboxState state,
        string? error,
        WebhookEventNotification? webhookEvent,
        CancellationToken cancellationToken)
    {
        var item = await dbContext.InboxMessages.FindAsync(
            [sourceMessageId, routeId, destinationSystem],
            cancellationToken) ?? throw new InvalidOperationException("Inbox message was not claimed.");
        item.State = state;
        item.LastError = error is null ? null : error[..Math.Min(error.Length, 4000)];
        item.UpdatedAtUtc = timeProvider.GetUtcNow();
        item.LockedUntilUtc = null;
        if (webhookEvent is not null)
        {
            await webhookOutbox.AddAsync(webhookEvent, cancellationToken);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string? SerializePayload(EntityPayload? payload) =>
        payload is null ? null : JsonSerializer.Serialize(payload.Fields, JsonOptions);

    private static EntityPayload? DeserializePayload(string? json) =>
        json is null
            ? null
            : new EntityPayload(JsonSerializer.Deserialize<Dictionary<string, JsonNode?>>(json, JsonOptions) ?? []);

    private static ColumnValueContract SourceContract(RouteColumnMappingEntity column) => new(
        column.SourceDataType,
        column.SourceIsNullable,
        column.SourceMaxLength,
        column.SourceNumericPrecision,
        column.SourceNumericScale);

    private static ColumnValueContract DestinationContract(RouteColumnMappingEntity column) => new(
        column.DestinationDataType,
        column.DestinationIsNullable,
        column.DestinationMaxLength,
        column.DestinationNumericPrecision,
        column.DestinationNumericScale);

    private static ValueTransformInput DeserializeTransform(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new ValueTransformInput()
            : JsonSerializer.Deserialize<ValueTransformInput>(json, JsonOptions) ?? new ValueTransformInput();
}
