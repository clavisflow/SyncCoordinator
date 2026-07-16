using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SyncCoordinator.Contracts;
using SyncCoordinator.Core;
using SyncCoordinator.Infrastructure.Persistence;

namespace SyncCoordinator.Tests;

public sealed class ConflictResolutionServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ResolvingOldestAppliesAutomaticFieldsBeforeHoldingNextMixedConflict()
    {
        var databaseName = $"SyncCoordinatorConflictResolutionTests_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<CoordinatorDbContext>()
            .UseSqlServer($"Server=(localdb)\\mssqllocaldb;Database={databaseName};Trusted_Connection=True")
            .Options;

        await using var context = new CoordinatorDbContext(options);
        try
        {
            await context.Database.EnsureCreatedAsync();
            var routeId = Guid.NewGuid();
            var oldestId = Guid.NewGuid();
            var nextId = Guid.NewGuid();
            var oldestMessageId = Guid.NewGuid();
            var nextMessageId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var sourceSystem = System("A", "Source");
            var destinationSystem = System("C", "Destination");
            var routeEntity = new SyncRouteEntity
            {
                Id = routeId,
                Name = "route",
                SourceSystem = sourceSystem,
                DestinationSystem = destinationSystem,
                SourceSystemId = sourceSystem.Id,
                DestinationSystemId = destinationSystem.Id,
                EntityType = "Sample",
                Direction = SyncDirection.OneWay,
                ConflictScope = ConflictScope.Field,
                DefaultConflictPolicy = ConflictPolicy.HoldAndNotify,
                DeploymentState = DatabaseDeploymentState.Prepared,
                Enabled = true
            };
            context.AddRange(sourceSystem, destinationSystem, routeEntity);

            var baseline = Fields("base-auto", "base-manual");
            var oldestIncoming = Fields("base-auto", "old-incoming-manual");
            var oldestCurrent = Fields("old-current-auto", "old-current-manual");
            var nextIncoming = Fields("next-incoming-auto", "next-incoming-manual");
            context.SyncSnapshots.Add(Snapshot(routeId, baseline));
            context.SyncConflicts.AddRange(
                Conflict(oldestId, routeId, oldestMessageId, now, oldestIncoming, oldestCurrent,
                    [HeldField("Manual", "base-manual", "old-incoming-manual", "old-current-manual")]),
                Conflict(nextId, routeId, nextMessageId, now.AddMinutes(1), nextIncoming, oldestCurrent,
                    [HeldField("Manual", "old-current-manual", "next-incoming-manual", "old-current-manual")], oldestId));
            context.InboxMessages.AddRange(
                Inbox(oldestMessageId, routeId, now),
                Inbox(nextMessageId, routeId, now.AddMinutes(1)));
            await context.SaveChangesAsync();

            var route = new SyncRouteDefinition(
                routeId, "route", "A", "C", "Sample", SyncDirection.OneWay,
                null, null, ConflictScope.Field, ConflictPolicy.HoldAndNotify, true,
                new Dictionary<string, ConflictPolicy>(StringComparer.Ordinal)
                {
                    ["Automatic"] = ConflictPolicy.ApplyIncomingAndNotify,
                    ["Manual"] = ConflictPolicy.HoldAndNotify
                });
            var connector = new StatefulConnector("C", oldestCurrent)
            {
                CurrentAfterFirstApply = Fields("external-auto", "external-manual")
            };
            var service = new ConflictResolutionService(
                context,
                new RouteStore(route),
                new ConnectorCatalog([connector]),
                new ConflictResolver(new NoOpConflictValueMerger()),
                TimeProvider.System);

            var details = await service.GetAsync(oldestId, CancellationToken.None);
            Assert.NotNull(details?.CurrentVersionToken);
            await service.QueueAsync(oldestId, new ConflictResolutionInput
            {
                ExpectedCurrentVersionToken = details.CurrentVersionToken!,
                Fields =
                [
                    new FieldResolutionInput
                    {
                        FieldName = "Manual",
                        Choice = ManualConflictChoice.Current
                    }
                ]
            }, "tester", CancellationToken.None);

            Assert.Equal(1, await service.ProcessPendingAsync(10, CancellationToken.None));

            context.ChangeTracker.Clear();
            var oldest = await context.SyncConflicts.SingleAsync(x => x.Id == oldestId);
            var next = await context.SyncConflicts.SingleAsync(x => x.Id == nextId);
            var nextInbox = await context.InboxMessages.SingleAsync(x => x.SourceMessageId == nextMessageId);
            var snapshot = await context.SyncSnapshots.SingleAsync(x => x.RouteId == routeId && x.DestinationSystem == "C");
            var snapshotDestination = Deserialize(snapshot.DestinationPayloadJson)!;

            Assert.Equal(ConflictResolutionState.Resolved, oldest.ResolutionState);
            Assert.Equal(ConflictResolutionState.AwaitingDecision, next.ResolutionState);
            Assert.Equal(InboxState.Held, nextInbox.State);
            Assert.Equal(2, connector.ApplyCalls);
            Assert.Equal("next-incoming-auto", connector.Current!.Fields["Automatic"]!.GetValue<string>());
            Assert.Equal("external-manual", connector.Current.Fields["Manual"]!.GetValue<string>());
            Assert.Equal("next-incoming-auto", snapshotDestination.Fields["Automatic"]!.GetValue<string>());
            Assert.Equal("external-manual", snapshotDestination.Fields["Manual"]!.GetValue<string>());
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task FailedResolutionCanBeQueuedAgainAndCompletesOnRetry()
    {
        var databaseName = $"SyncCoordinatorConflictRetryTests_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<CoordinatorDbContext>()
            .UseSqlServer($"Server=(localdb)\\mssqllocaldb;Database={databaseName};Trusted_Connection=True")
            .Options;

        await using var context = new CoordinatorDbContext(options);
        try
        {
            await context.Database.EnsureCreatedAsync();
            var routeId = Guid.NewGuid();
            var conflictId = Guid.NewGuid();
            var messageId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var sourceSystem = System("A", "Source");
            var destinationSystem = System("C", "Destination");
            context.AddRange(sourceSystem, destinationSystem, new SyncRouteEntity
            {
                Id = routeId,
                Name = "route",
                SourceSystem = sourceSystem,
                DestinationSystem = destinationSystem,
                SourceSystemId = sourceSystem.Id,
                DestinationSystemId = destinationSystem.Id,
                EntityType = "Sample",
                Direction = SyncDirection.OneWay,
                ConflictScope = ConflictScope.Field,
                DefaultConflictPolicy = ConflictPolicy.HoldAndNotify,
                DeploymentState = DatabaseDeploymentState.Prepared,
                Enabled = true
            });
            var baseline = Fields("base-auto", "base-manual");
            var incoming = Fields("base-auto", "incoming-manual");
            var current = Fields("current-auto", "current-manual");
            context.SyncSnapshots.Add(Snapshot(routeId, baseline));
            context.SyncConflicts.Add(Conflict(conflictId, routeId, messageId, now, incoming, current,
                [HeldField("Manual", "base-manual", "incoming-manual", "current-manual")]));
            context.InboxMessages.Add(Inbox(messageId, routeId, now));
            await context.SaveChangesAsync();

            var route = new SyncRouteDefinition(
                routeId, "route", "A", "C", "Sample", SyncDirection.OneWay,
                null, null, ConflictScope.Field, ConflictPolicy.HoldAndNotify, true,
                new Dictionary<string, ConflictPolicy>());
            var connector = new StatefulConnector("C", current) { FailNextApply = true };
            var service = new ConflictResolutionService(
                context,
                new RouteStore(route),
                new ConnectorCatalog([connector]),
                new ConflictResolver(new NoOpConflictValueMerger()),
                TimeProvider.System);

            await QueueKeepingCurrentAsync(service, conflictId);
            Assert.Equal(1, await service.ProcessPendingAsync(10, CancellationToken.None));
            context.ChangeTracker.Clear();
            Assert.Equal(ConflictResolutionState.Failed,
                (await context.SyncConflicts.SingleAsync(x => x.Id == conflictId)).ResolutionState);

            await QueueKeepingCurrentAsync(service, conflictId);
            Assert.Equal(1, await service.ProcessPendingAsync(10, CancellationToken.None));
            context.ChangeTracker.Clear();

            var resolved = await context.SyncConflicts.SingleAsync(x => x.Id == conflictId);
            var inbox = await context.InboxMessages.SingleAsync(x => x.SourceMessageId == messageId);
            Assert.Equal(ConflictResolutionState.Resolved, resolved.ResolutionState);
            Assert.Equal(InboxState.Completed, inbox.State);
            Assert.Equal(2, connector.ApplyCalls);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task ResolvingNewestSupersedesOlderUpdateAndDeleteConflictsWhileMiddleStaysReadOnly()
    {
        var databaseName = $"SyncCoordinatorConflictNewestTests_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<CoordinatorDbContext>()
            .UseSqlServer($"Server=(localdb)\\mssqllocaldb;Database={databaseName};Trusted_Connection=True")
            .Options;

        await using var context = new CoordinatorDbContext(options);
        try
        {
            await context.Database.EnsureCreatedAsync();
            var routeId = Guid.NewGuid();
            var oldestId = Guid.NewGuid();
            var middleId = Guid.NewGuid();
            var newestId = Guid.NewGuid();
            var oldestMessageId = Guid.NewGuid();
            var middleMessageId = Guid.NewGuid();
            var newestMessageId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var sourceSystem = System("A", "Source");
            var destinationSystem = System("C", "Destination");
            context.AddRange(sourceSystem, destinationSystem, new SyncRouteEntity
            {
                Id = routeId,
                Name = "route",
                SourceSystem = sourceSystem,
                DestinationSystem = destinationSystem,
                SourceSystemId = sourceSystem.Id,
                DestinationSystemId = destinationSystem.Id,
                EntityType = "Sample",
                Direction = SyncDirection.OneWay,
                ConflictScope = ConflictScope.Field,
                DefaultConflictPolicy = ConflictPolicy.HoldAndNotify,
                DeploymentState = DatabaseDeploymentState.Prepared,
                Enabled = true
            });
            var baseline = Fields("base-auto", "base-manual");
            var current = Fields("current-auto", "current-manual");
            var field = HeldField("Manual", "base-manual", "incoming-manual", "current-manual");
            context.SyncSnapshots.Add(Snapshot(routeId, baseline));
            context.SyncConflicts.AddRange(
                Conflict(oldestId, routeId, oldestMessageId, now, Fields("base-auto", "oldest-incoming"), current, [field]),
                Conflict(middleId, routeId, middleMessageId, now.AddMinutes(1), Fields("base-auto", "middle-delete"), current,
                    [field], oldestId, ChangeOperation.Delete, ConflictResolutionState.WaitingForPrevious),
                Conflict(newestId, routeId, newestMessageId, now.AddMinutes(2), Fields("base-auto", "newest-incoming"), current,
                    [field], middleId));
            context.InboxMessages.AddRange(
                Inbox(oldestMessageId, routeId, now),
                Inbox(middleMessageId, routeId, now.AddMinutes(1), InboxState.WaitingForPrevious),
                Inbox(newestMessageId, routeId, now.AddMinutes(2)));
            await context.SaveChangesAsync();

            var route = new SyncRouteDefinition(
                routeId, "route", "A", "C", "Sample", SyncDirection.OneWay,
                new DeletionBehavior(DeletionMode.Physical),
                new DeletionBehavior(DeletionMode.Physical),
                ConflictScope.Field, ConflictPolicy.HoldAndNotify, true,
                new Dictionary<string, ConflictPolicy>());
            var connector = new StatefulConnector("C", current);
            var service = new ConflictResolutionService(
                context,
                new RouteStore(route),
                new ConnectorCatalog([connector]),
                new ConflictResolver(new NoOpConflictValueMerger()),
                TimeProvider.System);

            Assert.False((await service.GetAsync(middleId, CancellationToken.None))!.CanResolve);
            Assert.True((await service.GetAsync(newestId, CancellationToken.None))!.CanResolve);
            await QueueKeepingCurrentAsync(service, newestId);
            Assert.Equal(1, await service.ProcessPendingAsync(10, CancellationToken.None));

            context.ChangeTracker.Clear();
            Assert.Equal(ConflictResolutionState.Superseded,
                (await context.SyncConflicts.SingleAsync(x => x.Id == oldestId)).ResolutionState);
            Assert.Equal(ConflictResolutionState.Superseded,
                (await context.SyncConflicts.SingleAsync(x => x.Id == middleId)).ResolutionState);
            Assert.Equal(ConflictResolutionState.Resolved,
                (await context.SyncConflicts.SingleAsync(x => x.Id == newestId)).ResolutionState);
            Assert.Equal(InboxState.Superseded,
                (await context.InboxMessages.SingleAsync(x => x.SourceMessageId == oldestMessageId)).State);
            Assert.Equal(InboxState.Superseded,
                (await context.InboxMessages.SingleAsync(x => x.SourceMessageId == middleMessageId)).State);
            Assert.Equal(1, connector.ApplyCalls);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    private static async Task QueueKeepingCurrentAsync(ConflictResolutionService service, Guid conflictId)
    {
        var details = await service.GetAsync(conflictId, CancellationToken.None);
        Assert.NotNull(details?.CurrentVersionToken);
        await service.QueueAsync(conflictId, new ConflictResolutionInput
        {
            ExpectedCurrentVersionToken = details.CurrentVersionToken!,
            Fields =
            [
                new FieldResolutionInput
                {
                    FieldName = "Manual",
                    Choice = ManualConflictChoice.Current
                }
            ]
        }, "tester", CancellationToken.None);
    }

    private static SystemDefinitionEntity System(string code, string name) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        DisplayName = name,
        Provider = "SqlServer",
        Enabled = true
    };

    private static EntityPayload Fields(string automatic, string manual) => new(
        new Dictionary<string, JsonNode?>
        {
            ["Automatic"] = JsonValue.Create(automatic),
            ["Manual"] = JsonValue.Create(manual)
        });

    private static SyncSnapshotEntity Snapshot(Guid routeId, EntityPayload payload) => new()
    {
        RouteId = routeId,
        DestinationSystem = "C",
        EntityType = "Sample",
        EntityId = "1",
        SourcePayloadJson = Serialize(payload),
        DestinationPayloadJson = Serialize(payload),
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };

    private static SyncConflictEntity Conflict(
        Guid id,
        Guid routeId,
        Guid messageId,
        DateTimeOffset detectedAt,
        EntityPayload incoming,
        EntityPayload current,
        IReadOnlyList<FieldConflict> fields,
        Guid? previousId = null,
        ChangeOperation operation = ChangeOperation.Upsert,
        ConflictResolutionState state = ConflictResolutionState.AwaitingDecision) => new()
        {
            Id = id,
            RouteId = routeId,
            SourceMessageId = messageId,
            DeliveryMessageId = Guid.NewGuid(),
            SourceSystem = "A",
            DestinationSystem = "C",
            EntityType = "Sample",
            EntityId = "1",
            Operation = operation,
            Scope = ConflictScope.Field,
            FieldsJson = JsonSerializer.Serialize(fields, JsonOptions),
            HadBaseline = true,
            BaselineSourcePayloadJson = Serialize(Fields("base-auto", "base-manual")),
            BaselineDestinationPayloadJson = Serialize(Fields("base-auto", "base-manual")),
            IncomingPayloadJson = Serialize(incoming)!,
            CurrentPayloadJson = Serialize(current),
            DetectedAtUtc = detectedAt,
            ResolutionState = state,
            PreviousConflictId = previousId
        };

    private static FieldConflict HeldField(string name, string baseline, string incoming, string current) => new(
        name, JsonValue.Create(baseline), JsonValue.Create(incoming), JsonValue.Create(current),
        JsonValue.Create(current), ConflictPolicy.HoldAndNotify, "Held");

    private static InboxMessageEntity Inbox(
        Guid messageId,
        Guid routeId,
        DateTimeOffset now,
        InboxState state = InboxState.Held) => new()
    {
        SourceMessageId = messageId,
        RouteId = routeId,
        DestinationSystem = "C",
        State = state,
        FirstSeenAtUtc = now,
        UpdatedAtUtc = now
    };

    private static string? Serialize(EntityPayload? payload) =>
        payload is null ? null : JsonSerializer.Serialize(payload.Fields, JsonOptions);

    private static EntityPayload? Deserialize(string? json) =>
        json is null ? null : new EntityPayload(
            JsonSerializer.Deserialize<Dictionary<string, JsonNode?>>(json, JsonOptions) ?? []);

    private sealed class StatefulConnector(string systemCode, EntityPayload current) : ISyncConnector
    {
        public string SystemCode { get; } = systemCode;
        public EntityPayload? Current { get; private set; } = current;
        public EntityPayload? CurrentAfterFirstApply { get; init; }
        public bool FailNextApply { get; set; }
        public int ApplyCalls { get; private set; }

        public Task<IReadOnlyList<ChangeQueueItem>> ReadChangesAsync(long afterQueueId, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ChangeQueueItem>>([]);
        public Task<bool> WasAppliedMessageAsync(Guid messageId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<SyncMessage?> ReadLatestMessageAsync(ChangeQueueItem change, CancellationToken cancellationToken) => Task.FromResult<SyncMessage?>(null);
        public Task<EntityPayload?> ReadCurrentAsync(string entityType, string entityId, CancellationToken cancellationToken) => Task.FromResult(Current);
        public Task<ApplyResult> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
        {
            ApplyCalls++;
            if (FailNextApply)
            {
                FailNextApply = false;
                return Task.FromException<ApplyResult>(new InvalidOperationException("destination unavailable"));
            }
            Current = ApplyCalls == 1 && CurrentAfterFirstApply is not null
                ? CurrentAfterFirstApply
                : request.Payload;
            return Task.FromResult(new ApplyResult(ApplyStatus.Applied));
        }
    }

    private sealed class RouteStore(SyncRouteDefinition route) : ICoordinatorStore
    {
        public Task<IReadOnlyList<SyncRouteDefinition>> GetRoutesAsync(string sourceSystem, string originSystem, string entityType, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SyncRouteDefinition>>([route]);
        public Task<bool> IsGloballyPausedAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsSystemPausedAsync(string systemCode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<long> GetCheckpointAsync(string systemCode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AdvanceCheckpointAsync(string systemCode, long queueId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InboxAcquireResult> TryBeginInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, InboxState state, WebhookEventNotification? webhookEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, string errorDetails, WebhookEventNotification webhookEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task HoldInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, string errorDetails, WebhookEventNotification webhookEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SyncSnapshot?> GetSnapshotAsync(Guid routeId, string destinationSystem, string entityType, string entityId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveSnapshotAsync(SyncSnapshot snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveConflictAsync(ConflictHistory conflict, WebhookEventNotification webhookEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
