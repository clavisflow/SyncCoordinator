using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyncCoordinator.Contracts;
using SyncCoordinator.Core;

namespace SyncCoordinator.Infrastructure.Persistence;

public sealed class DemoConflictSeeder(
    CoordinatorDbContext dbContext,
    IOptions<CoordinatorDatabaseOptions> options,
    ICoordinatorStore store,
    IConnectorCatalog connectors,
    ConflictResolver conflictResolver,
    TimeProvider timeProvider) : IDemoConflictSeeder
{
    internal const string EntityType = "SupportCase";
    internal const string TemplateEntityId = "CASE-1001";
    internal const string EntityId = "CASE-UPDATE-1001";
    internal const string DeleteEntityId = "CASE-DELETE-1001";
    internal const string UpdateThenDeleteEntityId = "CASE-UPDATE-THEN-DELETE-1001";
    internal const string DeleteThenUpdateEntityId = "CASE-DELETE-THEN-UPDATE-1001";
    internal const string ResolvedEntityId = "CASE-RESOLVED-1001";
    internal const string DateConflictEntityId = "CASE-DATE-CONFLICT-1001";
    internal const string DateTimeConflictEntityId = "WO-DATETIME-CONFLICT-1001";
    internal const string TextConflictEntityId = "WO-CONFLICT-TEXT";
    internal const string IntegerConflictEntityId = "WO-CONFLICT-INT";
    internal const string DecimalConflictEntityId = "WO-CONFLICT-DECIMAL";
    internal const string BooleanConflictEntityId = "WO-CONFLICT-BOOL";
    internal const string NullConflictEntityId = "WO-CONFLICT-NULL";
    internal const string StatusConflictEntityId = "WO-CONFLICT-STATUS";
    internal const string GuidConflictEntityId = "WO-CONFLICT-GUID";
    internal const string WorkOrderEntityType = "WorkOrder";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] WorkOrderTouchEntityIds =
    [
        "WO-FANOUT-1001",
        "WO-FANOUT-1002",
        "WO-MULTI-STAFF-1001",
        "WO-UNASSIGN-1001",
        "WO-ERROR-STRING",
        "WO-ERROR-INT",
        "WO-ERROR-DECIMAL-PRECISION",
        "WO-ERROR-DECIMAL-SCALE",
        "WO-ERROR-STATUS"
    ];

    public async Task<int> SeedIfReadyAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.SeedDemoData)
        {
            return 0;
        }

        var seeded = await SeedWorkOrderScenariosIfReadyAsync(cancellationToken);
        var routeInfo = await dbContext.Routes.AsNoTracking()
            .Where(x => x.EntityType == EntityType &&
                        x.SourceSystem.Code == "PORTAL" &&
                        x.DestinationSystem.Code == "CRM" &&
                        x.Enabled && x.DeploymentState == DatabaseDeploymentState.Prepared &&
                        x.MappingMaintenanceStartedAtUtc == null &&
                        x.SourceSystem.PausedAtUtc == null && x.DestinationSystem.PausedAtUtc == null)
            .Select(x => new { x.Id })
            .SingleOrDefaultAsync(cancellationToken);
        if (routeInfo is null)
        {
            return seeded;
        }

        var conflictId = UpdateConflictId(routeInfo.Id, EntityId);
        var deleteConflictId = DeleteConflictId(routeInfo.Id, DeleteEntityId);
        var updateThenDeleteUpdateId = UpdateConflictId(routeInfo.Id, UpdateThenDeleteEntityId);
        var updateThenDeleteDeleteId = DeleteConflictId(routeInfo.Id, UpdateThenDeleteEntityId);
        var deleteThenUpdateDeleteId = DeleteConflictId(routeInfo.Id, DeleteThenUpdateEntityId);
        var deleteThenUpdateUpdateId = UpdateConflictId(routeInfo.Id, DeleteThenUpdateEntityId);
        var resolvedConflictId = ResolvedConflictId(routeInfo.Id, ResolvedEntityId);
        var dateConflictId = UpdateConflictId(routeInfo.Id, DateConflictEntityId);
        Guid[] expectedConflictIds =
        [
            conflictId,
            deleteConflictId,
            updateThenDeleteUpdateId,
            updateThenDeleteDeleteId,
            deleteThenUpdateDeleteId,
            deleteThenUpdateUpdateId,
            resolvedConflictId,
            dateConflictId
        ];
        var existingConflictIds = await dbContext.SyncConflicts.AsNoTracking()
            .Where(x => expectedConflictIds.Contains(x.Id))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var seedUpdateConflict = !existingConflictIds.Contains(conflictId);
        var seedDeleteConflict = !existingConflictIds.Contains(deleteConflictId);
        var seedUpdateThenDelete = !existingConflictIds.Contains(updateThenDeleteUpdateId) ||
                                   !existingConflictIds.Contains(updateThenDeleteDeleteId);
        var seedDeleteThenUpdate = !existingConflictIds.Contains(deleteThenUpdateDeleteId) ||
                                   !existingConflictIds.Contains(deleteThenUpdateUpdateId);
        var seedResolvedConflict = !existingConflictIds.Contains(resolvedConflictId);
        var seedDateConflict = !existingConflictIds.Contains(dateConflictId);

        var legacyUpdateConflictId = WebhookEventId.Create(
            "demo.conflict.history", routeInfo.Id, TemplateEntityId);
        var legacyDeleteConflictId = WebhookEventId.Create(
            "demo.delete-conflict.history", routeInfo.Id, DeleteEntityId);
        var conflictIdsToReplace = new HashSet<Guid> { legacyUpdateConflictId, legacyDeleteConflictId };
        foreach (var entityId in new[]
                 {
                     EntityId, DeleteEntityId, UpdateThenDeleteEntityId, DeleteThenUpdateEntityId, ResolvedEntityId,
                     DateConflictEntityId
                 })
        {
            conflictIdsToReplace.Add(WebhookEventId.Create("demo.conflict.v2.update.history", routeInfo.Id, entityId));
            conflictIdsToReplace.Add(WebhookEventId.Create("demo.conflict.v2.delete.history", routeInfo.Id, entityId));
        }
        if (seedUpdateConflict) conflictIdsToReplace.Add(conflictId);
        if (seedDeleteConflict) conflictIdsToReplace.Add(deleteConflictId);
        if (seedUpdateThenDelete)
        {
            conflictIdsToReplace.Add(updateThenDeleteUpdateId);
            conflictIdsToReplace.Add(updateThenDeleteDeleteId);
        }
        if (seedDeleteThenUpdate)
        {
            conflictIdsToReplace.Add(deleteThenUpdateDeleteId);
            conflictIdsToReplace.Add(deleteThenUpdateUpdateId);
        }
        if (seedResolvedConflict) conflictIdsToReplace.Add(resolvedConflictId);
        if (seedDateConflict) conflictIdsToReplace.Add(dateConflictId);

        var conflictsToReplace = await dbContext.SyncConflicts
            .Where(x => conflictIdsToReplace.Contains(x.Id))
            .ToListAsync(cancellationToken);
        var inboxSourceMessageIds = conflictsToReplace.Select(x => x.SourceMessageId).Distinct().ToArray();
        if (inboxSourceMessageIds.Length > 0)
        {
            var inboxToReplace = await dbContext.InboxMessages
                .Where(x => inboxSourceMessageIds.Contains(x.SourceMessageId) && x.RouteId == routeInfo.Id)
                .ToListAsync(cancellationToken);
            dbContext.InboxMessages.RemoveRange(inboxToReplace);
        }
        dbContext.SyncConflicts.RemoveRange(conflictsToReplace);

        string[] scenarioEntityIds =
        [
            EntityId,
            DeleteEntityId,
            UpdateThenDeleteEntityId,
            DeleteThenUpdateEntityId,
            ResolvedEntityId,
            DateConflictEntityId
        ];
        var snapshotsToReplace = await dbContext.SyncSnapshots
            .Where(x => x.RouteId == routeInfo.Id && scenarioEntityIds.Contains(x.EntityId) &&
                        ((x.EntityId == EntityId && seedUpdateConflict) ||
                         (x.EntityId == DeleteEntityId && seedDeleteConflict) ||
                         (x.EntityId == UpdateThenDeleteEntityId && seedUpdateThenDelete) ||
                         (x.EntityId == DeleteThenUpdateEntityId && seedDeleteThenUpdate) ||
                         (x.EntityId == ResolvedEntityId && seedResolvedConflict) ||
                         (x.EntityId == DateConflictEntityId && seedDateConflict)))
            .ToListAsync(cancellationToken);
        dbContext.SyncSnapshots.RemoveRange(snapshotsToReplace);
        if (conflictsToReplace.Count > 0 || snapshotsToReplace.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (!seedUpdateConflict && !seedDeleteConflict && !seedUpdateThenDelete && !seedDeleteThenUpdate &&
            !seedResolvedConflict && !seedDateConflict)
        {
            return seeded;
        }

        var route = (await store.GetRoutesAsync("PORTAL", "PORTAL", EntityType, cancellationToken))
            .SingleOrDefault(x => x.Id == routeInfo.Id);
        if (route is null || route.OperationallyPaused)
        {
            return seeded;
        }

        var source = await connectors.GetRequiredAsync(route.SourceSystem, cancellationToken);
        var destination = await connectors.GetRequiredAsync(route.DestinationSystem, cancellationToken);
        var sourceCurrent = await source.ReadCurrentAsync(EntityType, TemplateEntityId, cancellationToken);
        if (sourceCurrent is null)
        {
            // Customer Portalの初期データがまだ準備されていない場合だけ次周期へ送る。
            return seeded;
        }

        var now = timeProvider.GetUtcNow();
        if (seedResolvedConflict)
        {
            await SeedResolvedConflictAsync(
                route, source, destination, sourceCurrent, ResolvedEntityId, resolvedConflictId,
                now.AddMinutes(-15), now.AddMinutes(-12), cancellationToken);
            seeded++;
        }
        if (seedDateConflict)
        {
            await SeedUpdateConflictAsync(
                route, source, destination, EntityType,
                CreateDateConflictScenario(sourceCurrent, DateConflictEntityId),
                DateConflictEntityId, dateConflictId, now.AddMinutes(-10), null, cancellationToken);
            seeded++;
        }
        if (seedUpdateConflict)
        {
            await SeedUpdateConflictAsync(
                route, source, destination, EntityType, CreateScenario(sourceCurrent, EntityId), EntityId, conflictId,
                now.AddMinutes(-5), null, cancellationToken);
            seeded++;
        }
        if (seedDeleteConflict)
        {
            await SeedDeleteConflictAsync(
                route, source, destination, sourceCurrent, DeleteEntityId, deleteConflictId,
                now.AddMinutes(-4), null, cancellationToken);
            seeded++;
        }
        if (seedUpdateThenDelete)
        {
            await SeedUpdateConflictAsync(
                route, source, destination, EntityType, CreateScenario(sourceCurrent, UpdateThenDeleteEntityId),
                UpdateThenDeleteEntityId, updateThenDeleteUpdateId,
                now.AddMinutes(-3), null, cancellationToken);
            await SeedDeleteConflictAsync(
                route, source, destination, sourceCurrent, UpdateThenDeleteEntityId, updateThenDeleteDeleteId,
                now.AddMinutes(-2), updateThenDeleteUpdateId, cancellationToken);
            seeded += 2;
        }
        if (seedDeleteThenUpdate)
        {
            await SeedDeleteConflictAsync(
                route, source, destination, sourceCurrent, DeleteThenUpdateEntityId, deleteThenUpdateDeleteId,
                now.AddMinutes(-1), null, cancellationToken);
            await SeedUpdateConflictAsync(
                route, source, destination, EntityType, CreateScenario(sourceCurrent, DeleteThenUpdateEntityId),
                DeleteThenUpdateEntityId, deleteThenUpdateUpdateId,
                now, deleteThenUpdateDeleteId, cancellationToken);
            seeded += 2;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return seeded;
    }

    private async Task<int> SeedWorkOrderScenariosIfReadyAsync(CancellationToken cancellationToken)
    {
        var routeInfo = await dbContext.Routes.AsNoTracking()
            .Where(x => x.EntityType == WorkOrderEntityType &&
                        x.SourceSystem.Code == "CRM" && x.DestinationSystem.Code == "FIELD" &&
                        x.Enabled && x.DeploymentState == DatabaseDeploymentState.Prepared &&
                        x.MappingMaintenanceStartedAtUtc == null &&
                        x.SourceSystem.PausedAtUtc == null && x.DestinationSystem.PausedAtUtc == null)
            .Select(x => new { x.Id })
            .SingleOrDefaultAsync(cancellationToken);
        if (routeInfo is null)
        {
            return 0;
        }

        var route = (await store.GetRoutesAsync("CRM", "CRM", WorkOrderEntityType, cancellationToken))
            .SingleOrDefault(x => x.Id == routeInfo.Id);
        if (route is null || route.OperationallyPaused)
        {
            return 0;
        }

        var source = await connectors.GetRequiredAsync(route.SourceSystem, cancellationToken);
        var destination = await connectors.GetRequiredAsync(route.DestinationSystem, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var definitions = new (string EntityId, DemoConflictScenario Scenario)[]
        {
            (TextConflictEntityId, CreateWorkOrderConflictScenario(TextConflictEntityId, "ProblemSummary",
                JsonValue.Create("文字列競合の基準値"), JsonValue.Create("CRMが変更した作業内容"), JsonValue.Create("FIELDが変更した作業内容"))),
            (IntegerConflictEntityId, CreateWorkOrderConflictScenario(IntegerConflictEntityId, "EstimatedMinutes",
                JsonValue.Create(60m), JsonValue.Create(90m), JsonValue.Create(120m))),
            (DecimalConflictEntityId, CreateWorkOrderConflictScenario(DecimalConflictEntityId, "EstimatedCost",
                JsonValue.Create(10000.00m), JsonValue.Create(12500.25m), JsonValue.Create(14000.75m))),
            (BooleanConflictEntityId, CreateWorkOrderConflictScenario(BooleanConflictEntityId, "RequiresParts",
                null, JsonValue.Create(true), JsonValue.Create(false))),
            (DateTimeConflictEntityId, CreateDateTimeConflictScenario(DateTimeConflictEntityId)),
            (NullConflictEntityId, CreateWorkOrderConflictScenario(NullConflictEntityId, "WorkNote",
                JsonValue.Create("基準メモ"), null, JsonValue.Create("FIELDで追記したメモ"))),
            (StatusConflictEntityId, CreateWorkOrderConflictScenario(StatusConflictEntityId, "Status",
                JsonValue.Create("Scheduled"), JsonValue.Create("InProgress"), JsonValue.Create("Completed"))),
            (GuidConflictEntityId, CreateWorkOrderConflictScenario(GuidConflictEntityId, "ExternalTrackingId",
                JsonValue.Create("22222222-2222-2222-2222-222222222208"),
                JsonValue.Create("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
                JsonValue.Create("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")))
        };

        var seeded = 0;
        for (var index = 0; index < definitions.Length; index++)
        {
            var definition = definitions[index];
            var conflictId = UpdateConflictId(routeInfo.Id, definition.EntityId);
            if (await dbContext.SyncConflicts.AsNoTracking().AnyAsync(x => x.Id == conflictId, cancellationToken))
            {
                continue;
            }

            var snapshotsToReplace = await dbContext.SyncSnapshots
                .Where(x => x.RouteId == routeInfo.Id && x.EntityType == WorkOrderEntityType &&
                            x.EntityId == definition.EntityId)
                .ToListAsync(cancellationToken);
            dbContext.SyncSnapshots.RemoveRange(snapshotsToReplace);
            await SeedUpdateConflictAsync(
                route, source, destination, WorkOrderEntityType, definition.Scenario,
                definition.EntityId, conflictId, now.AddMinutes(-20 + index), null, cancellationToken);
            seeded++;
        }

        if (seeded > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        foreach (var entityId in WorkOrderTouchEntityIds)
        {
            var current = await source.ReadCurrentAsync(WorkOrderEntityType, entityId, cancellationToken);
            if (current is null)
            {
                continue;
            }

            var messageId = WebhookEventId.Create("demo.work-order.initial-touch.v1", route.Id, entityId);
            await source.ApplyAsync(new ApplyRequest(
                messageId,
                messageId,
                route.SourceSystem,
                route.SourceSystem,
                WorkOrderEntityType,
                entityId,
                ChangeOperation.Upsert,
                null,
                SyncPayloadTransformer.TransformFromCanonical(current, route, route.SourceSystem)), cancellationToken);
        }

        return seeded;
    }

    private async Task SeedUpdateConflictAsync(
        SyncRouteDefinition route,
        ISyncConnector source,
        ISyncConnector destination,
        string entityType,
        DemoConflictScenario scenario,
        string entityId,
        Guid conflictId,
        DateTimeOffset detectedAt,
        Guid? previousConflictId,
        CancellationToken cancellationToken)
    {
        var sourceSeedMessageId = WebhookEventId.Create("demo.conflict.v3.update.source-change", route.Id, entityId);
        var destinationSeedMessageId = WebhookEventId.Create("demo.conflict.v3.update.destination-change", route.Id, entityId);
        var seedChanges = new List<(Guid SourceMessageId, string DestinationSystem)>
        {
            (sourceSeedMessageId, route.DestinationSystem)
        };
        if (route.Direction == SyncDirection.Bidirectional)
        {
            seedChanges.Add((destinationSeedMessageId, route.SourceSystem));
        }
        await SuppressSeedChangesAsync(route.Id, detectedAt, seedChanges, cancellationToken);
        await source.ApplyAsync(new ApplyRequest(
            sourceSeedMessageId,
            sourceSeedMessageId,
            route.SourceSystem,
            route.SourceSystem,
            entityType,
            entityId,
            ChangeOperation.Upsert,
            null,
            SyncPayloadTransformer.TransformFromCanonical(scenario.Incoming, route, route.SourceSystem)), cancellationToken);
        await destination.ApplyAsync(new ApplyRequest(
            destinationSeedMessageId,
            destinationSeedMessageId,
            route.DestinationSystem,
            route.SourceSystem,
            entityType,
            entityId,
            ChangeOperation.Upsert,
            null,
            SyncPayloadTransformer.TransformFromCanonical(scenario.Current, route, route.DestinationSystem)), cancellationToken);

        var baseline = new SyncSnapshot(
            route.Id,
            route.DestinationSystem,
            entityType,
            entityId,
            scenario.Baseline,
            scenario.Baseline);
        var resolution = conflictResolver.Resolve(entityType, baseline, scenario.Incoming, scenario.Current, route);
        if (!resolution.IsHeld || resolution.Conflicts.Count == 0)
        {
            throw new InvalidOperationException("デモ競合シナリオから保留コンフリクトを生成できませんでした。");
        }

        var fields = resolution.Conflicts.Select(field => field with
        {
            IncomingFieldName = field.FieldName,
            CurrentFieldName = route.ValueMappings.GetValueOrDefault(field.FieldName)?.DestinationColumn ?? field.FieldName
        }).ToArray();
        var sourceMessageId = WebhookEventId.Create("demo.conflict.v3.update.source-message", route.Id, entityId);
        var deliveryMessageId = DeliveryMessageId.Create(sourceMessageId, route.Id, route.DestinationSystem);
        dbContext.SyncConflicts.Add(new SyncConflictEntity
        {
            Id = conflictId,
            RouteId = route.Id,
            SourceMessageId = sourceMessageId,
            DeliveryMessageId = deliveryMessageId,
            SourceSystem = route.SourceSystem,
            DestinationSystem = route.DestinationSystem,
            EntityType = entityType,
            EntityId = entityId,
            Operation = ChangeOperation.Upsert,
            Scope = route.ConflictScope,
            FieldsJson = JsonSerializer.Serialize(fields, JsonOptions),
            HadBaseline = true,
            BaselineSourcePayloadJson = SerializePayload(scenario.Baseline),
            BaselineDestinationPayloadJson = SerializePayload(scenario.Baseline),
            IncomingPayloadJson = SerializePayload(scenario.Incoming)!,
            CurrentPayloadJson = SerializePayload(scenario.Current),
            DetectedAtUtc = detectedAt,
            ResolutionState = ConflictResolutionState.AwaitingDecision,
            PreviousConflictId = previousConflictId
        });
        dbContext.InboxMessages.Add(new InboxMessageEntity
        {
            SourceMessageId = sourceMessageId,
            RouteId = route.Id,
            DestinationSystem = route.DestinationSystem,
            State = InboxState.Held,
            AttemptCount = 1,
            FirstSeenAtUtc = detectedAt,
            UpdatedAtUtc = detectedAt
        });
        await UpsertSnapshotAsync(
            route.Id, route.DestinationSystem, entityType, entityId,
            scenario.Incoming, resolution.AdoptedPayload, detectedAt, cancellationToken);
        if (route.Direction == SyncDirection.Bidirectional)
        {
            await UpsertSnapshotAsync(
                route.Id, route.SourceSystem, entityType, entityId,
                resolution.AdoptedPayload, scenario.Incoming, detectedAt, cancellationToken);
        }
    }

    private async Task SeedDeleteConflictAsync(
        SyncRouteDefinition route,
        ISyncConnector source,
        ISyncConnector destination,
        EntityPayload template,
        string entityId,
        Guid conflictId,
        DateTimeOffset detectedAt,
        Guid? previousConflictId,
        CancellationToken cancellationToken)
    {
        var scenario = CreateDeleteScenario(template, entityId);
        var sourceSeedMessageId = WebhookEventId.Create("demo.conflict.v3.delete.source-create", route.Id, entityId);
        var destinationSeedMessageId = WebhookEventId.Create("demo.conflict.v3.delete.destination-change", route.Id, entityId);
        var sourceDeleteMessageId = WebhookEventId.Create("demo.conflict.v3.delete.source-delete", route.Id, entityId);
        var seedChanges = new List<(Guid SourceMessageId, string DestinationSystem)>
        {
            (sourceSeedMessageId, route.DestinationSystem),
            (sourceDeleteMessageId, route.DestinationSystem)
        };
        if (route.Direction == SyncDirection.Bidirectional)
        {
            seedChanges.Add((destinationSeedMessageId, route.SourceSystem));
        }
        await SuppressSeedChangesAsync(route.Id, detectedAt, seedChanges, cancellationToken);
        await source.ApplyAsync(new ApplyRequest(
            sourceSeedMessageId,
            sourceSeedMessageId,
            route.SourceSystem,
            route.SourceSystem,
            EntityType,
            entityId,
            ChangeOperation.Upsert,
            null,
            scenario.Baseline), cancellationToken);
        await destination.ApplyAsync(new ApplyRequest(
            destinationSeedMessageId,
            destinationSeedMessageId,
            route.DestinationSystem,
            route.SourceSystem,
            EntityType,
            entityId,
            ChangeOperation.Upsert,
            null,
            SyncPayloadTransformer.TransformFromCanonical(scenario.Current, route, route.DestinationSystem)), cancellationToken);
        await source.ApplyAsync(new ApplyRequest(
            sourceDeleteMessageId,
            sourceDeleteMessageId,
            route.SourceSystem,
            route.SourceSystem,
            EntityType,
            entityId,
            ChangeOperation.Delete,
            route.ResolveDeletionBehavior(route.SourceSystem),
            EntityPayload.Empty), cancellationToken);

        var baseline = new SyncSnapshot(
            route.Id,
            route.DestinationSystem,
            EntityType,
            entityId,
            scenario.Baseline,
            scenario.Baseline);
        var resolution = ConflictResolver.ResolveDelete(baseline, scenario.Baseline, scenario.Current, route);
        if (!resolution.IsHeld || resolution.Conflicts.Count == 0)
        {
            throw new InvalidOperationException("デモ削除競合シナリオから保留コンフリクトを生成できませんでした。");
        }

        var fields = resolution.Conflicts.Select(field => field with
        {
            IncomingFieldName = field.FieldName,
            CurrentFieldName = route.ValueMappings.GetValueOrDefault(field.FieldName)?.DestinationColumn ?? field.FieldName
        }).ToArray();
        var sourceMessageId = WebhookEventId.Create("demo.conflict.v3.delete.source-message", route.Id, entityId);
        var deliveryMessageId = DeliveryMessageId.Create(sourceMessageId, route.Id, route.DestinationSystem);

        dbContext.SyncConflicts.Add(new SyncConflictEntity
        {
            Id = conflictId,
            RouteId = route.Id,
            SourceMessageId = sourceMessageId,
            DeliveryMessageId = deliveryMessageId,
            SourceSystem = route.SourceSystem,
            DestinationSystem = route.DestinationSystem,
            EntityType = EntityType,
            EntityId = entityId,
            Operation = ChangeOperation.Delete,
            Scope = route.ConflictScope,
            FieldsJson = JsonSerializer.Serialize(fields, JsonOptions),
            HadBaseline = true,
            BaselineSourcePayloadJson = SerializePayload(scenario.Baseline),
            BaselineDestinationPayloadJson = SerializePayload(scenario.Baseline),
            IncomingPayloadJson = SerializePayload(scenario.Baseline)!,
            CurrentPayloadJson = SerializePayload(scenario.Current),
            DetectedAtUtc = detectedAt,
            ResolutionState = ConflictResolutionState.AwaitingDecision,
            PreviousConflictId = previousConflictId
        });
        dbContext.InboxMessages.Add(new InboxMessageEntity
        {
            SourceMessageId = sourceMessageId,
            RouteId = route.Id,
            DestinationSystem = route.DestinationSystem,
            State = InboxState.Held,
            AttemptCount = 1,
            FirstSeenAtUtc = detectedAt,
            UpdatedAtUtc = detectedAt
        });
        await UpsertSnapshotAsync(
            route.Id, route.DestinationSystem, EntityType, entityId,
            null, resolution.AdoptedPayload, detectedAt, cancellationToken);
        if (route.Direction == SyncDirection.Bidirectional)
        {
            await UpsertSnapshotAsync(
                route.Id, route.SourceSystem, EntityType, entityId,
                resolution.AdoptedPayload, null, detectedAt, cancellationToken);
        }
    }

    private async Task SeedResolvedConflictAsync(
        SyncRouteDefinition route,
        ISyncConnector source,
        ISyncConnector destination,
        EntityPayload sourceCurrent,
        string entityId,
        Guid conflictId,
        DateTimeOffset detectedAt,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken)
    {
        var scenario = CreateScenario(sourceCurrent, entityId);
        var sourceSeedMessageId = WebhookEventId.Create("demo.conflict.v4.resolved.source-change", route.Id, entityId);
        var destinationSeedMessageId = WebhookEventId.Create("demo.conflict.v4.resolved.destination-change", route.Id, entityId);
        var seedChanges = new List<(Guid SourceMessageId, string DestinationSystem)>
        {
            (sourceSeedMessageId, route.DestinationSystem)
        };
        if (route.Direction == SyncDirection.Bidirectional)
        {
            seedChanges.Add((destinationSeedMessageId, route.SourceSystem));
        }
        await SuppressSeedChangesAsync(route.Id, detectedAt, seedChanges, cancellationToken);

        await source.ApplyAsync(new ApplyRequest(
            sourceSeedMessageId,
            sourceSeedMessageId,
            route.SourceSystem,
            route.SourceSystem,
            EntityType,
            entityId,
            ChangeOperation.Upsert,
            null,
            scenario.Incoming), cancellationToken);
        await destination.ApplyAsync(new ApplyRequest(
            destinationSeedMessageId,
            destinationSeedMessageId,
            route.DestinationSystem,
            route.SourceSystem,
            EntityType,
            entityId,
            ChangeOperation.Upsert,
            null,
            SyncPayloadTransformer.TransformFromCanonical(scenario.Incoming, route, route.DestinationSystem)), cancellationToken);

        var baseline = new SyncSnapshot(
            route.Id,
            route.DestinationSystem,
            EntityType,
            entityId,
            scenario.Baseline,
            scenario.Baseline);
        var resolution = conflictResolver.Resolve(EntityType, baseline, scenario.Incoming, scenario.Current, route);
        if (!resolution.IsHeld || resolution.Conflicts.Count == 0)
        {
            throw new InvalidOperationException("解決済みデモ競合の元になる保留コンフリクトを生成できませんでした。");
        }

        var fields = resolution.Conflicts.Select(field => field with
        {
            AdoptedValue = field.IncomingValue?.DeepClone(),
            Resolution = "ManuallyAppliedIncoming",
            IncomingFieldName = field.FieldName,
            CurrentFieldName = route.ValueMappings.GetValueOrDefault(field.FieldName)?.DestinationColumn ?? field.FieldName
        }).ToArray();
        var sourceMessageId = WebhookEventId.Create("demo.conflict.v4.resolved.source-message", route.Id, entityId);
        var deliveryMessageId = DeliveryMessageId.Create(sourceMessageId, route.Id, route.DestinationSystem);
        dbContext.SyncConflicts.Add(new SyncConflictEntity
        {
            Id = conflictId,
            RouteId = route.Id,
            SourceMessageId = sourceMessageId,
            DeliveryMessageId = deliveryMessageId,
            SourceSystem = route.SourceSystem,
            DestinationSystem = route.DestinationSystem,
            EntityType = EntityType,
            EntityId = entityId,
            Operation = ChangeOperation.Upsert,
            Scope = route.ConflictScope,
            FieldsJson = JsonSerializer.Serialize(fields, JsonOptions),
            HadBaseline = true,
            BaselineSourcePayloadJson = SerializePayload(scenario.Baseline),
            BaselineDestinationPayloadJson = SerializePayload(scenario.Baseline),
            IncomingPayloadJson = SerializePayload(scenario.Incoming)!,
            CurrentPayloadJson = SerializePayload(scenario.Current),
            DetectedAtUtc = detectedAt,
            ResolutionState = ConflictResolutionState.Resolved,
            ResolutionComment = "お客様の最新申告を確認し、受信値を採用しました。",
            RequestedBy = "demo-admin",
            RequestedAtUtc = resolvedAt.AddSeconds(-20),
            ResolutionAttemptCount = 1,
            ResolvedBy = "demo-admin",
            ResolvedAtUtc = resolvedAt
        });
        dbContext.InboxMessages.Add(new InboxMessageEntity
        {
            SourceMessageId = sourceMessageId,
            RouteId = route.Id,
            DestinationSystem = route.DestinationSystem,
            State = InboxState.Completed,
            AttemptCount = 1,
            FirstSeenAtUtc = detectedAt,
            UpdatedAtUtc = resolvedAt
        });
        await UpsertSnapshotAsync(
            route.Id, route.DestinationSystem, EntityType, entityId,
            scenario.Incoming, scenario.Incoming, resolvedAt, cancellationToken);
        if (route.Direction == SyncDirection.Bidirectional)
        {
            await UpsertSnapshotAsync(
                route.Id, route.SourceSystem, EntityType, entityId,
                scenario.Incoming, scenario.Incoming, resolvedAt, cancellationToken);
        }
    }

    private async Task SuppressSeedChangesAsync(
        Guid routeId,
        DateTimeOffset timestamp,
        IReadOnlyCollection<(Guid SourceMessageId, string DestinationSystem)> seedChanges,
        CancellationToken cancellationToken)
    {
        foreach (var seedChange in seedChanges)
        {
            var inbox = dbContext.InboxMessages.Local.SingleOrDefault(x =>
                            x.SourceMessageId == seedChange.SourceMessageId && x.RouteId == routeId &&
                            x.DestinationSystem == seedChange.DestinationSystem) ??
                        await dbContext.InboxMessages.SingleOrDefaultAsync(x =>
                            x.SourceMessageId == seedChange.SourceMessageId && x.RouteId == routeId &&
                            x.DestinationSystem == seedChange.DestinationSystem, cancellationToken);
            if (inbox is null)
            {
                inbox = new InboxMessageEntity
                {
                    SourceMessageId = seedChange.SourceMessageId,
                    RouteId = routeId,
                    DestinationSystem = seedChange.DestinationSystem,
                    FirstSeenAtUtc = timestamp
                };
                dbContext.InboxMessages.Add(inbox);
            }
            inbox.State = InboxState.Completed;
            inbox.AttemptCount = Math.Max(1, inbox.AttemptCount);
            inbox.UpdatedAtUtc = timestamp;
            inbox.LockedUntilUtc = null;
            inbox.LastError = null;
        }

        // 先に確定し、外部DBのTriggerをWorkerが拾ってもデモ生成操作として無視できるようにする。
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal static DemoConflictScenario CreateScenario(EntityPayload template) =>
        CreateScenario(template, EntityId);

    internal static DemoConflictScenario CreateScenario(EntityPayload template, string entityId)
    {
        var baselineFields = CloneFields(template.Fields);
        baselineFields["CaseNumber"] = JsonValue.Create(entityId);
        baselineFields["Subject"] = JsonValue.Create("冷風が出ない");
        baselineFields["Description"] = JsonValue.Create("運転を開始しても送風のみで、冷たい風が出ません。");
        var baseline = new EntityPayload(baselineFields);

        var incomingFields = CloneFields(baseline.Fields);
        incomingFields["Subject"] = JsonValue.Create("冷風が出ない（お客様から再連絡）");
        incomingFields["Description"] = JsonValue.Create("症状が悪化し、室温が下がらないため早めの訪問を希望します。");
        var incoming = new EntityPayload(incomingFields);

        var currentFields = CloneFields(baseline.Fields);
        currentFields["Subject"] = JsonValue.Create("冷房不良・訪問点検が必要");
        currentFields["Description"] = JsonValue.Create("CRM担当者が電話確認。室外機を含む現地点検が必要と判断しました。");
        var current = new EntityPayload(currentFields);
        return new DemoConflictScenario(baseline, incoming, current);
    }

    internal static DemoConflictScenario CreateDateConflictScenario(EntityPayload template, string entityId)
    {
        var baselineFields = CloneFields(template.Fields);
        baselineFields["CaseNumber"] = JsonValue.Create(entityId);
        baselineFields["Subject"] = JsonValue.Create("訪問希望日の変更");
        baselineFields["Description"] = JsonValue.Create("冷房点検の訪問日を調整しています。");
        baselineFields["PreferredVisitDate"] = JsonValue.Create("2026-07-22");
        var baseline = new EntityPayload(baselineFields);

        var incomingFields = CloneFields(baseline.Fields);
        incomingFields["PreferredVisitDate"] = JsonValue.Create("2026-07-24");
        var incoming = new EntityPayload(incomingFields);

        var currentFields = CloneFields(baseline.Fields);
        currentFields["PreferredVisitDate"] = JsonValue.Create("2026-07-23");
        var current = new EntityPayload(currentFields);
        return new DemoConflictScenario(baseline, incoming, current);
    }

    internal static DemoConflictScenario CreateDateTimeConflictScenario(string entityId)
        => CreateWorkOrderConflictScenario(
            entityId,
            "ScheduledAt",
            JsonValue.Create("2026-07-27T12:00:00+09:00"),
            JsonValue.Create("2026-07-27T14:00:00+09:00"),
            JsonValue.Create("2026-07-27T11:30:00+09:00"));

    internal static DemoConflictScenario CreateWorkOrderConflictScenario(
        string entityId,
        string fieldName,
        JsonNode? baselineValue,
        JsonNode? incomingValue,
        JsonNode? currentValue)
    {
        var baselineFields = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["WorkOrderNumber"] = JsonValue.Create(entityId),
            ["CaseRef"] = JsonValue.Create(entityId == DateTimeConflictEntityId ? "CASE-UPDATE-1001" : "CASE-TYPES-1001"),
            ["case_info.CaseRef"] = JsonValue.Create(entityId == DateTimeConflictEntityId ? "CASE-UPDATE-1001" : "CASE-TYPES-1001"),
            ["case_info.ContactName"] = JsonValue.Create("型別テスト 顧客"),
            ["case_info.ContactPhone"] = JsonValue.Create("070-3333-4444"),
            ["case_info.ProductLabel"] = JsonValue.Create("Demo Device Pro"),
            ["ServiceAddress"] = JsonValue.Create("東京都品川区東品川1-2-3"),
            ["ProblemSummary"] = JsonValue.Create("冷房不良のため訪問点検を実施します。"),
            ["ScheduledAt"] = JsonValue.Create("2026-07-27T12:00:00+09:00"),
            ["TechnicianName"] = JsonValue.Create("佐藤 健"),
            ["Status"] = JsonValue.Create("Scheduled"),
            ["WorkResult"] = null,
            ["CompletedAt"] = null,
            ["EstimatedMinutes"] = JsonValue.Create(60m),
            ["EstimatedCost"] = JsonValue.Create(10000.00m),
            ["RequiresParts"] = JsonValue.Create(false),
            ["WorkNote"] = JsonValue.Create("基準メモ"),
            ["ExternalTrackingId"] = JsonValue.Create("22222222-2222-2222-2222-222222222208"),
            ["OriginSystem"] = JsonValue.Create("CRM"),
            ["UpdatedAtUtc"] = JsonValue.Create("2026-07-18T03:00:00Z")
        };
        baselineFields[fieldName] = baselineValue?.DeepClone();
        var baseline = new EntityPayload(baselineFields);

        var incomingFields = CloneFields(baseline.Fields);
        incomingFields[fieldName] = incomingValue?.DeepClone();
        var incoming = new EntityPayload(incomingFields);

        var currentFields = CloneFields(baseline.Fields);
        currentFields[fieldName] = currentValue?.DeepClone();
        var current = new EntityPayload(currentFields);
        return new DemoConflictScenario(baseline, incoming, current);
    }

    internal static DemoDeleteConflictScenario CreateDeleteScenario(EntityPayload template) =>
        CreateDeleteScenario(template, DeleteEntityId);

    internal static DemoDeleteConflictScenario CreateDeleteScenario(EntityPayload template, string entityId)
    {
        var baselineFields = CloneFields(template.Fields);
        baselineFields["CaseNumber"] = JsonValue.Create(entityId);
        baselineFields["Subject"] = JsonValue.Create("訪問依頼の取り消し");
        baselineFields["Description"] = JsonValue.Create("お客様から訪問依頼を取り消したいと連絡がありました。");
        var baseline = new EntityPayload(baselineFields);

        var currentFields = CloneFields(baseline.Fields);
        currentFields["Subject"] = JsonValue.Create("訪問日程を調整中");
        currentFields["Description"] = JsonValue.Create("CRM担当者が訪問候補日を案内し、お客様からの回答を待っています。");
        var current = new EntityPayload(currentFields);
        return new DemoDeleteConflictScenario(baseline, current);
    }

    private async Task UpsertSnapshotAsync(
        Guid routeId,
        string destinationSystem,
        string entityType,
        string entityId,
        EntityPayload? source,
        EntityPayload? destination,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var entity = dbContext.SyncSnapshots.Local.SingleOrDefault(x =>
                         x.RouteId == routeId && x.DestinationSystem == destinationSystem &&
                         x.EntityType == entityType && x.EntityId == entityId) ??
                     await dbContext.SyncSnapshots.SingleOrDefaultAsync(x =>
                         x.RouteId == routeId && x.DestinationSystem == destinationSystem &&
                         x.EntityType == entityType && x.EntityId == entityId, cancellationToken);
        if (entity is null)
        {
            entity = new SyncSnapshotEntity
            {
                RouteId = routeId,
                DestinationSystem = destinationSystem,
                EntityType = entityType,
                EntityId = entityId
            };
            dbContext.SyncSnapshots.Add(entity);
        }
        entity.SourcePayloadJson = SerializePayload(source);
        entity.DestinationPayloadJson = SerializePayload(destination);
        entity.UpdatedAtUtc = now;
    }

    private static Dictionary<string, JsonNode?> CloneFields(IReadOnlyDictionary<string, JsonNode?> fields) =>
        fields.ToDictionary(x => x.Key, x => x.Value?.DeepClone(), StringComparer.Ordinal);

    private static string? SerializePayload(EntityPayload? payload) =>
        payload is null ? null : JsonSerializer.Serialize(payload.Fields, JsonOptions);

    private static Guid UpdateConflictId(Guid routeId, string entityId) =>
        WebhookEventId.Create("demo.conflict.v3.update.history", routeId, entityId);

    private static Guid DeleteConflictId(Guid routeId, string entityId) =>
        WebhookEventId.Create("demo.conflict.v3.delete.history", routeId, entityId);

    private static Guid ResolvedConflictId(Guid routeId, string entityId) =>
        WebhookEventId.Create("demo.conflict.v4.resolved.history", routeId, entityId);

    internal sealed record DemoConflictScenario(
        EntityPayload Baseline,
        EntityPayload Incoming,
        EntityPayload Current);

    internal sealed record DemoDeleteConflictScenario(
        EntityPayload Baseline,
        EntityPayload Current);
}
