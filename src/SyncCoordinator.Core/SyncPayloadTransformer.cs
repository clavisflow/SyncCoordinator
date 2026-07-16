using System.Text.Json.Nodes;
using SyncCoordinator.Contracts;

namespace SyncCoordinator.Core;

public static class SyncPayloadTransformer
{
    public static EntityPayload NormalizeToCanonical(
        EntityPayload payload,
        SyncRouteDefinition route,
        string physicalSystem) =>
        TransformPayload(
            payload,
            route,
            mapping => string.Equals(physicalSystem, route.DestinationSystem, StringComparison.OrdinalIgnoreCase)
                ? (mapping.ReverseTransform, mapping.SourceContract, mapping.FieldName)
                : (new ValueTransformInput(), mapping.SourceContract, mapping.FieldName));

    public static EntityPayload TransformFromCanonical(
        EntityPayload payload,
        SyncRouteDefinition route,
        string physicalSystem) =>
        TransformPayload(
            payload,
            route,
            mapping => string.Equals(physicalSystem, route.DestinationSystem, StringComparison.OrdinalIgnoreCase)
                ? (mapping.ForwardTransform, mapping.DestinationContract, mapping.DestinationColumn)
                : (new ValueTransformInput(), mapping.SourceContract, mapping.FieldName));

    private static EntityPayload TransformPayload(
        EntityPayload payload,
        SyncRouteDefinition route,
        Func<ColumnValueMappingDefinition, (ValueTransformInput Transform, ColumnValueContract Contract, string TargetColumn)> select)
    {
        var fields = payload.Fields.ToDictionary(
            pair => pair.Key,
            pair => pair.Value?.DeepClone(),
            StringComparer.Ordinal);
        foreach (var mapping in route.ValueMappings.Values)
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
}
