using Microsoft.Extensions.Logging;
using SyncCoordinator.Contracts;

namespace SyncCoordinator.Core;

public sealed class SynchronizationCoordinator(
    IConnectorCatalog connectors,
    ICoordinatorStore store,
    ConflictResolver conflictResolver,
    TimeProvider timeProvider,
    ILogger<SynchronizationCoordinator> logger)
{
    public async Task<int> RunOnceAsync(int batchSize, CancellationToken cancellationToken)
    {
        var processed = 0;
        foreach (var source in connectors.All)
        {
            processed += await ProcessSourceAsync(source, batchSize, cancellationToken);
        }
        return processed;
    }

    private async Task<int> ProcessSourceAsync(
        ISyncConnector source,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var checkpoint = await store.GetCheckpointAsync(source.SystemCode, cancellationToken);
        var changes = await source.ReadChangesAsync(checkpoint, batchSize, cancellationToken);
        var processed = 0;

        foreach (var change in changes.OrderBy(x => x.QueueId))
        {
            try
            {
                if (await source.WasAppliedMessageAsync(change.MessageId, cancellationToken))
                {
                    CoordinatorLog.AppliedMessageSkipped(logger, change.MessageId, source.SystemCode);
                    await store.AdvanceCheckpointAsync(source.SystemCode, change.QueueId, cancellationToken);
                    processed++;
                    continue;
                }

                var message = await source.ReadMessageAsync(change, cancellationToken);
                var routes = await store.GetRoutesAsync(
                    source.SystemCode,
                    message.EntityType,
                    cancellationToken);

                foreach (var route in routes)
                {
                    await ProcessRouteAsync(message, route, cancellationToken);
                }

                await store.AdvanceCheckpointAsync(source.SystemCode, change.QueueId, cancellationToken);
                processed++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                CoordinatorLog.QueueItemFailed(logger, exception, change.QueueId, source.SystemCode);
                break;
            }
        }

        return processed;
    }

    private async Task ProcessRouteAsync(
        SyncMessage message,
        SyncRouteDefinition route,
        CancellationToken cancellationToken)
    {
        var destinationSystem = route.DestinationMode switch
        {
            DestinationMode.FixedSystem when !string.IsNullOrWhiteSpace(route.DestinationSystem) =>
                route.DestinationSystem,
            DestinationMode.OriginSystem when !string.IsNullOrWhiteSpace(message.OriginSystem) =>
                message.OriginSystem,
            _ => throw new InvalidOperationException($"Route '{route.Name}' の宛先を解決できません。")
        };

        if (string.Equals(message.SourceSystem, destinationSystem, StringComparison.OrdinalIgnoreCase))
        {
            CoordinatorLog.SelfRouteSkipped(logger, route.Name);
            return;
        }

        var shouldProcess = await store.TryBeginInboxAsync(
            message.SourceMessageId,
            route.Id,
            destinationSystem,
            cancellationToken);
        if (!shouldProcess)
        {
            return;
        }

        try
        {
            var destination = connectors.GetRequired(destinationSystem);
            var current = await destination.ReadCurrentAsync(
                message.EntityType,
                message.EntityId,
                cancellationToken);
            var snapshot = await store.GetSnapshotAsync(
                route.Id,
                destinationSystem,
                message.EntityType,
                message.EntityId,
                cancellationToken);
            var resolution = conflictResolver.Resolve(
                message.EntityType,
                snapshot?.Payload,
                message.Payload,
                current,
                route);
            var deliveryMessageId = DeliveryMessageId.Create(
                message.SourceMessageId,
                route.Id,
                destinationSystem);

            if (resolution.Conflicts.Count > 0)
            {
                await store.SaveConflictAsync(new ConflictHistory(
                    Guid.NewGuid(),
                    route.Id,
                    message.SourceMessageId,
                    deliveryMessageId,
                    message.SourceSystem,
                    destinationSystem,
                    message.EntityType,
                    message.EntityId,
                    route.ConflictScope,
                    resolution.Conflicts,
                    timeProvider.GetUtcNow()), cancellationToken);
            }

            if (resolution.ShouldApply)
            {
                await destination.ApplyAsync(new ApplyRequest(
                    deliveryMessageId,
                    message.SourceMessageId,
                    message.SourceSystem,
                    message.OriginSystem,
                    message.EntityType,
                    message.EntityId,
                    message.Operation,
                    resolution.AdoptedPayload), cancellationToken);
            }

            await store.SaveSnapshotAsync(new SyncSnapshot(
                route.Id,
                destinationSystem,
                message.EntityType,
                message.EntityId,
                resolution.AdoptedPayload), cancellationToken);
            await store.CompleteInboxAsync(
                message.SourceMessageId,
                route.Id,
                destinationSystem,
                resolution.IsHeld ? InboxState.Held : InboxState.Completed,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await store.FailInboxAsync(
                message.SourceMessageId,
                route.Id,
                destinationSystem,
                exception.ToString(),
                cancellationToken);
            throw;
        }
    }
}

internal static partial class CoordinatorLog
{
    [LoggerMessage(LogLevel.Debug, "Applied message {messageId} from {systemCode} was skipped to prevent a sync loop")]
    public static partial void AppliedMessageSkipped(ILogger logger, Guid messageId, string systemCode);

    [LoggerMessage(LogLevel.Error, "Change queue item {queueId} from {systemCode} failed; checkpoint was not advanced")]
    public static partial void QueueItemFailed(ILogger logger, Exception exception, long queueId, string systemCode);

    [LoggerMessage(LogLevel.Debug, "Route {routeName} resolved to its source system and was skipped")]
    public static partial void SelfRouteSkipped(ILogger logger, string routeName);
}
