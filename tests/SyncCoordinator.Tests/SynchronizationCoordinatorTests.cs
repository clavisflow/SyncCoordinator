using Microsoft.Extensions.Logging.Abstractions;
using SyncCoordinator.Contracts;
using SyncCoordinator.Core;

namespace SyncCoordinator.Tests;

public sealed class SynchronizationCoordinatorTests
{
    [Fact]
    public async Task GlobalPauseLeavesAllQueuesUntouched()
    {
        var connector = new FakeConnector();
        var store = new RecordingStore(Route(), null) { GlobalPaused = true };
        var coordinator = new SynchronizationCoordinator(
            new ConnectorCatalog([connector]),
            store,
            new ConflictResolver(new NoOpConflictValueMerger()),
            TimeProvider.System,
            NullLogger<SynchronizationCoordinator>.Instance);

        var processed = await coordinator.RunOnceAsync(10, CancellationToken.None);

        Assert.Equal(0, processed);
        Assert.Equal(0, connector.ReadChangesCalls);
        Assert.Equal(0, store.Checkpoint);
    }

    [Fact]
    public async Task RunOnceSkipsAppliedMessageAndAdvancesCheckpoint()
    {
        var connector = new FakeConnector { AppliedMessage = true };
        var store = new FakeStore();
        var coordinator = new SynchronizationCoordinator(
            new ConnectorCatalog([connector]),
            store,
            new ConflictResolver(new NoOpConflictValueMerger()),
            TimeProvider.System,
            NullLogger<SynchronizationCoordinator>.Instance);

        var processed = await coordinator.RunOnceAsync(10, CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Equal(42, store.Checkpoint);
        Assert.Equal(0, connector.ReadMessageCalls);
    }

    [Fact]
    public async Task SourceReadFailureKeepsItsCheckpointAndDoesNotBlockOtherSources()
    {
        var failedSource = new FakeConnector
        {
            SystemCode = "A",
            ReadChangesException = new InvalidOperationException("source unavailable")
        };
        var healthySource = new FakeConnector
        {
            SystemCode = "B",
            AppliedMessage = true
        };
        var store = new PerSystemCheckpointStore();
        var operationalEvents = new RecordingOperationalEventRecorder();
        var coordinator = new SynchronizationCoordinator(
            new ConnectorCatalog([failedSource, healthySource]),
            store,
            new ConflictResolver(new NoOpConflictValueMerger()),
            TimeProvider.System,
            NullLogger<SynchronizationCoordinator>.Instance,
            operationalEvents);

        var processed = await coordinator.RunOnceAsync(10, CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Equal(0, store.Checkpoint("A"));
        Assert.Equal(42, store.Checkpoint("B"));
        Assert.Equal(1, failedSource.ReadChangesCalls);
        Assert.Equal(1, healthySource.ReadChangesCalls);
        var recorded = Assert.Single(operationalEvents.Events);
        Assert.Equal(OperationalEventCodes.SynchronizationPollingFailed, recorded.Code);
        Assert.Equal("A", recorded.Target);
        Assert.Contains("source unavailable", recorded.Details);
    }

    [Fact]
    public async Task DeleteUsesDestinationLogicalDeletionBehavior()
    {
        var messageId = Guid.NewGuid();
        var payload = new EntityPayload(new Dictionary<string, System.Text.Json.Nodes.JsonNode?>
        {
            ["Value"] = System.Text.Json.Nodes.JsonValue.Create("base")
        });
        var source = new FakeConnector
        {
            SystemCode = "A",
            MessageId = messageId,
            Operation = ChangeOperation.Delete,
            Message = new SyncMessage(messageId, "A", "A", "Sample", "1", ChangeOperation.Delete, DateTimeOffset.UtcNow, payload)
        };
        var destination = new FakeConnector
        {
            SystemCode = "C",
            EmitChange = false,
            Current = payload
        };
        var route = new SyncRouteDefinition(
            Guid.NewGuid(), "route", "A", "C", "Sample", SyncDirection.OneWay,
            new DeletionBehavior(DeletionMode.Physical),
            new DeletionBehavior(DeletionMode.Logical, "IsDeleted", "1"),
            ConflictScope.Field, ConflictPolicy.HoldAndNotify, true,
            new Dictionary<string, ConflictPolicy>());
        var store = new DeleteFlowStore(route, payload);
        var coordinator = new SynchronizationCoordinator(
            new ConnectorCatalog([source, destination]),
            store,
            new ConflictResolver(new NoOpConflictValueMerger()),
            TimeProvider.System,
            NullLogger<SynchronizationCoordinator>.Instance);

        await coordinator.RunOnceAsync(10, CancellationToken.None);

        var applied = Assert.IsType<ApplyRequest>(destination.AppliedRequest);
        Assert.Equal(ChangeOperation.Delete, applied.Operation);
        Assert.Equal(DeletionMode.Logical, applied.DeletionBehavior!.Mode);
        Assert.Equal("IsDeleted", applied.DeletionBehavior.LogicalDeleteColumn);
        Assert.Equal("1", applied.DeletionBehavior.LogicalDeleteValue);
    }

    [Fact]
    public async Task EligibilityRemovalDoesNotDeleteEntityThatWasNeverSynchronized()
    {
        var messageId = Guid.NewGuid();
        var payload = Payload("destination-owned");
        var source = new FakeConnector
        {
            SystemCode = "A",
            MessageId = messageId,
            Operation = ChangeOperation.Delete,
            Message = new SyncMessage(
                messageId, "A", "A", "Sample", "1", ChangeOperation.Delete, DateTimeOffset.UtcNow, payload)
            {
                IsEligibilityRemoval = true
            }
        };
        var destination = new FakeConnector { SystemCode = "C", EmitChange = false, Current = payload };
        var route = Route(destinationDeletion: new DeletionBehavior(DeletionMode.Physical));
        var store = new RecordingStore(route, null);

        var processed = await Coordinator(source, destination, store).RunOnceAsync(10, CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Equal(0, destination.ApplyCalls);
        Assert.Empty(store.SavedSnapshots);
        Assert.Null(store.CompletedWebhook);
        Assert.Equal(42, store.Checkpoint);
    }

    [Fact]
    public async Task EligibilityRemovalDeletesEntityThatWasPreviouslySynchronized()
    {
        var messageId = Guid.NewGuid();
        var payload = Payload("previously-synchronized");
        var source = new FakeConnector
        {
            SystemCode = "A",
            MessageId = messageId,
            Operation = ChangeOperation.Delete,
            Message = new SyncMessage(
                messageId, "A", "A", "Sample", "1", ChangeOperation.Delete, DateTimeOffset.UtcNow, payload)
            {
                IsEligibilityRemoval = true
            }
        };
        var destination = new FakeConnector { SystemCode = "C", EmitChange = false, Current = payload };
        var route = Route(destinationDeletion: new DeletionBehavior(DeletionMode.Physical));
        var store = new RecordingStore(route, Snapshot(route, payload));

        await Coordinator(source, destination, store).RunOnceAsync(10, CancellationToken.None);

        Assert.Equal(1, destination.ApplyCalls);
        Assert.Equal(ChangeOperation.Delete, destination.AppliedRequest!.Operation);
    }

    [Fact]
    public async Task SourceReadValidationFailureIsHeldWithoutBlockingCheckpoint()
    {
        var messageId = Guid.NewGuid();
        var payload = Payload("key-only");
        var source = new FakeConnector
        {
            SystemCode = "A",
            MessageId = messageId,
            Message = new SyncMessage(
                messageId, "A", "A", "Sample", "1", ChangeOperation.Upsert, DateTimeOffset.UtcNow, payload)
            {
                ValidationFailure = new SyncValidationFailure(
                    "reception", "WorkRequest", "related-projection-multiple-rows", "projection returned two rows")
            }
        };
        var destination = new FakeConnector { SystemCode = "C", EmitChange = false };
        var route = Route();
        var store = new RecordingStore(route, null);

        var processed = await Coordinator(source, destination, store).RunOnceAsync(10, CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Equal(1, store.HeldDeliveries);
        Assert.Equal(0, destination.ApplyCalls);
        Assert.Equal(42, store.Checkpoint);
    }

    [Fact]
    public async Task RunOnceCoalescesNotificationsAndAppliesLatestStateOnce()
    {
        var firstMessageId = Guid.NewGuid();
        var latestMessageId = Guid.NewGuid();
        var baseline = Payload("old");
        var latest = Payload("latest");
        var source = new FakeConnector
        {
            SystemCode = "A",
            Changes =
            [
                new ChangeQueueItem(41, firstMessageId, "Sample", "1", ChangeOperation.Upsert, DateTimeOffset.UtcNow.AddMinutes(-1)),
                new ChangeQueueItem(42, latestMessageId, "Sample", "1", ChangeOperation.Upsert, DateTimeOffset.UtcNow)
            ],
            Message = new SyncMessage(latestMessageId, "A", "A", "Sample", "1", ChangeOperation.Upsert, DateTimeOffset.UtcNow, latest)
        };
        var destination = new FakeConnector { SystemCode = "C", EmitChange = false, Current = baseline };
        var route = Route();
        var store = new RecordingStore(route, Snapshot(route, baseline));
        var coordinator = Coordinator(source, destination, store);

        var processed = await coordinator.RunOnceAsync(10, CancellationToken.None);

        Assert.Equal(2, processed);
        Assert.Equal(1, source.ReadMessageCalls);
        Assert.Equal(1, destination.ApplyCalls);
        Assert.Equal("latest", destination.AppliedRequest!.Payload.Fields["Value"]!.GetValue<string>());
        Assert.Equal(42, store.Checkpoint);
        Assert.Equal("latest", Assert.Single(store.SavedSnapshots).SourcePayload!.Fields["Value"]!.GetValue<string>());
        Assert.Equal(WebhookEventTypes.SyncUpserted, store.CompletedWebhook?.EventType);
    }

    [Fact]
    public async Task HeldConflictPersistsPayloadsAndPhysicalFieldNamesForManualResolution()
    {
        var baseline = Payload("base");
        var incoming = Payload("incoming");
        var current = Payload("current");
        var source = new FakeConnector
        {
            SystemCode = "A",
            Message = new SyncMessage(Guid.NewGuid(), "A", "A", "Sample", "1", ChangeOperation.Upsert, DateTimeOffset.UtcNow, incoming)
        };
        var destination = new FakeConnector { SystemCode = "C", EmitChange = false, Current = current };
        var route = Route() with
        {
            ValueMappings = new Dictionary<string, ColumnValueMappingDefinition>(StringComparer.Ordinal)
            {
                ["Value"] = new(
                    "Value", "value_text", ColumnValueContract.Unknown, ColumnValueContract.Unknown,
                    new ValueTransformInput(), new ValueTransformInput())
            }
        };
        var store = new RecordingStore(route, Snapshot(route, baseline));

        await Coordinator(source, destination, store).RunOnceAsync(10, CancellationToken.None);

        var conflict = Assert.IsType<ConflictHistory>(store.SavedConflict);
        Assert.True(conflict.RequiresDecision);
        Assert.Equal(ChangeOperation.Upsert, conflict.Operation);
        Assert.Equal("incoming", conflict.IncomingPayload.Fields["Value"]!.GetValue<string>());
        Assert.Equal("current", conflict.CurrentPayload!.Fields["Value"]!.GetValue<string>());
        Assert.NotNull(conflict.Baseline);
        var field = Assert.Single(conflict.Fields);
        Assert.Equal("Value", field.IncomingFieldName);
        Assert.Equal("value_text", field.CurrentFieldName);
    }

    [Fact]
    public async Task AutomaticConflictIsPersistedOnlyAfterDestinationApplySucceeds()
    {
        var events = new List<string>();
        var baseline = Payload("base");
        var incoming = Payload("incoming");
        var current = Payload("current");
        var source = new FakeConnector
        {
            SystemCode = "A",
            Message = new SyncMessage(Guid.NewGuid(), "A", "A", "Sample", "1", ChangeOperation.Upsert, DateTimeOffset.UtcNow, incoming)
        };
        var destination = new FakeConnector { SystemCode = "C", EmitChange = false, Current = current, Events = events };
        var route = Route() with { DefaultConflictPolicy = ConflictPolicy.ApplyIncomingAndNotify };
        var store = new RecordingStore(route, Snapshot(route, baseline)) { Events = events };

        var processed = await Coordinator(source, destination, store).RunOnceAsync(10, CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Equal(["apply", "save-conflict"], events);
        Assert.False(Assert.IsType<ConflictHistory>(store.SavedConflict).RequiresDecision);
    }

    [Fact]
    public async Task AutomaticConflictIsNotFinalizedWhenDestinationApplyFails()
    {
        var baseline = Payload("base");
        var incoming = Payload("incoming");
        var current = Payload("current");
        var source = new FakeConnector
        {
            SystemCode = "A",
            Message = new SyncMessage(Guid.NewGuid(), "A", "A", "Sample", "1", ChangeOperation.Upsert, DateTimeOffset.UtcNow, incoming)
        };
        var destination = new FakeConnector
        {
            SystemCode = "C",
            EmitChange = false,
            Current = current,
            ApplyException = new InvalidOperationException("destination unavailable")
        };
        var route = Route() with { DefaultConflictPolicy = ConflictPolicy.ApplyIncomingAndNotify };
        var store = new RecordingStore(route, Snapshot(route, baseline));

        var processed = await Coordinator(source, destination, store).RunOnceAsync(10, CancellationToken.None);

        Assert.Equal(0, processed);
        Assert.Null(store.SavedConflict);
        Assert.Equal(1, store.FailedDeliveries);
    }

    [Fact]
    public async Task FieldConflictAppliesAutomaticFieldsBeforeHoldingManualFields()
    {
        var events = new List<string>();
        static EntityPayload Fields(string automatic, string manual) =>
            new(new Dictionary<string, System.Text.Json.Nodes.JsonNode?>
            {
                ["Automatic"] = System.Text.Json.Nodes.JsonValue.Create(automatic),
                ["Manual"] = System.Text.Json.Nodes.JsonValue.Create(manual)
            });

        var baseline = Fields("base-auto", "base-manual");
        var incoming = Fields("incoming-auto", "incoming-manual");
        var current = Fields("current-auto", "current-manual");
        var source = new FakeConnector
        {
            SystemCode = "A",
            Message = new SyncMessage(Guid.NewGuid(), "A", "A", "Sample", "1", ChangeOperation.Upsert, DateTimeOffset.UtcNow, incoming)
        };
        var destination = new FakeConnector { SystemCode = "C", EmitChange = false, Current = current, Events = events };
        var route = Route() with
        {
            FieldPolicies = new Dictionary<string, ConflictPolicy>(StringComparer.Ordinal)
            {
                ["Automatic"] = ConflictPolicy.ApplyIncomingAndNotify,
                ["Manual"] = ConflictPolicy.HoldAndNotify
            }
        };
        var store = new RecordingStore(route, Snapshot(route, baseline)) { Events = events };

        await Coordinator(source, destination, store).RunOnceAsync(10, CancellationToken.None);

        Assert.Equal(["save-conflict", "apply"], events);
        Assert.Equal("incoming-auto", destination.AppliedRequest!.Payload.Fields["Automatic"]!.GetValue<string>());
        Assert.Equal("current-manual", destination.AppliedRequest.Payload.Fields["Manual"]!.GetValue<string>());
        var snapshot = Assert.Single(store.SavedSnapshots);
        Assert.Equal("incoming-auto", snapshot.DestinationPayload!.Fields["Automatic"]!.GetValue<string>());
        Assert.Equal("current-manual", snapshot.DestinationPayload.Fields["Manual"]!.GetValue<string>());
        Assert.True(Assert.IsType<ConflictHistory>(store.SavedConflict).RequiresDecision);
    }

    [Fact]
    public async Task RunOnceUsesLatestDeleteResolvedFromOlderUpsertNotification()
    {
        var upsertMessageId = Guid.NewGuid();
        var deleteMessageId = Guid.NewGuid();
        var baseline = Payload("old");
        var source = new FakeConnector
        {
            SystemCode = "A",
            MessageId = upsertMessageId,
            Operation = ChangeOperation.Upsert,
            Message = new SyncMessage(deleteMessageId, "A", "A", "Sample", "1", ChangeOperation.Delete, DateTimeOffset.UtcNow, baseline)
        };
        var destination = new FakeConnector { SystemCode = "C", EmitChange = false, Current = baseline };
        var route = Route(
            new DeletionBehavior(DeletionMode.Physical),
            new DeletionBehavior(DeletionMode.Physical));
        var store = new RecordingStore(route, Snapshot(route, baseline));
        var coordinator = Coordinator(source, destination, store);

        await coordinator.RunOnceAsync(10, CancellationToken.None);

        Assert.Equal(ChangeOperation.Delete, destination.AppliedRequest!.Operation);
        Assert.Null(Assert.Single(store.SavedSnapshots).SourcePayload);
        Assert.Null(Assert.Single(store.SavedSnapshots).DestinationPayload);
        Assert.Equal(WebhookEventTypes.SyncDeleted, store.CompletedWebhook?.EventType);
    }

    [Fact]
    public async Task RunOnceDoesNotAdvanceBatchCheckpointWhenDeliveryFails()
    {
        var baseline = Payload("old");
        var latest = Payload("latest");
        var messageId = Guid.NewGuid();
        var source = new FakeConnector
        {
            SystemCode = "A",
            MessageId = messageId,
            Message = new SyncMessage(messageId, "A", "A", "Sample", "1", ChangeOperation.Upsert, DateTimeOffset.UtcNow, latest)
        };
        var destination = new FakeConnector
        {
            SystemCode = "C",
            EmitChange = false,
            Current = baseline,
            ApplyException = new InvalidOperationException("destination unavailable")
        };
        var route = Route();
        var store = new RecordingStore(route, Snapshot(route, baseline));
        var coordinator = Coordinator(source, destination, store);

        var processed = await coordinator.RunOnceAsync(10, CancellationToken.None);

        Assert.Equal(0, processed);
        Assert.Equal(0, store.Checkpoint);
        Assert.Equal(1, store.FailedDeliveries);
        Assert.Equal(WebhookEventTypes.SyncFailed, store.FailedWebhook?.EventType);
    }

    [Fact]
    public async Task RunOnceHoldsNonRetryableValueOverflowAndAdvancesCheckpoint()
    {
        var baseline = Payload("short");
        var latest = Payload("too long");
        var messageId = Guid.NewGuid();
        var source = new FakeConnector
        {
            SystemCode = "A",
            MessageId = messageId,
            Message = new SyncMessage(messageId, "A", "A", "Sample", "1", ChangeOperation.Upsert, DateTimeOffset.UtcNow, latest)
        };
        var destination = new FakeConnector { SystemCode = "C", EmitChange = false, Current = baseline };
        var route = Route() with
        {
            ValueMappings = new Dictionary<string, ColumnValueMappingDefinition>(StringComparer.Ordinal)
            {
                ["Value"] = new(
                    "Value", "target_value",
                    new ColumnValueContract("varchar", true, 100, null, null),
                    new ColumnValueContract("varchar", true, 5, null, null),
                    new ValueTransformInput(), new ValueTransformInput())
            }
        };
        var store = new RecordingStore(route, Snapshot(route, baseline));
        var coordinator = Coordinator(source, destination, store);

        var processed = await coordinator.RunOnceAsync(10, CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Equal(42, store.Checkpoint);
        Assert.Equal(1, store.HeldDeliveries);
        Assert.Equal(0, store.FailedDeliveries);
        Assert.Equal(0, destination.ApplyCalls);
        Assert.Equal(WebhookEventTypes.SyncFailed, store.FailedWebhook?.EventType);
    }

    [Fact]
    public async Task BidirectionalDeliveryNormalizesDestinationCodesBackToCanonicalValues()
    {
        var messageId = Guid.NewGuid();
        var incoming = Payload("done");
        var baseline = Payload("Received");
        var source = new FakeConnector
        {
            SystemCode = "C",
            MessageId = messageId,
            Message = new SyncMessage(messageId, "C", "A", "Sample", "1", ChangeOperation.Upsert, DateTimeOffset.UtcNow, incoming)
        };
        var destination = new FakeConnector { SystemCode = "A", EmitChange = false, Current = baseline };
        var reverse = new ValueTransformInput
        {
            RejectUnmappedValues = true,
            ValueMap = [new ValueMapEntryInput { SourceValue = "done", TargetValue = "Completed" }]
        };
        var route = (Route() with { Direction = SyncDirection.Bidirectional }) with
        {
            ValueMappings = new Dictionary<string, ColumnValueMappingDefinition>(StringComparer.Ordinal)
            {
                ["Value"] = new(
                    "Value", "job_status",
                    new ColumnValueContract("nvarchar", false, 40, null, null),
                    new ColumnValueContract("varchar", false, 20, null, null),
                    new ValueTransformInput(), reverse)
            }
        };
        var snapshot = new SyncSnapshot(route.Id, "A", "Sample", "1", baseline, baseline);
        var store = new RecordingStore(route, snapshot);
        var coordinator = Coordinator(source, destination, store);

        await coordinator.RunOnceAsync(10, CancellationToken.None);

        Assert.Equal("Completed", destination.AppliedRequest!.Payload.Fields["Value"]!.GetValue<string>());
    }

    [Fact]
    public async Task RunOnceDoesNotAdvanceCheckpointWhileInboxLeaseIsBusy()
    {
        var payload = Payload("latest");
        var messageId = Guid.NewGuid();
        var source = new FakeConnector
        {
            SystemCode = "A",
            MessageId = messageId,
            Message = new SyncMessage(messageId, "A", "A", "Sample", "1", ChangeOperation.Upsert, DateTimeOffset.UtcNow, payload)
        };
        var destination = new FakeConnector { SystemCode = "C", EmitChange = false, Current = payload };
        var route = Route();
        var store = new RecordingStore(route, Snapshot(route, payload))
        {
            AcquireResult = InboxAcquireResult.Busy
        };
        var coordinator = Coordinator(source, destination, store);

        var processed = await coordinator.RunOnceAsync(10, CancellationToken.None);

        Assert.Equal(0, processed);
        Assert.Equal(0, store.Checkpoint);
        Assert.Equal(0, destination.ApplyCalls);
    }

    [Fact]
    public async Task RunOnceDoesNotAdvanceCheckpointWhenMappingMaintenanceStartsBeforeInboxClaim()
    {
        var payload = Payload("latest");
        var messageId = Guid.NewGuid();
        var source = new FakeConnector
        {
            SystemCode = "A",
            MessageId = messageId,
            Message = new SyncMessage(messageId, "A", "A", "Sample", "1", ChangeOperation.Upsert, DateTimeOffset.UtcNow, payload)
        };
        var destination = new FakeConnector { SystemCode = "C", EmitChange = false, Current = payload };
        var route = Route();
        var store = new RecordingStore(route, Snapshot(route, payload))
        {
            AcquireResult = InboxAcquireResult.RoutePaused
        };
        var coordinator = Coordinator(source, destination, store);

        var processed = await coordinator.RunOnceAsync(10, CancellationToken.None);

        Assert.Equal(0, processed);
        Assert.Equal(0, store.Checkpoint);
        Assert.Equal(0, destination.ApplyCalls);
        Assert.Equal(0, store.FailedDeliveries);
    }

    [Fact]
    public async Task RunOnceLeavesSourceQueueUntouchedWhileSystemIsPaused()
    {
        var payload = Payload("latest");
        var messageId = Guid.NewGuid();
        var source = new FakeConnector
        {
            SystemCode = "A",
            MessageId = messageId,
            Message = new SyncMessage(messageId, "A", "A", "Sample", "1", ChangeOperation.Upsert, DateTimeOffset.UtcNow, payload)
        };
        var destination = new FakeConnector { SystemCode = "C", EmitChange = false, Current = payload };
        var route = Route();
        var store = new RecordingStore(route, Snapshot(route, payload)) { SystemPaused = true };
        var coordinator = Coordinator(source, destination, store);

        var processed = await coordinator.RunOnceAsync(10, CancellationToken.None);

        Assert.Equal(0, processed);
        Assert.Equal(0, source.ReadChangesCalls);
        Assert.Equal(0, store.Checkpoint);
        Assert.Equal(0, destination.ApplyCalls);
    }

    [Fact]
    public async Task RunOnceKeepsCheckpointWhileRouteIsPausedAndCatchesUpAfterResume()
    {
        var baseline = Payload("old");
        var latest = Payload("latest");
        var messageId = Guid.NewGuid();
        var source = new FakeConnector
        {
            SystemCode = "A",
            MessageId = messageId,
            Message = new SyncMessage(messageId, "A", "A", "Sample", "1", ChangeOperation.Upsert, DateTimeOffset.UtcNow, latest)
        };
        var destination = new FakeConnector { SystemCode = "C", EmitChange = false, Current = baseline };
        var route = Route();
        var store = new RecordingStore(route, Snapshot(route, baseline)) { RoutePaused = true };
        var coordinator = Coordinator(source, destination, store);

        Assert.Equal(0, await coordinator.RunOnceAsync(10, CancellationToken.None));
        Assert.Equal(0, store.Checkpoint);
        Assert.Equal(0, destination.ApplyCalls);

        store.RoutePaused = false;
        Assert.Equal(1, await coordinator.RunOnceAsync(10, CancellationToken.None));
        Assert.Equal(42, store.Checkpoint);
        Assert.Equal(1, destination.ApplyCalls);
        Assert.Equal("latest", destination.AppliedRequest!.Payload.Fields["Value"]!.GetValue<string>());
    }

    private static EntityPayload Payload(string value) =>
        new(new Dictionary<string, System.Text.Json.Nodes.JsonNode?>
        {
            ["Value"] = System.Text.Json.Nodes.JsonValue.Create(value)
        });

    private static SyncRouteDefinition Route(
        DeletionBehavior? sourceDeletion = null,
        DeletionBehavior? destinationDeletion = null) =>
        new(
            Guid.NewGuid(), "route", "A", "C", "Sample", SyncDirection.OneWay,
            sourceDeletion, destinationDeletion,
            ConflictScope.Field, ConflictPolicy.HoldAndNotify, true,
            new Dictionary<string, ConflictPolicy>());

    private static SyncSnapshot Snapshot(SyncRouteDefinition route, EntityPayload payload) =>
        new(route.Id, "C", "Sample", "1", payload, payload);

    private static SynchronizationCoordinator Coordinator(
        FakeConnector source,
        FakeConnector destination,
        ICoordinatorStore store) =>
        new(
            new ConnectorCatalog([source, destination]),
            store,
            new ConflictResolver(new NoOpConflictValueMerger()),
            TimeProvider.System,
            NullLogger<SynchronizationCoordinator>.Instance);

    private sealed class FakeConnector : ISyncConnector
    {
        public string SystemCode { get; init; } = "A";
        public bool AppliedMessage { get; init; }
        public bool EmitChange { get; init; } = true;
        public IReadOnlyList<ChangeQueueItem>? Changes { get; init; }
        public Guid MessageId { get; init; } = Guid.NewGuid();
        public ChangeOperation Operation { get; init; } = ChangeOperation.Upsert;
        public SyncMessage? Message { get; init; }
        public EntityPayload? Current { get; init; }
        public ApplyRequest? AppliedRequest { get; private set; }
        public Exception? ApplyException { get; init; }
        public Exception? ReadChangesException { get; init; }
        public List<string>? Events { get; init; }
        public int ApplyCalls { get; private set; }
        public int ReadMessageCalls { get; private set; }
        public int ReadChangesCalls { get; private set; }

        public Task<IReadOnlyList<ChangeQueueItem>> ReadChangesAsync(long afterQueueId, int take, CancellationToken cancellationToken)
        {
            ReadChangesCalls++;
            if (ReadChangesException is not null)
            {
                return Task.FromException<IReadOnlyList<ChangeQueueItem>>(ReadChangesException);
            }
            return Task.FromResult<IReadOnlyList<ChangeQueueItem>>(EmitChange
                ? (Changes ?? [new(42, MessageId, "Sample", "1", Operation, DateTimeOffset.UtcNow)])
                    .Where(x => x.QueueId > afterQueueId)
                    .OrderBy(x => x.QueueId)
                    .Take(take)
                    .ToArray()
                : []);
        }

        public Task<bool> WasAppliedMessageAsync(Guid messageId, CancellationToken cancellationToken) =>
            Task.FromResult(AppliedMessage);

        public Task<SyncMessage?> ReadLatestMessageAsync(ChangeQueueItem change, CancellationToken cancellationToken)
        {
            ReadMessageCalls++;
            return Task.FromResult<SyncMessage?>(Message ?? throw new NotSupportedException());
        }

        public Task<EntityPayload?> ReadCurrentAsync(string entityType, string entityId, CancellationToken cancellationToken) =>
            Task.FromResult(Current);

        public Task<ApplyResult> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
        {
            ApplyCalls++;
            Events?.Add("apply");
            if (ApplyException is not null)
            {
                return Task.FromException<ApplyResult>(ApplyException);
            }
            AppliedRequest = request;
            return Task.FromResult(new ApplyResult(ApplyStatus.Applied));
        }
    }

    private sealed class RecordingStore(SyncRouteDefinition route, SyncSnapshot? snapshot) : ICoordinatorStore
    {
        public long Checkpoint { get; private set; }
        public int FailedDeliveries { get; private set; }
        public int HeldDeliveries { get; private set; }
        public InboxAcquireResult AcquireResult { get; init; } = InboxAcquireResult.Acquired;
        public bool GlobalPaused { get; init; }
        public bool SystemPaused { get; init; }
        public bool RoutePaused { get; set; }
        public List<SyncSnapshot> SavedSnapshots { get; } = [];
        public ConflictHistory? SavedConflict { get; private set; }
        public WebhookEventNotification? CompletedWebhook { get; private set; }
        public WebhookEventNotification? FailedWebhook { get; private set; }
        public List<string>? Events { get; init; }

        public Task<bool> IsGloballyPausedAsync(CancellationToken cancellationToken) =>
            Task.FromResult(GlobalPaused);

        public Task<bool> IsSystemPausedAsync(string systemCode, CancellationToken cancellationToken) =>
            Task.FromResult(SystemPaused);

        public Task<long> GetCheckpointAsync(string systemCode, CancellationToken cancellationToken) =>
            Task.FromResult(Checkpoint);

        public Task AdvanceCheckpointAsync(string systemCode, long queueId, CancellationToken cancellationToken)
        {
            Checkpoint = queueId;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SyncRouteDefinition>> GetRoutesAsync(
            string sourceSystem,
            string originSystem,
            string entityType,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SyncRouteDefinition>>([
                route with { OperationallyPaused = RoutePaused }
            ]);

        public Task<InboxAcquireResult> TryBeginInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, CancellationToken cancellationToken) =>
            Task.FromResult(AcquireResult);

        public Task CompleteInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, InboxState state, WebhookEventNotification? webhookEvent, CancellationToken cancellationToken)
        {
            CompletedWebhook = webhookEvent;
            return Task.CompletedTask;
        }

        public Task FailInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, string errorDetails, WebhookEventNotification webhookEvent, CancellationToken cancellationToken)
        {
            FailedDeliveries++;
            FailedWebhook = webhookEvent;
            return Task.CompletedTask;
        }

        public Task HoldInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, string errorDetails, WebhookEventNotification webhookEvent, CancellationToken cancellationToken)
        {
            HeldDeliveries++;
            FailedWebhook = webhookEvent;
            return Task.CompletedTask;
        }

        public Task<SyncSnapshot?> GetSnapshotAsync(Guid routeId, string destinationSystem, string entityType, string entityId, CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);

        public Task SaveSnapshotAsync(SyncSnapshot saved, CancellationToken cancellationToken)
        {
            SavedSnapshots.Add(saved);
            snapshot = saved;
            return Task.CompletedTask;
        }

        public Task SaveConflictAsync(ConflictHistory conflict, WebhookEventNotification webhookEvent, CancellationToken cancellationToken)
        {
            Events?.Add("save-conflict");
            SavedConflict = conflict;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStore : ICoordinatorStore
    {
        public long Checkpoint { get; private set; }
        public Task<bool> IsGloballyPausedAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> IsSystemPausedAsync(string systemCode, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<long> GetCheckpointAsync(string systemCode, CancellationToken cancellationToken) => Task.FromResult(Checkpoint);
        public Task AdvanceCheckpointAsync(string systemCode, long queueId, CancellationToken cancellationToken)
        {
            Checkpoint = queueId;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<SyncRouteDefinition>> GetRoutesAsync(string sourceSystem, string originSystem, string entityType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InboxAcquireResult> TryBeginInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, InboxState state, WebhookEventNotification? webhookEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, string errorDetails, WebhookEventNotification webhookEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task HoldInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, string errorDetails, WebhookEventNotification webhookEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SyncSnapshot?> GetSnapshotAsync(Guid routeId, string destinationSystem, string entityType, string entityId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveSnapshotAsync(SyncSnapshot snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveConflictAsync(ConflictHistory conflict, WebhookEventNotification webhookEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class PerSystemCheckpointStore : ICoordinatorStore
    {
        private readonly Dictionary<string, long> checkpoints = new(StringComparer.OrdinalIgnoreCase);

        public long Checkpoint(string systemCode) => checkpoints.GetValueOrDefault(systemCode);
        public Task<bool> IsGloballyPausedAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> IsSystemPausedAsync(string systemCode, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<long> GetCheckpointAsync(string systemCode, CancellationToken cancellationToken) =>
            Task.FromResult(Checkpoint(systemCode));
        public Task AdvanceCheckpointAsync(string systemCode, long queueId, CancellationToken cancellationToken)
        {
            checkpoints[systemCode] = queueId;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<SyncRouteDefinition>> GetRoutesAsync(string sourceSystem, string originSystem, string entityType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InboxAcquireResult> TryBeginInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, InboxState state, WebhookEventNotification? webhookEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, string errorDetails, WebhookEventNotification webhookEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task HoldInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, string errorDetails, WebhookEventNotification webhookEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SyncSnapshot?> GetSnapshotAsync(Guid routeId, string destinationSystem, string entityType, string entityId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveSnapshotAsync(SyncSnapshot snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveConflictAsync(ConflictHistory conflict, WebhookEventNotification webhookEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingOperationalEventRecorder : IOperationalEventRecorder
    {
        public List<OperationalEventInput> Events { get; } = [];

        public Task RecordAsync(OperationalEventInput input, CancellationToken cancellationToken)
        {
            Events.Add(input);
            return Task.CompletedTask;
        }
    }

    private sealed class DeleteFlowStore(SyncRouteDefinition route, EntityPayload snapshot) : ICoordinatorStore
    {
        public Task<bool> IsGloballyPausedAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> IsSystemPausedAsync(string systemCode, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<long> GetCheckpointAsync(string systemCode, CancellationToken cancellationToken) => Task.FromResult(0L);
        public Task AdvanceCheckpointAsync(string systemCode, long queueId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<SyncRouteDefinition>> GetRoutesAsync(string sourceSystem, string originSystem, string entityType, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SyncRouteDefinition>>([route]);
        public Task<InboxAcquireResult> TryBeginInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, CancellationToken cancellationToken) => Task.FromResult(InboxAcquireResult.Acquired);
        public Task CompleteInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, InboxState state, WebhookEventNotification? webhookEvent, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FailInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, string errorDetails, WebhookEventNotification webhookEvent, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task HoldInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, string errorDetails, WebhookEventNotification webhookEvent, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<SyncSnapshot?> GetSnapshotAsync(Guid routeId, string destinationSystem, string entityType, string entityId, CancellationToken cancellationToken) =>
            Task.FromResult<SyncSnapshot?>(new SyncSnapshot(
                routeId,
                destinationSystem,
                entityType,
                entityId,
                snapshot,
                snapshot));
        public Task SaveSnapshotAsync(SyncSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveConflictAsync(ConflictHistory conflict, WebhookEventNotification webhookEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
