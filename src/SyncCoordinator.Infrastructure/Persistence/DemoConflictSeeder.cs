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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<int> SeedIfReadyAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.SeedDemoData)
        {
            return 0;
        }

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
            return 0;
        }

        var conflictId = UpdateConflictId(routeInfo.Id, EntityId);
        var deleteConflictId = DeleteConflictId(routeInfo.Id, DeleteEntityId);
        var updateThenDeleteUpdateId = UpdateConflictId(routeInfo.Id, UpdateThenDeleteEntityId);
        var updateThenDeleteDeleteId = DeleteConflictId(routeInfo.Id, UpdateThenDeleteEntityId);
        var deleteThenUpdateDeleteId = DeleteConflictId(routeInfo.Id, DeleteThenUpdateEntityId);
        var deleteThenUpdateUpdateId = UpdateConflictId(routeInfo.Id, DeleteThenUpdateEntityId);
        Guid[] expectedConflictIds =
        [
            conflictId,
            deleteConflictId,
            updateThenDeleteUpdateId,
            updateThenDeleteDeleteId,
            deleteThenUpdateDeleteId,
            deleteThenUpdateUpdateId
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

        var legacyUpdateConflictId = WebhookEventId.Create(
            "demo.conflict.history", routeInfo.Id, TemplateEntityId);
        var legacyDeleteConflictId = WebhookEventId.Create(
            "demo.delete-conflict.history", routeInfo.Id, DeleteEntityId);
        var conflictIdsToReplace = new HashSet<Guid> { legacyUpdateConflictId, legacyDeleteConflictId };
        foreach (var entityId in new[] { EntityId, DeleteEntityId, UpdateThenDeleteEntityId, DeleteThenUpdateEntityId })
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
            DeleteThenUpdateEntityId
        ];
        var snapshotsToReplace = await dbContext.SyncSnapshots
            .Where(x => x.RouteId == routeInfo.Id && scenarioEntityIds.Contains(x.EntityId) &&
                        ((x.EntityId == EntityId && seedUpdateConflict) ||
                         (x.EntityId == DeleteEntityId && seedDeleteConflict) ||
                         (x.EntityId == UpdateThenDeleteEntityId && seedUpdateThenDelete) ||
                         (x.EntityId == DeleteThenUpdateEntityId && seedDeleteThenUpdate)))
            .ToListAsync(cancellationToken);
        dbContext.SyncSnapshots.RemoveRange(snapshotsToReplace);
        if (conflictsToReplace.Count > 0 || snapshotsToReplace.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (!seedUpdateConflict && !seedDeleteConflict && !seedUpdateThenDelete && !seedDeleteThenUpdate)
        {
            return 0;
        }

        var route = (await store.GetRoutesAsync("PORTAL", "PORTAL", EntityType, cancellationToken))
            .SingleOrDefault(x => x.Id == routeInfo.Id);
        if (route is null || route.OperationallyPaused)
        {
            return 0;
        }

        var source = await connectors.GetRequiredAsync(route.SourceSystem, cancellationToken);
        var destination = await connectors.GetRequiredAsync(route.DestinationSystem, cancellationToken);
        var sourceCurrent = await source.ReadCurrentAsync(EntityType, TemplateEntityId, cancellationToken);
        if (sourceCurrent is null)
        {
            // Customer Portalの初期データがまだ準備されていない場合だけ次周期へ送る。
            return 0;
        }

        var now = timeProvider.GetUtcNow();
        var seeded = 0;
        if (seedUpdateConflict)
        {
            await SeedUpdateConflictAsync(
                route, source, destination, sourceCurrent, EntityId, conflictId,
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
                route, source, destination, sourceCurrent, UpdateThenDeleteEntityId, updateThenDeleteUpdateId,
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
                route, source, destination, sourceCurrent, DeleteThenUpdateEntityId, deleteThenUpdateUpdateId,
                now, deleteThenUpdateDeleteId, cancellationToken);
            seeded += 2;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return seeded;
    }

    private async Task SeedUpdateConflictAsync(
        SyncRouteDefinition route,
        ISyncConnector source,
        ISyncConnector destination,
        EntityPayload sourceCurrent,
        string entityId,
        Guid conflictId,
        DateTimeOffset detectedAt,
        Guid? previousConflictId,
        CancellationToken cancellationToken)
    {
        var scenario = CreateScenario(sourceCurrent, entityId);
        var sourceSeedMessageId = WebhookEventId.Create("demo.conflict.v3.update.source-change", route.Id, entityId);
        var destinationSeedMessageId = WebhookEventId.Create("demo.conflict.v3.update.destination-change", route.Id, entityId);
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
            SyncPayloadTransformer.TransformFromCanonical(scenario.Current, route, route.DestinationSystem)), cancellationToken);

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
            route.Id, route.DestinationSystem, entityId,
            scenario.Incoming, resolution.AdoptedPayload, detectedAt, cancellationToken);
        if (route.Direction == SyncDirection.Bidirectional)
        {
            await UpsertSnapshotAsync(
                route.Id, route.SourceSystem, entityId,
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
            route.Id, route.DestinationSystem, entityId,
            null, resolution.AdoptedPayload, detectedAt, cancellationToken);
        if (route.Direction == SyncDirection.Bidirectional)
        {
            await UpsertSnapshotAsync(
                route.Id, route.SourceSystem, entityId,
                resolution.AdoptedPayload, null, detectedAt, cancellationToken);
        }
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
        string entityId,
        EntityPayload? source,
        EntityPayload? destination,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var entity = dbContext.SyncSnapshots.Local.SingleOrDefault(x =>
                         x.RouteId == routeId && x.DestinationSystem == destinationSystem &&
                         x.EntityType == EntityType && x.EntityId == entityId) ??
                     await dbContext.SyncSnapshots.SingleOrDefaultAsync(x =>
                         x.RouteId == routeId && x.DestinationSystem == destinationSystem &&
                         x.EntityType == EntityType && x.EntityId == entityId, cancellationToken);
        if (entity is null)
        {
            entity = new SyncSnapshotEntity
            {
                RouteId = routeId,
                DestinationSystem = destinationSystem,
                EntityType = EntityType,
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

    internal sealed record DemoConflictScenario(
        EntityPayload Baseline,
        EntityPayload Incoming,
        EntityPayload Current);

    internal sealed record DemoDeleteConflictScenario(
        EntityPayload Baseline,
        EntityPayload Current);
}
