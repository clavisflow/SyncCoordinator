using System.Text.Json.Nodes;
using SyncCoordinator.Contracts;

namespace SyncCoordinator.Core;

public static class SyncPayloadTransformer
{
    public static EntityPayload NormalizeToCanonical(
        EntityPayload payload,
        SyncRouteDefinition route,
        string physicalSystem,
        MappingWriteDirection? flowDirection = null) =>
        TransformPayload(
            payload,
            route,
            flowDirection ?? ResolveIncomingDirection(route, physicalSystem),
            mapping => string.Equals(physicalSystem, route.DestinationSystem, StringComparison.OrdinalIgnoreCase)
                ? (mapping.ReverseTransform, mapping.SourceContract, mapping.FieldName)
                : (new ValueTransformInput(), mapping.SourceContract, mapping.FieldName));

    public static EntityPayload TransformFromCanonical(
        EntityPayload payload,
        SyncRouteDefinition route,
        string physicalSystem,
        MappingWriteDirection? flowDirection = null) =>
        TransformPayload(
            payload,
            route,
            flowDirection ?? ResolveTargetDirection(route, physicalSystem),
            mapping => string.Equals(physicalSystem, route.DestinationSystem, StringComparison.OrdinalIgnoreCase)
                ? (mapping.ForwardTransform, mapping.DestinationContract, mapping.DestinationColumn)
                : (new ValueTransformInput(), mapping.SourceContract, mapping.FieldName));

    public static EntityPayload FilterCanonical(
        EntityPayload payload,
        SyncRouteDefinition route,
        MappingWriteDirection flowDirection) =>
        new(payload.Fields
            .Where(pair => !route.ValueMappings.TryGetValue(pair.Key, out var mapping) || mapping.Allows(flowDirection))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.DeepClone(),
                StringComparer.Ordinal));

    private static EntityPayload TransformPayload(
        EntityPayload payload,
        SyncRouteDefinition route,
        MappingWriteDirection flowDirection,
        Func<ColumnValueMappingDefinition, (ValueTransformInput Transform, ColumnValueContract Contract, string TargetColumn)> select)
    {
        var fields = payload.Fields
            .Where(pair => !route.ValueMappings.TryGetValue(pair.Key, out var mapping) || mapping.Allows(flowDirection))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.DeepClone(),
                StringComparer.Ordinal);
        foreach (var mapping in route.ValueMappings.Values.Where(mapping => mapping.Allows(flowDirection)))
        {
            if (!payload.Fields.TryGetValue(mapping.FieldName, out var value))
            {
                continue;
            }

            var target = select(mapping);
            fields[mapping.FieldName] = ValueTransformEngine.Transform(
                value,
                target.Transform,
                target.Contract,
                mapping.FieldName,
                target.TargetColumn);
        }
        return new EntityPayload(fields);
    }

    private static MappingWriteDirection ResolveIncomingDirection(
        SyncRouteDefinition route,
        string physicalSystem) =>
        string.Equals(physicalSystem, route.SourceSystem, StringComparison.OrdinalIgnoreCase)
            ? MappingWriteDirection.Forward
            : string.Equals(physicalSystem, route.DestinationSystem, StringComparison.OrdinalIgnoreCase)
                ? MappingWriteDirection.Reverse
                : throw new InvalidOperationException($"システム '{physicalSystem}' は同期ルール '{route.Name}' に含まれていません。");

    private static MappingWriteDirection ResolveTargetDirection(
        SyncRouteDefinition route,
        string physicalSystem) =>
        string.Equals(physicalSystem, route.DestinationSystem, StringComparison.OrdinalIgnoreCase)
            ? MappingWriteDirection.Forward
            : string.Equals(physicalSystem, route.SourceSystem, StringComparison.OrdinalIgnoreCase)
                ? MappingWriteDirection.Reverse
                : throw new InvalidOperationException($"システム '{physicalSystem}' は同期ルール '{route.Name}' に含まれていません。");
}
