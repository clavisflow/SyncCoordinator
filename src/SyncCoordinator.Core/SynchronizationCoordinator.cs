using Microsoft.Extensions.Logging;
using SyncCoordinator.Contracts;

namespace SyncCoordinator.Core;

public sealed class SynchronizationCoordinator(
    IConnectorCatalog connectors,
    ICoordinatorStore store,
    ConflictResolver conflictResolver,
    TimeProvider timeProvider,
    ILogger<SynchronizationCoordinator> logger,
    IOperationalEventRecorder? operationalEvents = null)
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
                    if (!await ProcessRouteAsync(message, route, cancellationToken))
                    {
                        return 0;
                    }
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

    private async Task<bool> ProcessRouteAsync(
        SyncMessage message,
        SyncRouteDefinition route,
        CancellationToken cancellationToken)
    {
        var destinationSystem = route.ResolveDestination(message.SourceSystem, message.OriginSystem) ??
                                throw new InvalidOperationException($"同期ルール '{route.Name}' の送信方向を解決できません。");

        if (string.Equals(message.SourceSystem, destinationSystem, StringComparison.OrdinalIgnoreCase))
        {
            CoordinatorLog.SelfRouteSkipped(logger, route.Name);
            return true;
        }
        var deletionBehavior = message.Operation == ChangeOperation.Delete
            ? route.ResolveDeletionBehavior(destinationSystem)
            : null;
        if (message.Operation == ChangeOperation.Delete && deletionBehavior is null)
        {
            CoordinatorLog.DeleteSkipped(logger, route.Name);
            return true;
        }

        var inboxResult = await store.TryBeginInboxAsync(
            message.SourceMessageId,
            route.Id,
            destinationSystem,
            cancellationToken);
        if (inboxResult == InboxAcquireResult.AlreadyCompleted)
        {
            return true;
        }
        if (inboxResult == InboxAcquireResult.RoutePaused)
        {
            CoordinatorLog.PausedRouteDeferred(logger, route.Name, message.SourceSystem);
            return false;
        }
        if (inboxResult == InboxAcquireResult.Busy)
        {
            throw new InvalidOperationException(
                $"同期ルール '{route.Name}' の配送は別のWorkerが処理中です。Checkpointを進めず再試行します。");
        }

        try
        {
            var destination = connectors.GetRequired(destinationSystem);
            var incoming = NormalizeToCanonical(message.Payload, route, message.SourceSystem);
            var destinationPayload = await destination.ReadCurrentAsync(
                message.EntityType,
                message.EntityId,
                cancellationToken);
            var current = destinationPayload is null
                ? null
                : NormalizeToCanonical(destinationPayload, route, destinationSystem);
            var snapshot = await store.GetSnapshotAsync(
                route.Id,
                destinationSystem,
                message.EntityType,
                message.EntityId,
                cancellationToken);
            var resolution = message.Operation == ChangeOperation.Delete
                ? ConflictResolver.ResolveDelete(snapshot, incoming, current, route)
                : conflictResolver.Resolve(
                        message.EntityType,
                        snapshot,
                        incoming,
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
                var payloadToWrite = message.Operation == ChangeOperation.Delete
                    ? resolution.AdoptedPayload
                    : TransformFromCanonical(resolution.AdoptedPayload, route, destinationSystem);
                await destination.ApplyAsync(new ApplyRequest(
                    deliveryMessageId,
                    message.SourceMessageId,
                    message.SourceSystem,
                    message.OriginSystem,
                    message.EntityType,
                    message.EntityId,
                    message.Operation,
                    deletionBehavior,
                    payloadToWrite), cancellationToken);
            }

            await store.SaveSnapshotAsync(new SyncSnapshot(
                route.Id,
                destinationSystem,
                message.EntityType,
                message.EntityId,
                message.Operation == ChangeOperation.Delete ? null : incoming,
                resolution.AdoptedExists ? resolution.AdoptedPayload : null), cancellationToken);
            if (route.Direction == SyncDirection.Bidirectional)
            {
                await store.SaveSnapshotAsync(new SyncSnapshot(
                    route.Id,
                    message.SourceSystem,
                    message.EntityType,
                    message.EntityId,
                    resolution.AdoptedExists ? resolution.AdoptedPayload : null,
                    message.Operation == ChangeOperation.Delete ? null : incoming), cancellationToken);
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
            return true;
        }
        catch (ValueTransformationException exception)
        {
            var webhook = FailureWebhook(message, route, destinationSystem);
            await store.HoldInboxAsync(
                message.SourceMessageId,
                route.Id,
                destinationSystem,
                exception.ToString(),
                webhook,
                cancellationToken);
            CoordinatorLog.ValueValidationHeld(
                logger,
                route.Name,
                message.EntityType,
                message.EntityId,
                exception.FieldName,
                exception.TargetColumn,
                exception.ReasonCode);
            if (operationalEvents is not null)
            {
                await operationalEvents.RecordAsync(new OperationalEventInput(
                    OperationalEventSeverity.Warning,
                    OperationalEventCategories.Synchronization,
                    OperationalEventCodes.SynchronizationValueValidationHeld,
                    "worker",
                    $"{route.Name}: {message.EntityType}/{message.EntityId}",
                    exception.Message,
                    message.SourceMessageId.ToString("D")), CancellationToken.None);
            }
            // データ修正または変換設定の変更が必要な非一時エラー。キュー全体を詰まらせない。
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await store.FailInboxAsync(
                message.SourceMessageId,
                route.Id,
                destinationSystem,
                exception.ToString(),
                FailureWebhook(message, route, destinationSystem),
                cancellationToken);
            throw;
        }
    }

    private static EntityPayload NormalizeToCanonical(
        EntityPayload payload,
        SyncRouteDefinition route,
        string physicalSystem) =>
        TransformPayload(
            payload,
            route,
            mapping => string.Equals(physicalSystem, route.DestinationSystem, StringComparison.OrdinalIgnoreCase)
                ? (mapping.ReverseTransform, mapping.SourceContract, mapping.FieldName)
                : (new ValueTransformInput(), mapping.SourceContract, mapping.FieldName));

    private static EntityPayload TransformFromCanonical(
        EntityPayload payload,
        SyncRouteDefinition route,
        string physicalSystem) =>
        TransformPayload(
            payload,
            route,
            mapping => string.Equals(physicalSystem, route.DestinationSystem, StringComparison.OrdinalIgnoreCase)
                ? (mapping.ForwardTransform, mapping.DestinationContract, mapping.DestinationColumn)
                : (new ValueTransformInput(), mapping.SourceContract, mapping.FieldName));

    private static EntityPayload TransformPayload(
        EntityPayload payload,
        SyncRouteDefinition route,
        Func<ColumnValueMappingDefinition, (ValueTransformInput Transform, ColumnValueContract Contract, string TargetColumn)> select)
    {
        var fields = payload.Fields.ToDictionary(
            pair => pair.Key,
            pair => pair.Value?.DeepClone(),
            StringComparer.Ordinal);
        foreach (var mapping in route.ValueMappings.Values)
        {
            if (!payload.Fields.TryGetValue(mapping.FieldName, out var value))
            {
                continue;
            }

            var target = select(mapping);
            fields[mapping.FieldName] = ValueTransformEngine.Transform(
                value,
                target.Transform,
                target.Contract,
                mapping.FieldName,
                target.TargetColumn);
        }
        return new EntityPayload(fields);
    }

    private WebhookEventNotification FailureWebhook(
        SyncMessage message,
        SyncRouteDefinition route,
        string destinationSystem) =>
        new(
            WebhookEventId.Create(WebhookEventTypes.SyncFailed, message.SourceMessageId, route.Id, destinationSystem),
            WebhookEventTypes.SyncFailed,
            timeProvider.GetUtcNow(),
            route.Id, route.Name, message.SourceSystem, destinationSystem,
            message.EntityType, message.EntityId, message.SourceMessageId,
            DeliveryMessageId.Create(message.SourceMessageId, route.Id, destinationSystem));

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

    [LoggerMessage(LogLevel.Warning, "Route {routeName} held {entityType}/{entityId}: value validation failed for {fieldName} -> {targetColumn} ({reasonCode})")]
    public static partial void ValueValidationHeld(
        ILogger logger,
        string routeName,
        string entityType,
        string entityId,
        string fieldName,
        string targetColumn,
        string reasonCode);
}
