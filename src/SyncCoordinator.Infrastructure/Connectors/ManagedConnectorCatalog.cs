using Microsoft.EntityFrameworkCore;
using SyncCoordinator.Core;
using SyncCoordinator.Infrastructure.Persistence;

namespace SyncCoordinator.Infrastructure.Connectors;

internal sealed record ManagedConnectorDefinition(
    string SystemCode,
    string Provider,
    bool Enabled,
    string? ProtectedConnectionString);

internal interface IManagedConnectorDefinitionSource
{
    Task<IReadOnlyList<ManagedConnectorDefinition>> GetAllAsync(
        CancellationToken cancellationToken);
}

internal sealed class EfManagedConnectorDefinitionSource(CoordinatorDbContext dbContext)
    : IManagedConnectorDefinitionSource
{
    public async Task<IReadOnlyList<ManagedConnectorDefinition>> GetAllAsync(
        CancellationToken cancellationToken) =>
        await dbContext.Systems
            .AsNoTracking()
            .OrderBy(system => system.Code)
            .Select(system => new ManagedConnectorDefinition(
                system.Code,
                system.Provider,
                system.Enabled,
                system.ProtectedConnectionString))
            .ToListAsync(cancellationToken);
}

internal interface IManagedConnectorFactory
{
    ISyncConnector Create(ManagedConnectorDefinition definition);
}

internal sealed class ManagedConnectorFactory(
    ProtectedConnectionStringService connectionProtector,
    RelationalMappingProvider mappings) : IManagedConnectorFactory
{
    public ISyncConnector Create(ManagedConnectorDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.ProtectedConnectionString))
        {
            throw new InvalidOperationException(
                $"System '{definition.SystemCode}' の接続情報が未設定です。");
        }

        if (!Enum.TryParse<RelationalProvider>(
                definition.Provider,
                ignoreCase: true,
                out var provider) ||
            !Enum.IsDefined(provider))
        {
            throw new InvalidOperationException(
                $"System '{definition.SystemCode}' のProvider '{definition.Provider}' は未対応です。");
        }

        return new MappedRelationalConnector(
            definition.SystemCode,
            provider,
            connectionProtector.Unprotect(definition.ProtectedConnectionString),
            mappings);
    }
}

/// <summary>
/// scopeの最初の参照時に、管理DBを正本として有効なConnectorを構築する。
/// Workerは周期ごとにscopeを作るため、管理画面の変更は再起動せず次周期から反映される。
/// </summary>
internal sealed class ManagedConnectorCatalog(
    IManagedConnectorDefinitionSource definitions,
    IManagedConnectorFactory factory) : IConnectorCatalog
{
    private readonly object loadLock = new();
    private Task<IReadOnlyDictionary<string, ISyncConnector>>? loadTask;

    public async Task<IReadOnlyCollection<ISyncConnector>> GetAllAsync(
        CancellationToken cancellationToken) =>
        (await LoadAsync(cancellationToken)).Values.ToArray();

    public async Task<ISyncConnector> GetRequiredAsync(
        string systemCode,
        CancellationToken cancellationToken)
    {
        var connectors = await LoadAsync(cancellationToken);
        return connectors.TryGetValue(systemCode, out var connector)
            ? connector
            : throw new InvalidOperationException(
                $"System '{systemCode}' は無効か、接続情報が未設定です。");
    }

    private Task<IReadOnlyDictionary<string, ISyncConnector>> LoadAsync(
        CancellationToken cancellationToken)
    {
        lock (loadLock)
        {
            return loadTask ??= LoadCoreAsync(cancellationToken);
        }
    }

    private async Task<IReadOnlyDictionary<string, ISyncConnector>> LoadCoreAsync(
        CancellationToken cancellationToken)
    {
        var configuredSystems = await definitions.GetAllAsync(cancellationToken);
        return configuredSystems
            .Where(definition =>
                definition.Enabled &&
                !string.IsNullOrWhiteSpace(definition.ProtectedConnectionString))
            .Select(factory.Create)
            .ToDictionary(
                connector => connector.SystemCode,
                StringComparer.OrdinalIgnoreCase);
    }
}
