namespace SyncCoordinator.Core;

public sealed class ConnectorCatalog(IEnumerable<ISyncConnector> connectors) : IConnectorCatalog
{
    private readonly IReadOnlyDictionary<string, ISyncConnector> _connectors = connectors
        .ToDictionary(x => x.SystemCode, StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyCollection<ISyncConnector>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<ISyncConnector>>(_connectors.Values.ToArray());

    public Task<ISyncConnector> GetRequiredAsync(string systemCode, CancellationToken cancellationToken) =>
        Task.FromResult(_connectors.TryGetValue(systemCode, out var connector)
            ? connector
            : throw new InvalidOperationException($"System '{systemCode}' の Connector が登録されていません。"));
}
