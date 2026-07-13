using System.Text.Json.Nodes;
using SyncCoordinator.Contracts;

namespace SyncCoordinator.Core;

public interface ISyncConnector
{
    string SystemCode { get; }

    Task<IReadOnlyList<ChangeQueueItem>> ReadChangesAsync(
        long afterQueueId,
        int take,
        CancellationToken cancellationToken);

    Task<bool> WasAppliedMessageAsync(Guid messageId, CancellationToken cancellationToken);

    Task<SyncMessage> ReadMessageAsync(
        ChangeQueueItem change,
        CancellationToken cancellationToken);

    Task<EntityPayload?> ReadCurrentAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken);

    Task<ApplyResult> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken);
}

public interface IConnectorCatalog
{
    IReadOnlyCollection<ISyncConnector> All { get; }
    ISyncConnector GetRequired(string systemCode);
}

public interface ICoordinatorStore
{
    Task<long> GetCheckpointAsync(string systemCode, CancellationToken cancellationToken);
    Task AdvanceCheckpointAsync(string systemCode, long queueId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SyncRouteDefinition>> GetRoutesAsync(
        string sourceSystem,
        string entityType,
        CancellationToken cancellationToken);
    Task<bool> TryBeginInboxAsync(
        Guid sourceMessageId,
        Guid routeId,
        string destinationSystem,
        CancellationToken cancellationToken);
    Task CompleteInboxAsync(
        Guid sourceMessageId,
        Guid routeId,
        string destinationSystem,
        InboxState state,
        CancellationToken cancellationToken);
    Task FailInboxAsync(
        Guid sourceMessageId,
        Guid routeId,
        string destinationSystem,
        string errorDetails,
        CancellationToken cancellationToken);
    Task<SyncSnapshot?> GetSnapshotAsync(
        Guid routeId,
        string destinationSystem,
        string entityType,
        string entityId,
        CancellationToken cancellationToken);
    Task SaveSnapshotAsync(SyncSnapshot snapshot, CancellationToken cancellationToken);
    Task SaveConflictAsync(ConflictHistory conflict, CancellationToken cancellationToken);
}

public interface ICoordinatorReadService
{
    Task<DashboardSummary> GetSummaryAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<RouteListItem>> GetRoutesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ConflictListItem>> GetRecentConflictsAsync(int take, CancellationToken cancellationToken);
}

public interface ICoordinatorAdminService
{
    Task<IReadOnlyList<SystemListItem>> GetSystemsAsync(CancellationToken cancellationToken);
    Task<SystemConfigurationInput?> GetSystemAsync(Guid id, CancellationToken cancellationToken);
    Task<Guid> SaveSystemAsync(SystemConfigurationInput input, CancellationToken cancellationToken);
    Task<DatabaseConnectionInput?> GetDatabaseConnectionAsync(Guid systemId, CancellationToken cancellationToken);
    Task SaveDatabaseConnectionAsync(DatabaseConnectionInput input, CancellationToken cancellationToken);
    Task<RouteConfigurationInput?> GetRouteAsync(Guid id, CancellationToken cancellationToken);
    Task<Guid> SaveRouteAsync(RouteConfigurationInput input, CancellationToken cancellationToken);
    Task<IReadOnlyList<TableMappingListItem>> GetTableMappingsAsync(CancellationToken cancellationToken);
    Task<TableMappingInput?> GetTableMappingAsync(Guid routeId, string destinationSystem, CancellationToken cancellationToken);
    Task<Guid> SaveTableMappingAsync(TableMappingInput input, CancellationToken cancellationToken);
    Task<IReadOnlyList<InboxListItem>> GetRecentInboxAsync(int take, CancellationToken cancellationToken);
    Task<IReadOnlyList<CheckpointListItem>> GetCheckpointsAsync(CancellationToken cancellationToken);
    Task<ConflictDetails?> GetConflictAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConfigurationAuditListItem>> GetRecentConfigurationAuditsAsync(
        int take,
        CancellationToken cancellationToken);
}

public interface IDatabaseMetadataService
{
    Task<ConnectionTestResult> TestConnectionAsync(Guid systemId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DatabaseTableInfo>> GetTablesAsync(Guid systemId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DatabaseColumnInfo>> GetColumnsAsync(
        Guid systemId,
        string schema,
        string table,
        CancellationToken cancellationToken);
}

public interface IConflictValueMerger
{
    bool TryMerge(
        string entityType,
        string fieldName,
        JsonNode? baseValue,
        JsonNode? incomingValue,
        JsonNode? currentValue,
        out JsonNode? mergedValue);
}
