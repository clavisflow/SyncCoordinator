using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;
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
            .Include(route => route.TableMapping).ThenInclude(mapping => mapping!.RelatedTables)
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

    public async Task<RelationalEntityMapping> GetRequiredAsync(
        Guid routeId,
        string systemCode,
        CancellationToken cancellationToken)
    {
        var route = await RoutesQuery(includeMappingMaintenance: false)
            .SingleOrDefaultAsync(x => x.Id == routeId, cancellationToken) ??
            throw new InvalidOperationException($"同期ルール '{routeId}' に、有効かつ検証済みのテーブルマッピングがありません。");
        return CreateMapping(route, systemCode) ??
               throw new InvalidOperationException(
                   $"System '{systemCode}' は同期ルール '{route.Name}' の対象ではありません。");
    }

    public async Task<RelationalEntityMapping> ResolveRequiredAsync(
        string systemCode,
        string entityType,
        string physicalEntityId,
        CancellationToken cancellationToken)
    {
        var routes = await RoutesQuery(includeMappingMaintenance: false)
            .Where(route =>
                route.EntityType == entityType &&
                (route.SourceSystem.Code == systemCode || route.DestinationSystem.Code == systemCode))
            .ToListAsync(cancellationToken);
        var candidates = routes
            .Select(route => CreateMapping(route, systemCode))
            .Where(mapping => mapping is not null && mapping.MatchesPhysicalEntityId(physicalEntityId))
            .Cast<RelationalEntityMapping>()
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                $"System '{systemCode}' の EntityType '{entityType}' とキー '{physicalEntityId}' に一致する同期ルールがありません。");
        }

        var first = candidates[0];
        if (candidates.Skip(1).Any(candidate => !first.HasSamePhysicalContract(candidate)))
        {
            throw new InvalidOperationException(
                $"System '{systemCode}' の EntityType '{entityType}' とキー '{physicalEntityId}' に複数の同期ルールが一致しました。");
        }
        return first;
    }

    private IQueryable<SyncRouteEntity> RoutesQuery(bool includeMappingMaintenance) =>
        dbContext.Routes.AsNoTracking()
            .Include(route => route.SourceSystem)
            .Include(route => route.DestinationSystem)
            .Include(route => route.TableMapping).ThenInclude(mapping => mapping!.Columns)
            .Include(route => route.TableMapping).ThenInclude(mapping => mapping!.FixedValues)
            .Include(route => route.TableMapping).ThenInclude(mapping => mapping!.RelatedTables)
            .Where(route =>
                ((route.Enabled && route.DeploymentState == DatabaseDeploymentState.Prepared) ||
                 includeMappingMaintenance && route.MappingMaintenanceStartedAtUtc != null) &&
                route.SourceSystem.Enabled &&
                route.DestinationSystem.Enabled);

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
            .OrderBy(CanonicalFieldName, StringComparer.Ordinal)
            .Select(column => new RelationalColumnBinding(
                CanonicalFieldName(column),
                isSource ? column.SourceColumn : column.DestinationColumn,
                isSource && !string.IsNullOrWhiteSpace(column.SourceTableAlias)
                    ? column.SourceTableAlias
                    : null,
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
                value.IsKey,
                TargetContract(value)))
            .ToArray();

        return new RelationalEntityMapping(
            route.EntityType,
            isSource ? tableMapping.SourceSchema : tableMapping.DestinationSchema,
            isSource ? tableMapping.SourceTable : tableMapping.DestinationTable,
            columns,
            fixedValues,
            isSource
                ? tableMapping.RelatedTables.OrderBy(x => x.Alias).Select(x => new RelationalRelatedTable(
                    x.Schema, x.Table, x.Alias, x.JoinExpression, x.Usage,
                    x.ConditionExpression)).ToArray()
                : [],
            route.Id);
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

    private static string CanonicalFieldName(RouteColumnMappingEntity column) =>
        string.IsNullOrWhiteSpace(column.SourceTableAlias)
            ? column.SourceColumn
            : $"{column.SourceTableAlias}.{column.SourceColumn}";
}

internal sealed record RelationalColumnBinding(
    string PayloadField,
    string PhysicalColumn,
    string? TableAlias,
    bool IsKey,
    ColumnValueContract Contract);

internal sealed record RelationalFixedValue(
    string PhysicalColumn,
    string Value,
    bool IsKey,
    ColumnValueContract Contract);

internal sealed record RelationalKeyBinding(
    string EntityIdField,
    string PhysicalColumn,
    string? PayloadField,
    JsonNode? FixedValue,
    ColumnValueContract Contract)
{
    public bool IsFixed => PayloadField is null;
}

internal sealed record RelationalRelatedTable(
    string Schema,
    string Table,
    string Alias,
    string JoinExpression,
    RelatedTableUsage Usage,
    string? ConditionExpression);

internal sealed record RelationalEntityMapping(
    string EntityType,
    string Schema,
    string Table,
    IReadOnlyList<RelationalColumnBinding> Columns,
    IReadOnlyList<RelationalFixedValue> FixedValues,
    IReadOnlyList<RelationalRelatedTable> RelatedTables,
    Guid RouteId = default)
{
    public IReadOnlyList<RelationalColumnBinding> ColumnKeys =>
        Columns.Where(column => column.IsKey).ToArray();

    public IReadOnlyList<RelationalKeyBinding> Keys =>
        ColumnKeys
            .Select(column => new RelationalKeyBinding(
                column.PayloadField,
                column.PhysicalColumn,
                column.PayloadField,
                null,
                column.Contract))
            .Concat(FixedValues.Where(value => value.IsKey).Select(value => new RelationalKeyBinding(
                $"@fixed:{value.PhysicalColumn}",
                value.PhysicalColumn,
                null,
                ValueTransformEngine.Transform(
                    JsonValue.Create(value.Value),
                    new ValueTransformInput(),
                    value.Contract,
                    value.PhysicalColumn,
                    value.PhysicalColumn),
                value.Contract)))
            .ToArray();

    public bool MatchesPhysicalEntityId(string entityId)
    {
        if (FixedValues.All(value => !value.IsKey))
        {
            return true;
        }

        var values = ParseEntityId(Keys, entityId);
        return Keys.Where(key => key.IsFixed).All(key =>
            values.TryGetPropertyValue(key.EntityIdField, out var actual) &&
            JsonNode.DeepEquals(actual, key.FixedValue));
    }

    public string ToCanonicalEntityId(string physicalEntityId)
    {
        var physical = ParseEntityId(Keys, physicalEntityId);
        if (ColumnKeys.Count == 1)
        {
            return JsonScalarText(physical[ColumnKeys[0].PayloadField]);
        }

        var result = new JsonObject();
        foreach (var key in ColumnKeys)
        {
            result[key.PayloadField] = physical[key.PayloadField]?.DeepClone();
        }
        return result.ToJsonString();
    }

    public static JsonObject ParseEntityId(IReadOnlyList<RelationalKeyBinding> keys, string entityId)
    {
        if (keys.Count == 1)
        {
            return new JsonObject { [keys[0].EntityIdField] = JsonValue.Create(entityId) };
        }
        return JsonNode.Parse(entityId) as JsonObject ??
               throw new InvalidOperationException($"複合キーのEntityIdがJSON objectではありません: {entityId}");
    }

    private static string JsonScalarText(JsonNode? value)
    {
        if (value is null)
        {
            return string.Empty;
        }
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            return text;
        }
        return value.ToJsonString();
    }

    public bool HasSamePhysicalContract(RelationalEntityMapping other) =>
        string.Equals(Schema, other.Schema, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Table, other.Table, StringComparison.OrdinalIgnoreCase) &&
        Columns.SequenceEqual(other.Columns) &&
        FixedValues.SequenceEqual(other.FixedValues) &&
        RelatedTables.SequenceEqual(other.RelatedTables);
}
