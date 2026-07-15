using Microsoft.EntityFrameworkCore;
using SyncCoordinator.Core;
using SyncCoordinator.Infrastructure.Persistence;

namespace SyncCoordinator.Infrastructure.Connectors;

internal sealed class RelationalMappingProvider(CoordinatorDbContext dbContext)
{
    public Task<bool> HasActiveMappingAsync(string systemCode, CancellationToken cancellationToken) =>
        dbContext.Routes.AsNoTracking().AnyAsync(route =>
            ((route.Enabled && route.DeploymentState == DatabaseDeploymentState.Prepared) ||
             route.MappingMaintenanceStartedAtUtc != null) &&
            route.SourceSystem.Enabled &&
            route.DestinationSystem.Enabled &&
            (route.SourceSystem.Code == systemCode || route.DestinationSystem.Code == systemCode),
            cancellationToken);

    public async Task<RelationalEntityMapping?> FindAsync(
        string systemCode,
        string entityType,
        CancellationToken cancellationToken) =>
        await FindAsync(systemCode, entityType, includeMappingMaintenance: true, cancellationToken);

    private async Task<RelationalEntityMapping?> FindAsync(
        string systemCode,
        string entityType,
        bool includeMappingMaintenance,
        CancellationToken cancellationToken)
    {
        var routes = await dbContext.Routes.AsNoTracking()
            .Include(route => route.SourceSystem)
            .Include(route => route.DestinationSystem)
            .Include(route => route.TableMapping).ThenInclude(mapping => mapping!.Columns)
            .Include(route => route.TableMapping).ThenInclude(mapping => mapping!.FixedValues)
            .Where(route =>
                ((route.Enabled && route.DeploymentState == DatabaseDeploymentState.Prepared) ||
                 includeMappingMaintenance && route.MappingMaintenanceStartedAtUtc != null) &&
                route.SourceSystem.Enabled &&
                route.DestinationSystem.Enabled &&
                route.EntityType == entityType &&
                (route.SourceSystem.Code == systemCode || route.DestinationSystem.Code == systemCode))
            .ToListAsync(cancellationToken);

        var candidates = routes
            .Select(route => CreateMapping(route, systemCode))
            .Where(mapping => mapping is not null)
            .Cast<RelationalEntityMapping>()
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var first = candidates[0];
        if (candidates.Skip(1).Any(candidate => !first.HasSamePhysicalContract(candidate)))
        {
            throw new InvalidOperationException(
                $"System '{systemCode}' の EntityType '{entityType}' に、異なる物理テーブルまたは列マッピングを持つ有効な同期ルールがあります。");
        }

        return first;
    }

    public async Task<RelationalEntityMapping> GetRequiredAsync(
        string systemCode,
        string entityType,
        CancellationToken cancellationToken) =>
        await FindAsync(systemCode, entityType, includeMappingMaintenance: false, cancellationToken) ??
        throw new InvalidOperationException(
            $"System '{systemCode}' の EntityType '{entityType}' に、有効かつ検証済みのテーブルマッピングがありません。");

    private static RelationalEntityMapping? CreateMapping(SyncRouteEntity route, string systemCode)
    {
        var tableMapping = route.TableMapping;
        if (tableMapping is null)
        {
            return null;
        }

        var isSource = string.Equals(route.SourceSystem.Code, systemCode, StringComparison.OrdinalIgnoreCase);
        var isDestination = string.Equals(route.DestinationSystem.Code, systemCode, StringComparison.OrdinalIgnoreCase);
        if (!isSource && !isDestination)
        {
            return null;
        }

        var columns = tableMapping.Columns
            .OrderBy(column => column.SourceColumn, StringComparer.Ordinal)
            .Select(column => new RelationalColumnBinding(
                column.SourceColumn,
                isSource ? column.SourceColumn : column.DestinationColumn,
                column.IsKey,
                isSource ? SourceContract(column) : DestinationContract(column)))
            .ToArray();
        if (columns.Length == 0 || !columns.Any(column => column.IsKey))
        {
            throw new InvalidOperationException(
                $"同期ルール '{route.Name}' のテーブルマッピングに列またはキー列がありません。");
        }

        var writeDirection = isSource ? MappingWriteDirection.Reverse : MappingWriteDirection.Forward;
        var fixedValues = tableMapping.FixedValues
            .Where(value => value.Direction == writeDirection)
            .OrderBy(value => value.TargetColumn, StringComparer.Ordinal)
            .Select(value => new RelationalFixedValue(
                value.TargetColumn,
                value.Value,
                TargetContract(value)))
            .ToArray();

        return new RelationalEntityMapping(
            route.EntityType,
            isSource ? tableMapping.SourceSchema : tableMapping.DestinationSchema,
            isSource ? tableMapping.SourceTable : tableMapping.DestinationTable,
            columns,
            fixedValues);
    }

    private static ColumnValueContract SourceContract(RouteColumnMappingEntity column) => new(
        column.SourceDataType,
        column.SourceIsNullable,
        column.SourceMaxLength,
        column.SourceNumericPrecision,
        column.SourceNumericScale);

    private static ColumnValueContract DestinationContract(RouteColumnMappingEntity column) => new(
        column.DestinationDataType,
        column.DestinationIsNullable,
        column.DestinationMaxLength,
        column.DestinationNumericPrecision,
        column.DestinationNumericScale);

    private static ColumnValueContract TargetContract(RouteFixedValueMappingEntity value) => new(
        value.TargetDataType,
        value.TargetIsNullable,
        value.TargetMaxLength,
        value.TargetNumericPrecision,
        value.TargetNumericScale);
}

internal sealed record RelationalColumnBinding(
    string PayloadField,
    string PhysicalColumn,
    bool IsKey,
    ColumnValueContract Contract);

internal sealed record RelationalFixedValue(
    string PhysicalColumn,
    string Value,
    ColumnValueContract Contract);

internal sealed record RelationalEntityMapping(
    string EntityType,
    string Schema,
    string Table,
    IReadOnlyList<RelationalColumnBinding> Columns,
    IReadOnlyList<RelationalFixedValue> FixedValues)
{
    public IReadOnlyList<RelationalColumnBinding> Keys => Columns.Where(column => column.IsKey).ToArray();

    public bool HasSamePhysicalContract(RelationalEntityMapping other) =>
        string.Equals(Schema, other.Schema, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Table, other.Table, StringComparison.OrdinalIgnoreCase) &&
        Columns.SequenceEqual(other.Columns) &&
        FixedValues.SequenceEqual(other.FixedValues);
}
