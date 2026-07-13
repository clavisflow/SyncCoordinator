using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SyncCoordinator.Contracts;
using SyncCoordinator.Core;

namespace SyncCoordinator.Infrastructure.Persistence;

public sealed class EfCoordinatorStore(
    CoordinatorDbContext dbContext,
    TimeProvider timeProvider) : ICoordinatorStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
        string entityType,
        CancellationToken cancellationToken)
    {
        var routes = await dbContext.Routes
            .AsNoTracking()
            .Include(x => x.FieldPolicies)
            .Where(x => x.Enabled && x.SourceSystem == sourceSystem && x.EntityType == entityType)
            .ToListAsync(cancellationToken);

        return routes.Select(x => new SyncRouteDefinition(
            x.Id,
            x.Name,
            x.SourceSystem,
            x.EntityType,
            x.DestinationMode,
            x.DestinationSystem,
            x.ConflictScope,
            x.DefaultConflictPolicy,
            x.Enabled,
            x.FieldPolicies.ToDictionary(y => y.FieldName, y => y.Policy, StringComparer.Ordinal))).ToArray();
    }

    public async Task<bool> TryBeginInboxAsync(
        Guid sourceMessageId,
        Guid routeId,
        string destinationSystem,
        CancellationToken cancellationToken)
    {
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
            if (item.State is InboxState.Completed or InboxState.Held ||
                item.State == InboxState.Processing && item.LockedUntilUtc > now)
            {
                return false;
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
            return true;
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }

    public Task CompleteInboxAsync(
        Guid sourceMessageId,
        Guid routeId,
        string destinationSystem,
        InboxState state,
        CancellationToken cancellationToken) =>
        UpdateInboxAsync(sourceMessageId, routeId, destinationSystem, state, null, cancellationToken);

    public Task FailInboxAsync(
        Guid sourceMessageId,
        Guid routeId,
        string destinationSystem,
        string errorDetails,
        CancellationToken cancellationToken) =>
        UpdateInboxAsync(sourceMessageId, routeId, destinationSystem, InboxState.Failed, errorDetails, cancellationToken);

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
            : new SyncSnapshot(routeId, destinationSystem, entityType, entityId, DeserializePayload(entity.PayloadJson));
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
                PayloadJson = SerializePayload(snapshot.Payload),
                UpdatedAtUtc = timeProvider.GetUtcNow()
            };
            dbContext.SyncSnapshots.Add(entity);
        }
        else
        {
            entity.PayloadJson = SerializePayload(snapshot.Payload);
            entity.UpdatedAtUtc = timeProvider.GetUtcNow();
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveConflictAsync(ConflictHistory conflict, CancellationToken cancellationToken)
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
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task UpdateInboxAsync(
        Guid sourceMessageId,
        Guid routeId,
        string destinationSystem,
        InboxState state,
        string? error,
        CancellationToken cancellationToken)
    {
        var item = await dbContext.InboxMessages.FindAsync(
            [sourceMessageId, routeId, destinationSystem],
            cancellationToken) ?? throw new InvalidOperationException("Inbox message was not claimed.");
        item.State = state;
        item.LastError = error is null ? null : error[..Math.Min(error.Length, 4000)];
        item.UpdatedAtUtc = timeProvider.GetUtcNow();
        item.LockedUntilUtc = null;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string SerializePayload(EntityPayload payload) =>
        JsonSerializer.Serialize(payload.Fields, JsonOptions);

    private static EntityPayload DeserializePayload(string json) =>
        new(JsonSerializer.Deserialize<Dictionary<string, JsonNode?>>(json, JsonOptions) ?? []);
}
