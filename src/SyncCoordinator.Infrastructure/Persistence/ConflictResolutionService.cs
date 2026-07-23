using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SyncCoordinator.Contracts;
using SyncCoordinator.Core;

namespace SyncCoordinator.Infrastructure.Persistence;

public sealed class ConflictResolutionService(
    CoordinatorDbContext dbContext,
    ICoordinatorStore store,
    IConnectorCatalog connectors,
    ConflictResolver conflictResolver,
    TimeProvider timeProvider) : IConflictResolutionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ConflictDetails?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var conflict = await LoadConflictAsync(id, tracking: false, cancellationToken);
        if (conflict is null)
        {
            return null;
        }

        var activeChain = await LoadActiveChainAsync(conflict.Entity, tracking: false, cancellationToken);
        var position = activeChain.FindIndex(x => x.Id == conflict.Entity.Id);
        var oldestActiveConflictId = activeChain.FirstOrDefault()?.Id;
        var latestActiveConflictId = activeChain.LastOrDefault()?.Id;
        var canResolve = position >= 0 &&
                         (position == 0 || position == activeChain.Count - 1) &&
                         conflict.Entity.ResolutionState is ConflictResolutionState.AwaitingDecision or ConflictResolutionState.Failed;

        IReadOnlyList<FieldConflict> fields = DeserializeFields(conflict.Entity.FieldsJson);
        string? currentToken = null;
        string? readError = conflict.Entity.ResolutionLastError;
        if (conflict.Entity.ResolutionState is ConflictResolutionState.AwaitingDecision or ConflictResolutionState.Failed)
        {
            try
            {
                var route = await LoadRouteAsync(conflict.Entity, conflict.RouteSourceSystem, cancellationToken);
                var current = await ReadCurrentAsync(conflict.Entity, route, cancellationToken);
                currentToken = CurrentVersionToken(current);
                fields = fields.Select(field => EnrichField(field, conflict.Entity, route, current)).ToArray();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                readError = exception.Message;
            }
        }

        return new ConflictDetails(
            conflict.Entity.Id,
            conflict.RouteName,
            conflict.Entity.SourceMessageId,
            conflict.Entity.DeliveryMessageId,
            conflict.Entity.SourceSystem,
            conflict.SourceSystemName,
            conflict.Entity.DestinationSystem,
            conflict.DestinationSystemName,
            conflict.Entity.EntityType,
            conflict.Entity.EntityId,
            conflict.Entity.Operation,
            conflict.Entity.Scope,
            fields,
            conflict.Entity.DetectedAtUtc,
            conflict.Entity.ResolutionState,
            currentToken,
            conflict.Entity.ResolutionComment,
            readError,
            conflict.Entity.RequestedBy,
            conflict.Entity.RequestedAtUtc,
            conflict.Entity.ResolvedBy,
            conflict.Entity.ResolvedAtUtc,
            conflict.Entity.SupersededByConflictId,
            conflict.Entity.SupersededAtUtc,
            conflict.Entity.PreviousConflictId,
            oldestActiveConflictId,
            latestActiveConflictId,
            position < 0 ? 0 : position,
            position < 0 ? 0 : activeChain.Count - position - 1,
            canResolve);
    }

    public async Task QueueAsync(
        Guid id,
        ConflictResolutionInput input,
        string requestedBy,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var loaded = await LoadConflictAsync(id, tracking: true, cancellationToken) ??
                     throw new KeyNotFoundException("指定されたコンフリクトは存在しません。");
        var entity = loaded.Entity;
        if (entity.ResolutionState is not (ConflictResolutionState.AwaitingDecision or ConflictResolutionState.Failed))
        {
            throw new InvalidOperationException("このコンフリクトは現在、解決内容を登録できる状態ではありません。");
        }
        var activeChain = await LoadActiveChainAsync(entity, tracking: true, cancellationToken);
        var position = activeChain.FindIndex(x => x.Id == entity.Id);
        if (position < 0 || position != 0 && position != activeChain.Count - 1)
        {
            throw new InvalidOperationException("このコンフリクトは前のコンフリクトが解決されるまで操作できません。");
        }
        if (activeChain.Any(x => x.Id != entity.Id &&
                                 x.ResolutionState is ConflictResolutionState.Pending or ConflictResolutionState.Processing))
        {
            throw new InvalidOperationException("同じレコードのコンフリクトを解決処理中です。完了後に再読み込みしてください。");
        }
        input.Comment = string.IsNullOrWhiteSpace(input.Comment) ? null : input.Comment.Trim();
        if (input.Comment?.Length > 1000)
        {
            throw new InvalidOperationException("解決理由は1000文字以内で入力してください。");
        }

        var route = await LoadRouteAsync(entity, loaded.RouteSourceSystem, cancellationToken);
        var current = await ReadCurrentAsync(entity, route, cancellationToken);
        var currentToken = CurrentVersionToken(current);
        if (!TokensEqual(input.ExpectedCurrentVersionToken, currentToken))
        {
            throw new InvalidOperationException("同期先の値が表示後に変更されました。再読み込みして解決内容を確認してください。");
        }

        var conflictFields = DeserializeFields(entity.FieldsJson);
        var stored = new StoredResolutionRequest { ExpectedCurrentVersionToken = currentToken };
        if (entity.Operation == ChangeOperation.Delete)
        {
            if (input.DeleteChoice is not (ManualConflictChoice.Incoming or ManualConflictChoice.Current))
            {
                throw new InvalidOperationException("削除を適用するか、同期先レコードを維持するか選択してください。");
            }
            stored.ApplyDelete = input.DeleteChoice == ManualConflictChoice.Incoming;
        }
        else
        {
            var required = conflictFields.Where(RequiresManualDecision).Select(x => x.FieldName).ToHashSet(StringComparer.Ordinal);
            var inputs = input.Fields.GroupBy(x => x.FieldName, StringComparer.Ordinal).ToArray();
            if (inputs.Any(x => x.Count() != 1) || !required.SetEquals(inputs.Select(x => x.Key)))
            {
                throw new InvalidOperationException("解決が必要なすべての項目について採用する値を選択してください。");
            }

            foreach (var fieldInput in inputs.Select(x => x.Single()))
            {
                var field = conflictFields.Single(x => x.FieldName == fieldInput.FieldName);
                JsonNode? value = fieldInput.Choice switch
                {
                    ManualConflictChoice.Incoming => field.IncomingValue?.DeepClone(),
                    ManualConflictChoice.Current => ReadValue(current, field.FieldName)?.DeepClone(),
                    ManualConflictChoice.Custom => fieldInput.CustomValue?.DeepClone(),
                    _ => throw new InvalidOperationException("採用する値の選択が正しくありません。")
                };
                stored.Fields.Add(new StoredFieldResolution
                {
                    FieldName = field.FieldName,
                    Choice = fieldInput.Choice,
                    Value = value
                });
            }
        }

        entity.ResolutionRequestJson = JsonSerializer.Serialize(stored, JsonOptions);
        entity.ResolutionComment = input.Comment;
        entity.RequestedBy = requestedBy;
        entity.RequestedAtUtc = timeProvider.GetUtcNow();
        entity.ResolutionState = ConflictResolutionState.Pending;
        entity.ResolutionLastError = null;
        entity.ResolutionLockedUntilUtc = null;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException("別の操作によってコンフリクトの状態が更新されました。再読み込みしてください。");
        }
    }

    public async Task<int> ProcessPendingAsync(int take, CancellationToken cancellationToken)
    {
        var queryNow = timeProvider.GetUtcNow();
        var candidates = await dbContext.SyncConflicts
            .Where(x => x.ResolutionState == ConflictResolutionState.Pending ||
                        x.ResolutionState == ConflictResolutionState.Processing && x.ResolutionLockedUntilUtc <= queryNow)
            .OrderBy(x => x.RequestedAtUtc)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken);
        var processed = 0;
        foreach (var entity in candidates)
        {
            var lockStartedAt = timeProvider.GetUtcNow();
            entity.ResolutionState = ConflictResolutionState.Processing;
            entity.ResolutionLockedUntilUtc = lockStartedAt.AddMinutes(5);
            entity.ResolutionAttemptCount++;
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                dbContext.Entry(entity).State = EntityState.Detached;
                continue;
            }

            await ProcessOneAsync(entity, cancellationToken);
            processed++;
        }
        return processed;
    }

    private async Task ProcessOneAsync(SyncConflictEntity entity, CancellationToken cancellationToken)
    {
        try
        {
            var routeSource = await dbContext.Routes.AsNoTracking()
                .Where(x => x.Id == entity.RouteId)
                .Select(x => x.SourceSystem.Code)
                .SingleAsync(cancellationToken);
            var route = await LoadRouteAsync(entity, routeSource, cancellationToken);
            if (route.OperationallyPaused || !route.Enabled)
            {
                throw new InvalidOperationException("同期ルールまたは関連システムが停止中です。再開後にもう一度実行してください。");
            }
            var request = JsonSerializer.Deserialize<StoredResolutionRequest>(entity.ResolutionRequestJson!, JsonOptions) ??
                          throw new InvalidOperationException("解決要求を読み込めません。");
            var current = await ReadCurrentAsync(entity, route, cancellationToken);
            await using (var transaction = await dbContext.Database.BeginTransactionAsync(
                             IsolationLevel.Serializable,
                             cancellationToken))
            {
                await dbContext.Entry(entity).ReloadAsync(cancellationToken);
                var activeChain = await LoadActiveChainAsync(entity, tracking: true, cancellationToken);
                var position = activeChain.FindIndex(x => x.Id == entity.Id);
                if (entity.ResolutionState == ConflictResolutionState.Superseded ||
                    position < 0 || position != 0 && position != activeChain.Count - 1)
                {
                    return;
                }
                if (!string.Equals(CurrentVersionToken(current), request.ExpectedCurrentVersionToken, StringComparison.Ordinal))
                {
                    entity.ResolutionState = ConflictResolutionState.AwaitingDecision;
                    entity.ResolutionRequestJson = null;
                    entity.ResolutionLastError = "同期先の値が解決処理待ちの間に変更されました。現在値を確認して選択し直してください。";
                    entity.ResolutionLockedUntilUtc = null;
                    await dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return;
                }

                await ApplyResolutionAsync(entity, route, request, current, cancellationToken);
                if (position == activeChain.Count - 1 && position > 0)
                {
                    await SupersedeOlderChainAsync(entity, activeChain.Take(position), cancellationToken);
                }
                else if (position == 0 && activeChain.Count > 1)
                {
                    await AdvanceChainAsync(entity, route, cancellationToken);
                }
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            foreach (var entry in dbContext.ChangeTracker.Entries()
                         .Where(x => x.Entity != entity && x.State is not EntityState.Unchanged and not EntityState.Detached)
                         .ToArray())
            {
                if (entry.State == EntityState.Added)
                {
                    entry.State = EntityState.Detached;
                }
                else
                {
                    await entry.ReloadAsync(CancellationToken.None);
                }
            }
            await dbContext.Entry(entity).ReloadAsync(CancellationToken.None);
            if (entity.ResolutionState != ConflictResolutionState.Processing)
            {
                return;
            }
            entity.ResolutionState = ConflictResolutionState.Failed;
            entity.ResolutionLastError = exception.ToString()[..Math.Min(exception.ToString().Length, 4000)];
            entity.ResolutionLockedUntilUtc = null;
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task ApplyResolutionAsync(
        SyncConflictEntity entity,
        SyncRouteDefinition route,
        StoredResolutionRequest request,
        EntityPayload? current,
        CancellationToken cancellationToken)
    {
        var incoming = DeserializePayload(entity.IncomingPayloadJson)!;
        EntityPayload? adopted;
        var shouldApply = false;
        if (entity.Operation == ChangeOperation.Delete)
        {
            shouldApply = request.ApplyDelete && current is not null;
            adopted = request.ApplyDelete ? null : current;
        }
        else
        {
            var baseline = entity.HadBaseline
                ? new SyncSnapshot(
                    entity.RouteId,
                    entity.DestinationSystem,
                    entity.EntityType,
                    entity.EntityId,
                    DeserializePayload(entity.BaselineSourcePayloadJson),
                    DeserializePayload(entity.BaselineDestinationPayloadJson))
                : null;
            var keepPolicies = DeserializeFields(entity.FieldsJson)
                .ToDictionary(x => x.FieldName, _ => ConflictPolicy.KeepCurrentAndNotify, StringComparer.Ordinal);
            var safeRoute = route with
            {
                DefaultConflictPolicy = ConflictPolicy.KeepCurrentAndNotify,
                FieldPolicies = keepPolicies
            };
            var automatic = conflictResolver.Resolve(entity.EntityType, baseline, incoming, current, safeRoute);
            var values = automatic.AdoptedPayload.Fields.ToDictionary(
                x => x.Key, x => x.Value?.DeepClone(), StringComparer.Ordinal);
            foreach (var field in request.Fields)
            {
                values[field.FieldName] = field.Value?.DeepClone();
            }
            adopted = new EntityPayload(values);
            shouldApply = true;
        }

        var resolutionDeliveryId = WebhookEventId.Create("conflict.resolution.apply", entity.Id);
        if (shouldApply)
        {
            var connector = await connectors.GetRequiredAsync(entity.DestinationSystem, cancellationToken);
            var payload = entity.Operation == ChangeOperation.Delete
                ? EntityPayload.Empty
                : SyncPayloadTransformer.TransformFromCanonical(adopted!, route, entity.DestinationSystem);
            await connector.ApplyAsync(new ApplyRequest(
                resolutionDeliveryId,
                entity.SourceMessageId,
                entity.SourceSystem,
                route.SourceSystem,
                entity.EntityType,
                entity.EntityId,
                entity.Operation,
                entity.Operation == ChangeOperation.Delete
                    ? route.ResolveDeletionBehavior(entity.DestinationSystem)
                    : null,
                payload)
            {
                RouteId = entity.RouteId
            }, cancellationToken);
        }

        UpsertSnapshot(entity.RouteId, entity.DestinationSystem, entity.EntityType, entity.EntityId,
            entity.Operation == ChangeOperation.Delete ? null : incoming, adopted);
        if (route.Direction == SyncDirection.Bidirectional)
        {
            UpsertSnapshot(entity.RouteId, entity.SourceSystem, entity.EntityType, entity.EntityId,
                adopted, entity.Operation == ChangeOperation.Delete ? null : incoming);
        }
        var inbox = await dbContext.InboxMessages.SingleAsync(
            x => x.SourceMessageId == entity.SourceMessageId &&
                 x.RouteId == entity.RouteId &&
                 x.DestinationSystem == entity.DestinationSystem,
            cancellationToken);
        inbox.State = InboxState.Completed;
        inbox.UpdatedAtUtc = timeProvider.GetUtcNow();
        inbox.LastError = null;
        inbox.LockedUntilUtc = null;

        var choices = request.Fields.ToDictionary(x => x.FieldName, StringComparer.Ordinal);
        var fields = DeserializeFields(entity.FieldsJson).Select(field =>
        {
            if (entity.Operation == ChangeOperation.Delete)
            {
                return field with
                {
                    AdoptedValue = request.ApplyDelete ? null : ReadValue(current, field.FieldName)?.DeepClone(),
                    Resolution = request.ApplyDelete ? "ManuallyAppliedDelete" : "ManuallyKeptDeleteTarget"
                };
            }
            if (!choices.TryGetValue(field.FieldName, out var choice))
            {
                return field;
            }
            return field with
            {
                AdoptedValue = choice.Value?.DeepClone(),
                Resolution = choice.Choice switch
                {
                    ManualConflictChoice.Incoming => "ManuallyAppliedIncoming",
                    ManualConflictChoice.Current => "ManuallyKeptCurrent",
                    ManualConflictChoice.Custom => "ManuallyEntered",
                    _ => field.Resolution
                }
            };
        }).ToArray();
        entity.FieldsJson = JsonSerializer.Serialize(fields, JsonOptions);
        entity.ResolutionState = ConflictResolutionState.Resolved;
        entity.ResolvedAtUtc = timeProvider.GetUtcNow();
        entity.ResolvedBy = entity.RequestedBy;
        entity.ResolutionLastError = null;
        entity.ResolutionLockedUntilUtc = null;
    }

    private async Task SupersedeOlderChainAsync(
        SyncConflictEntity resolvedLatest,
        IEnumerable<SyncConflictEntity> olderConflicts,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var older in olderConflicts.Where(IsActive))
        {
            older.ResolutionState = ConflictResolutionState.Superseded;
            older.SupersededByConflictId = resolvedLatest.Id;
            older.SupersededAtUtc = now;
            older.ResolutionRequestJson = null;
            older.ResolutionLockedUntilUtc = null;
            await SetInboxStateAsync(older, InboxState.Superseded, now, cancellationToken);
        }
    }

    private async Task AdvanceChainAsync(
        SyncConflictEntity resolvedOldest,
        SyncRouteDefinition route,
        CancellationToken cancellationToken)
    {
        var cursor = resolvedOldest;
        while (true)
        {
            var remaining = await LoadActiveChainAsync(cursor, tracking: true, cancellationToken);
            var next = remaining.FirstOrDefault();
            if (next is null)
            {
                return;
            }

            var baseline = await LoadSnapshotAsync(next, cancellationToken);
            var incoming = DeserializePayload(next.IncomingPayloadJson)!;
            var current = await ReadCurrentAsync(next, route, cancellationToken);
            var resolution = next.Operation == ChangeOperation.Delete
                ? ConflictResolver.ResolveDelete(baseline, incoming, current, route)
                : conflictResolver.Resolve(next.EntityType, baseline, incoming, current, route);
            var fields = resolution.Conflicts.Select(field => field with
            {
                IncomingFieldName = PhysicalFieldName(route, field.FieldName, next.SourceSystem),
                CurrentFieldName = PhysicalFieldName(route, field.FieldName, next.DestinationSystem)
            }).ToArray();

            next.HadBaseline = baseline is not null;
            next.BaselineSourcePayloadJson = SerializePayload(baseline?.SourcePayload);
            next.BaselineDestinationPayloadJson = SerializePayload(baseline?.DestinationPayload);
            next.CurrentPayloadJson = SerializePayload(current);
            next.FieldsJson = JsonSerializer.Serialize(fields, JsonOptions);
            next.ResolutionRequestJson = null;
            next.ResolutionComment = null;
            next.RequestedBy = null;
            next.RequestedAtUtc = null;
            next.ResolutionLastError = null;
            next.ResolutionLockedUntilUtc = null;

            if (resolution.ShouldApply)
            {
                var connector = await connectors.GetRequiredAsync(next.DestinationSystem, cancellationToken);
                var payload = next.Operation == ChangeOperation.Delete
                    ? EntityPayload.Empty
                    : SyncPayloadTransformer.TransformFromCanonical(
                        resolution.AdoptedPayload,
                        route,
                        next.DestinationSystem);
                await connector.ApplyAsync(new ApplyRequest(
                    next.DeliveryMessageId,
                    next.SourceMessageId,
                    next.SourceSystem,
                    route.SourceSystem,
                    next.EntityType,
                    next.EntityId,
                    next.Operation,
                    next.Operation == ChangeOperation.Delete
                        ? route.ResolveDeletionBehavior(next.DestinationSystem)
                        : null,
                    payload)
                {
                    RouteId = next.RouteId
                }, cancellationToken);
            }

            UpsertSnapshot(next.RouteId, next.DestinationSystem, next.EntityType, next.EntityId,
                next.Operation == ChangeOperation.Delete ? null : incoming,
                resolution.AdoptedExists ? resolution.AdoptedPayload : null);
            if (route.Direction == SyncDirection.Bidirectional)
            {
                UpsertSnapshot(next.RouteId, next.SourceSystem, next.EntityType, next.EntityId,
                    resolution.AdoptedExists ? resolution.AdoptedPayload : null,
                    next.Operation == ChangeOperation.Delete ? null : incoming);
            }

            var now = timeProvider.GetUtcNow();
            if (resolution.IsHeld)
            {
                // 項目単位では、手動判断が必要な項目と自動採用できる項目が混在する。
                // 自動採用分とスナップショットを反映したうえで、残りだけを要対応にする。
                next.ResolutionState = ConflictResolutionState.AwaitingDecision;
                await SetInboxStateAsync(next, InboxState.Held, now, cancellationToken);
                return;
            }

            await SetInboxStateAsync(next, InboxState.Completed, now, cancellationToken);
            next.ResolutionState = ConflictResolutionState.Resolved;
            next.ResolvedAtUtc = now;
            next.ResolvedBy = "automatic-chain-rebase";
            cursor = next;
        }
    }

    private async Task<SyncSnapshot?> LoadSnapshotAsync(
        SyncConflictEntity entity,
        CancellationToken cancellationToken)
    {
        var snapshot = dbContext.SyncSnapshots.Local.SingleOrDefault(x =>
                           x.RouteId == entity.RouteId && x.DestinationSystem == entity.DestinationSystem &&
                           x.EntityType == entity.EntityType && x.EntityId == entity.EntityId) ??
                       await dbContext.SyncSnapshots.SingleOrDefaultAsync(x =>
                           x.RouteId == entity.RouteId && x.DestinationSystem == entity.DestinationSystem &&
                           x.EntityType == entity.EntityType && x.EntityId == entity.EntityId,
                           cancellationToken);
        return snapshot is null
            ? null
            : new SyncSnapshot(
                snapshot.RouteId,
                snapshot.DestinationSystem,
                snapshot.EntityType,
                snapshot.EntityId,
                DeserializePayload(snapshot.SourcePayloadJson),
                DeserializePayload(snapshot.DestinationPayloadJson));
    }

    private async Task SetInboxStateAsync(
        SyncConflictEntity conflict,
        InboxState state,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var inbox = await dbContext.InboxMessages.SingleOrDefaultAsync(
            x => x.SourceMessageId == conflict.SourceMessageId &&
                 x.RouteId == conflict.RouteId &&
                 x.DestinationSystem == conflict.DestinationSystem,
            cancellationToken);
        if (inbox is null)
        {
            return;
        }
        inbox.State = state;
        inbox.UpdatedAtUtc = now;
        inbox.LastError = null;
        inbox.LockedUntilUtc = null;
    }

    private async Task<List<SyncConflictEntity>> LoadActiveChainAsync(
        SyncConflictEntity entity,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var query = dbContext.SyncConflicts
            .Where(x => x.RouteId == entity.RouteId &&
                        x.DestinationSystem == entity.DestinationSystem &&
                        x.EntityType == entity.EntityType &&
                        x.EntityId == entity.EntityId &&
                        (x.ResolutionState == ConflictResolutionState.AwaitingDecision ||
                         x.ResolutionState == ConflictResolutionState.Pending ||
                         x.ResolutionState == ConflictResolutionState.Processing ||
                         x.ResolutionState == ConflictResolutionState.Failed ||
                         x.ResolutionState == ConflictResolutionState.WaitingForPrevious));
        if (!tracking)
        {
            query = query.AsNoTracking();
        }
        var conflicts = await query
            .OrderBy(x => x.DetectedAtUtc)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        return conflicts.Where(IsActive).ToList();
    }

    private static bool IsActive(SyncConflictEntity conflict) => conflict.ResolutionState is
        ConflictResolutionState.AwaitingDecision or
        ConflictResolutionState.Pending or
        ConflictResolutionState.Processing or
        ConflictResolutionState.Failed or
        ConflictResolutionState.WaitingForPrevious;

    private async Task<LoadedConflict?> LoadConflictAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var query = dbContext.SyncConflicts
            .Include(x => x.Route).ThenInclude(x => x.SourceSystem)
            .AsQueryable();
        if (!tracking)
        {
            query = query.AsNoTracking();
        }
        var entity = await query.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
        {
            return null;
        }
        var names = await dbContext.Systems.AsNoTracking()
            .Where(x => x.Code == entity.SourceSystem || x.Code == entity.DestinationSystem)
            .ToDictionaryAsync(x => x.Code, x => x.DisplayName, StringComparer.OrdinalIgnoreCase, cancellationToken);
        return new LoadedConflict(
            entity,
            entity.Route.Name,
            entity.Route.SourceSystem.Code,
            names.GetValueOrDefault(entity.SourceSystem, entity.SourceSystem),
            names.GetValueOrDefault(entity.DestinationSystem, entity.DestinationSystem));
    }

    private async Task<SyncRouteDefinition> LoadRouteAsync(
        SyncConflictEntity entity,
        string routeSourceSystem,
        CancellationToken cancellationToken)
    {
        var routes = await store.GetRoutesAsync(
            entity.SourceSystem,
            routeSourceSystem,
            entity.EntityType,
            cancellationToken);
        return routes.SingleOrDefault(x => x.Id == entity.RouteId) ??
               throw new InvalidOperationException("同期ルールが無効か、解決に必要なマッピングがありません。");
    }

    private async Task<EntityPayload?> ReadCurrentAsync(
        SyncConflictEntity entity,
        SyncRouteDefinition route,
        CancellationToken cancellationToken)
    {
        var connector = await connectors.GetRequiredAsync(entity.DestinationSystem, cancellationToken);
        var physical = await connector.ReadCurrentForRouteAsync(
            entity.RouteId,
            entity.EntityType,
            entity.EntityId,
            cancellationToken);
        return physical is null
            ? null
            : SyncPayloadTransformer.NormalizeToCanonical(physical, route, entity.DestinationSystem);
    }

    private void UpsertSnapshot(
        Guid routeId,
        string destinationSystem,
        string entityType,
        string entityId,
        EntityPayload? source,
        EntityPayload? destination)
    {
        var snapshot = dbContext.SyncSnapshots.Local.SingleOrDefault(x =>
                           x.RouteId == routeId && x.DestinationSystem == destinationSystem &&
                           x.EntityType == entityType && x.EntityId == entityId) ??
                       dbContext.SyncSnapshots.SingleOrDefault(x =>
                           x.RouteId == routeId && x.DestinationSystem == destinationSystem &&
                           x.EntityType == entityType && x.EntityId == entityId);
        if (snapshot is null)
        {
            snapshot = new SyncSnapshotEntity
            {
                RouteId = routeId,
                DestinationSystem = destinationSystem,
                EntityType = entityType,
                EntityId = entityId
            };
            dbContext.SyncSnapshots.Add(snapshot);
        }
        snapshot.SourcePayloadJson = SerializePayload(source);
        snapshot.DestinationPayloadJson = SerializePayload(destination);
        snapshot.UpdatedAtUtc = timeProvider.GetUtcNow();
    }

    private static FieldConflict EnrichField(
        FieldConflict field,
        SyncConflictEntity entity,
        SyncRouteDefinition route,
        EntityPayload? current)
    {
        var latest = ReadValue(current, field.FieldName);
        var mapping = route.ValueMappings.GetValueOrDefault(field.FieldName);
        var incomingName = field.IncomingFieldName ??
            (string.Equals(entity.SourceSystem, route.DestinationSystem, StringComparison.OrdinalIgnoreCase)
                ? mapping?.DestinationColumn
                : field.FieldName);
        var currentName = field.CurrentFieldName ??
            (string.Equals(entity.DestinationSystem, route.DestinationSystem, StringComparison.OrdinalIgnoreCase)
                ? mapping?.DestinationColumn
                : field.FieldName);
        return field with
        {
            IncomingFieldName = incomingName ?? field.FieldName,
            CurrentFieldName = currentName ?? field.FieldName,
            LatestCurrentValue = latest?.DeepClone(),
            CurrentChanged = !JsonNode.DeepEquals(field.CurrentValue, latest)
        };
    }

    private static string PhysicalFieldName(
        SyncRouteDefinition route,
        string canonicalFieldName,
        string physicalSystem) =>
        route.ValueMappings.TryGetValue(canonicalFieldName, out var mapping) &&
        string.Equals(physicalSystem, route.DestinationSystem, StringComparison.OrdinalIgnoreCase)
            ? mapping.DestinationColumn
            : canonicalFieldName;

    private static bool RequiresManualDecision(FieldConflict field) => field.Resolution is
        "Held" or "MergeUnavailableHeld" or "RecordHeld" or "DeleteHeld" or "DeleteMergeUnavailableHeld";

    private static JsonNode? ReadValue(EntityPayload? payload, string fieldName) =>
        payload?.Fields.TryGetValue(fieldName, out var value) == true ? value : null;

    private static string CurrentVersionToken(EntityPayload? current)
    {
        var token = new CurrentToken(
            current is not null,
            current?.Fields.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new CurrentTokenField(x.Key, x.Value)).ToArray() ?? []);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(token, JsonOptions)));
    }

    private static bool TokensEqual(string supplied, string expected)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(supplied) &&
                   CryptographicOperations.FixedTimeEquals(
                       Convert.FromHexString(supplied),
                       Convert.FromHexString(expected));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static List<FieldConflict> DeserializeFields(string json) =>
        JsonSerializer.Deserialize<List<FieldConflict>>(json, JsonOptions) ?? [];

    private static EntityPayload? DeserializePayload(string? json) =>
        json is null ? null : new EntityPayload(
            JsonSerializer.Deserialize<Dictionary<string, JsonNode?>>(json, JsonOptions) ?? []);

    private static string? SerializePayload(EntityPayload? payload) =>
        payload is null ? null : JsonSerializer.Serialize(payload.Fields, JsonOptions);

    private sealed record LoadedConflict(
        SyncConflictEntity Entity,
        string RouteName,
        string RouteSourceSystem,
        string SourceSystemName,
        string DestinationSystemName);

    private sealed class StoredResolutionRequest
    {
        public string ExpectedCurrentVersionToken { get; set; } = string.Empty;
        public bool ApplyDelete { get; set; }
        public List<StoredFieldResolution> Fields { get; set; } = [];
    }

    private sealed class StoredFieldResolution
    {
        public string FieldName { get; set; } = string.Empty;
        public ManualConflictChoice Choice { get; set; }
        public JsonNode? Value { get; set; }
    }

    private sealed record CurrentToken(bool Exists, IReadOnlyList<CurrentTokenField> Fields);
    private sealed record CurrentTokenField(string Name, JsonNode? Value);
}
