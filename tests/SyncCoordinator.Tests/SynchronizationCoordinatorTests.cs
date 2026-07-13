using Microsoft.Extensions.Logging.Abstractions;
using SyncCoordinator.Contracts;
using SyncCoordinator.Core;

namespace SyncCoordinator.Tests;

public sealed class SynchronizationCoordinatorTests
{
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

    private sealed class FakeConnector : ISyncConnector
    {
        public string SystemCode => "A";
        public bool AppliedMessage { get; init; }
        public int ReadMessageCalls { get; private set; }

        public Task<IReadOnlyList<ChangeQueueItem>> ReadChangesAsync(long afterQueueId, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ChangeQueueItem>>([
                new(42, Guid.NewGuid(), "Sample", "1", ChangeOperation.Upsert, DateTimeOffset.UtcNow)
            ]);

        public Task<bool> WasAppliedMessageAsync(Guid messageId, CancellationToken cancellationToken) =>
            Task.FromResult(AppliedMessage);

        public Task<SyncMessage> ReadMessageAsync(ChangeQueueItem change, CancellationToken cancellationToken)
        {
            ReadMessageCalls++;
            throw new NotSupportedException();
        }

        public Task<EntityPayload?> ReadCurrentAsync(string entityType, string entityId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApplyResult> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeStore : ICoordinatorStore
    {
        public long Checkpoint { get; private set; }
        public Task<long> GetCheckpointAsync(string systemCode, CancellationToken cancellationToken) => Task.FromResult(Checkpoint);
        public Task AdvanceCheckpointAsync(string systemCode, long queueId, CancellationToken cancellationToken)
        {
            Checkpoint = queueId;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<SyncRouteDefinition>> GetRoutesAsync(string sourceSystem, string entityType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryBeginInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, InboxState state, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailInboxAsync(Guid sourceMessageId, Guid routeId, string destinationSystem, string errorDetails, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SyncSnapshot?> GetSnapshotAsync(Guid routeId, string destinationSystem, string entityType, string entityId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveSnapshotAsync(SyncSnapshot snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveConflictAsync(ConflictHistory conflict, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
