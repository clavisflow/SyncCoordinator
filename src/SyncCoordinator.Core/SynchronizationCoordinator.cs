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
        if (await store.IsSystemPausedAsync(source.SystemCode, cancellationToken))
        {
            CoordinatorLog.PausedSystemSkipped(logger, source.SystemCode);
            return 0;
        }

        var checkpoint = await store.GetCheckpointAsync(source.SystemCode, cancellationToken);
        var changes = await source.ReadChangesAsync(checkpoint, batchSize, cancellationToken);
        if (changes.Count == 0)
        {
            return 0;
        }

        // Queueは状態変更命令ではなく変更通知として扱う。同じレコードの通知はまとめ、
        // Connectorから現在の最新状態を解決して、過去のpayloadで巻き戻さない。
        var latestNotifications = changes
            .GroupBy(x => new EntityKey(x.EntityType, x.EntityId))
            .Select(x => x.MaxBy(y => y.QueueId)!)
            .OrderBy(x => x.QueueId)
            .ToArray();

        foreach (var change in latestNotifications)
        {
            try
            {
                if (await source.WasAppliedMessageAsync(change.MessageId, cancellationToken))
                {
                    CoordinatorLog.AppliedMessageSkipped(logger, change.MessageId, source.SystemCode);
                    continue;
                }
                var message = await source.ReadLatestMessageAsync(change, cancellationToken);
                if (message is null)
                {
                    continue;
                }
                if (message.SourceMessageId != change.MessageId &&
                    await source.WasAppliedMessageAsync(message.SourceMessageId, cancellationToken))
                {
                    CoordinatorLog.AppliedMessageSkipped(logger, message.SourceMessageId, source.SystemCode);
                    continue;
                }
                var routes = await store.GetRoutesAsync(
                    source.SystemCode,
                    message.OriginSystem,
                    message.EntityType,
                    cancellationToken);
                var pausedRoute = routes.FirstOrDefault(x => x.OperationallyPaused);
                if (pausedRoute is not null)
                {
                    CoordinatorLog.PausedRouteDeferred(logger, pausedRoute.Name, source.SystemCode);
                    // 共有Checkpointを進めると、停止中ルールの通知を再開時に拾えなくなる。
                    // 完了済みの他ルールはInboxで冪等に読み飛ばせるため、batch全体を再試行する。
                    return 0;
                }

                foreach (var route in routes)
                {
                    await ProcessRouteAsync(message, route, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                CoordinatorLog.QueueItemFailed(logger, exception, change.QueueId, source.SystemCode);
                // バッチ内の一部だけをCheckpoint済みにしない。再試行時はInboxと
                // DeliveryMessageIdによって完了済み配送を安全に読み飛ばす。
                return 0;
            }
        }

        await store.AdvanceCheckpointAsync(
            source.SystemCode,
            changes.Max(x => x.QueueId),
            cancellationToken);
        return changes.Count;
    }

    private async Task ProcessRouteAsync(
        SyncMessage message,
        SyncRouteDefinition route,
        CancellationToken cancellationToken)
    {
        var destinationSystem = route.ResolveDestination(message.SourceSystem, message.OriginSystem) ??
                                throw new InvalidOperationException($"同期ルール '{route.Name}' の送信方向を解決できません。");

        if (string.Equals(message.SourceSystem, destinationSystem, StringComparison.OrdinalIgnoreCase))
        {
            CoordinatorLog.SelfRouteSkipped(logger, route.Name);
            return;
        }
        var deletionBehavior = message.Operation == ChangeOperation.Delete
            ? route.ResolveDeletionBehavior(destinationSystem)
            : null;
        if (message.Operation == ChangeOperation.Delete && deletionBehavior is null)
        {
            CoordinatorLog.DeleteSkipped(logger, route.Name);
            return;
        }

        var inboxResult = await store.TryBeginInboxAsync(
            message.SourceMessageId,
            route.Id,
            destinationSystem,
            cancellationToken);
        if (inboxResult == InboxAcquireResult.AlreadyCompleted)
        {
            return;
        }
        if (inboxResult == InboxAcquireResult.Busy)
        {
            throw new InvalidOperationException(
                $"同期ルール '{route.Name}' の配送は別のWorkerが処理中です。Checkpointを進めず再試行します。");
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
            var resolution = message.Operation == ChangeOperation.Delete
                ? ConflictResolver.ResolveDelete(snapshot, message.Payload, current, route)
                : conflictResolver.Resolve(
                        message.EntityType,
                        snapshot,
                        message.Payload,
                        current,
                        route);
            var deliveryMessageId = DeliveryMessageId.Create(
                message.SourceMessageId,
                route.Id,
                destinationSystem);

            if (resolution.Conflicts.Count > 0)
            {
                var conflictId = WebhookEventId.Create("conflict.history", deliveryMessageId);
                await store.SaveConflictAsync(new ConflictHistory(
                    conflictId,
                    route.Id,
                    message.SourceMessageId,
                    deliveryMessageId,
                    message.SourceSystem,
                    destinationSystem,
                    message.EntityType,
                    message.EntityId,
                    route.ConflictScope,
                    resolution.Conflicts,
                    timeProvider.GetUtcNow()), new WebhookEventNotification(
                        WebhookEventId.Create(WebhookEventTypes.ConflictDetected, deliveryMessageId),
                        WebhookEventTypes.ConflictDetected,
                        timeProvider.GetUtcNow(),
                        route.Id, route.Name, message.SourceSystem, destinationSystem,
                        message.EntityType, message.EntityId, message.SourceMessageId, deliveryMessageId),
                    cancellationToken);
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
                    deletionBehavior,
                    resolution.AdoptedPayload), cancellationToken);
            }

            await store.SaveSnapshotAsync(new SyncSnapshot(
                route.Id,
                destinationSystem,
                message.EntityType,
                message.EntityId,
                message.Operation == ChangeOperation.Delete ? null : message.Payload,
                resolution.AdoptedExists ? resolution.AdoptedPayload : null), cancellationToken);
            if (route.Direction == SyncDirection.Bidirectional)
            {
                await store.SaveSnapshotAsync(new SyncSnapshot(
                    route.Id,
                    message.SourceSystem,
                    message.EntityType,
                    message.EntityId,
                    resolution.AdoptedExists ? resolution.AdoptedPayload : null,
                    message.Operation == ChangeOperation.Delete ? null : message.Payload), cancellationToken);
            }
            await store.CompleteInboxAsync(
                message.SourceMessageId,
                route.Id,
                destinationSystem,
                resolution.IsHeld ? InboxState.Held : InboxState.Completed,
                resolution.ShouldApply
                    ? new WebhookEventNotification(
                        WebhookEventId.Create(
                            message.Operation == ChangeOperation.Delete ? WebhookEventTypes.SyncDeleted : WebhookEventTypes.SyncUpserted,
                            deliveryMessageId),
                        message.Operation == ChangeOperation.Delete ? WebhookEventTypes.SyncDeleted : WebhookEventTypes.SyncUpserted,
                        timeProvider.GetUtcNow(),
                        route.Id, route.Name, message.SourceSystem, destinationSystem,
                        message.EntityType, message.EntityId, message.SourceMessageId, deliveryMessageId)
                    : null,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await store.FailInboxAsync(
                message.SourceMessageId,
                route.Id,
                destinationSystem,
                exception.ToString(),
                new WebhookEventNotification(
                    WebhookEventId.Create(WebhookEventTypes.SyncFailed, message.SourceMessageId, route.Id, destinationSystem),
                    WebhookEventTypes.SyncFailed,
                    timeProvider.GetUtcNow(),
                    route.Id, route.Name, message.SourceSystem, destinationSystem,
                    message.EntityType, message.EntityId, message.SourceMessageId,
                    DeliveryMessageId.Create(message.SourceMessageId, route.Id, destinationSystem)),
                cancellationToken);
            throw;
        }
    }

    private readonly record struct EntityKey(string EntityType, string EntityId);
}

internal static partial class CoordinatorLog
{
    [LoggerMessage(LogLevel.Debug, "Applied message {messageId} from {systemCode} was skipped to prevent a sync loop")]
    public static partial void AppliedMessageSkipped(ILogger logger, Guid messageId, string systemCode);

    [LoggerMessage(LogLevel.Debug, "System {systemCode} is paused; its queue and checkpoint were left untouched")]
    public static partial void PausedSystemSkipped(ILogger logger, string systemCode);

    [LoggerMessage(LogLevel.Debug, "Route {routeName} is paused by a system while reading {systemCode}; checkpoint was not advanced")]
    public static partial void PausedRouteDeferred(ILogger logger, string routeName, string systemCode);

    [LoggerMessage(LogLevel.Error, "Change queue item {queueId} from {systemCode} failed; checkpoint was not advanced")]
    public static partial void QueueItemFailed(ILogger logger, Exception exception, long queueId, string systemCode);

    [LoggerMessage(LogLevel.Debug, "Route {routeName} resolved to its source system and was skipped")]
    public static partial void SelfRouteSkipped(ILogger logger, string routeName);

    [LoggerMessage(LogLevel.Debug, "Delete change for route {routeName} was skipped because delete synchronization is disabled")]
    public static partial void DeleteSkipped(ILogger logger, string routeName);
}
