namespace SyncCoordinator.Core;

public sealed class ConnectorCatalog(IEnumerable<ISyncConnector> connectors) : IConnectorCatalog
{
    private readonly IReadOnlyDictionary<string, ISyncConnector> _connectors = connectors
        .ToDictionary(x => x.SystemCode, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ISyncConnector> All => _connectors.Values.ToArray();

    public ISyncConnector GetRequired(string systemCode) =>
        _connectors.TryGetValue(systemCode, out var connector)
            ? connector
            : throw new InvalidOperationException($"System '{systemCode}' の Connector が登録されていません。");
}
