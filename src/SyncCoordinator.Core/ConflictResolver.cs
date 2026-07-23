using System.Text.Json.Nodes;
using SyncCoordinator.Contracts;

namespace SyncCoordinator.Core;

public sealed class ConflictResolver(IConflictValueMerger valueMerger)
{
    public ConflictResolution Resolve(
        string entityType,
        SyncSnapshot? baseline,
        EntityPayload incoming,
        EntityPayload? current,
        SyncRouteDefinition route)
    {
        var sourceBaseFields = baseline?.SourcePayload?.Fields ?? EmptyFields;
        var destinationBaseFields = baseline?.DestinationPayload?.Fields ?? EmptyFields;
        var currentFields = current?.Fields ?? EmptyFields;
        var fieldNames = sourceBaseFields.Keys
            .Concat(destinationBaseFields.Keys)
            .Concat(incoming.Fields.Keys)
            .Concat(currentFields.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        var adopted = Clone(currentFields);
        var adoptedExists = current is not null;
        var conflicts = new List<FieldConflict>();

        foreach (var fieldName in fieldNames)
        {
            var sourceBaseSlot = ReadSlot(sourceBaseFields, fieldName);
            var destinationBaseSlot = ReadSlot(destinationBaseFields, fieldName);
            var incomingSlot = ReadSlot(incoming.Fields, fieldName);
            var currentSlot = ReadSlot(currentFields, fieldName);

            // 初回同期では incoming が管理する項目だけを反映し、宛先固有項目は保持する。
            var incomingChanged = baseline is null
                ? incomingSlot.Present
                : !SlotEquals(incomingSlot, sourceBaseSlot);
            var currentChanged = baseline is not null && !SlotEquals(currentSlot, destinationBaseSlot);
            var isConflict = incomingChanged && currentChanged && !SlotEquals(incomingSlot, currentSlot);

            // 片方向項目は、その方向の送信側が常に正となる。
            // 宛先側で値が変更されていてもコンフリクトにはせず、受信値で戻す。
            if (route.ValueMappings.TryGetValue(fieldName, out var directionalMapping) &&
                directionalMapping.Direction != SyncFieldDirection.Bidirectional)
            {
                if (incomingChanged)
                {
                    SetSlot(adopted, fieldName, incomingSlot);
                    adoptedExists = true;
                }
                continue;
            }

            if (!isConflict)
            {
                if (incomingChanged)
                {
                    SetSlot(adopted, fieldName, incomingSlot);
                    adoptedExists = true;
                }
                continue;
            }

            var policy = route.FieldPolicies.GetValueOrDefault(fieldName, route.DefaultConflictPolicy);
            var selected = currentSlot;
            var resolution = "KeptCurrent";

            switch (policy)
            {
                case ConflictPolicy.ApplyIncomingAndNotify:
                    selected = incomingSlot;
                    adoptedExists = true;
                    resolution = "AppliedIncoming";
                    break;
                case ConflictPolicy.KeepCurrentAndNotify:
                    break;
                case ConflictPolicy.MergeAndNotify:
                    if (valueMerger.TryMerge(
                        entityType,
                        fieldName,
                        destinationBaseSlot.Value,
                        incomingSlot.Value,
                        currentSlot.Value,
                        out var merged))
                    {
                        selected = new ValueSlot(true, Clone(merged));
                        adoptedExists = true;
                        resolution = "Merged";
                    }
                    else
                    {
                        resolution = "MergeUnavailableHeld";
                    }
                    break;
                case ConflictPolicy.HoldAndNotify:
                    resolution = "Held";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(route), policy, "Unsupported conflict policy.");
            }

            SetSlot(adopted, fieldName, selected);
            conflicts.Add(new FieldConflict(
                fieldName,
                Clone(destinationBaseSlot.Value),
                Clone(incomingSlot.Value),
                Clone(currentSlot.Value),
                Clone(selected.Value),
                policy,
                resolution));
        }

        var hasHeldConflict = conflicts.Any(x =>
            x.Policy == ConflictPolicy.HoldAndNotify || x.Resolution == "MergeUnavailableHeld");
        var recordHeld = route.ConflictScope == ConflictScope.Record && hasHeldConflict;

        if (recordHeld)
        {
            adopted = Clone(currentFields);
            adoptedExists = current is not null;
            conflicts = conflicts.Select(x => x with
            {
                AdoptedValue = Clone(ReadSlot(currentFields, x.FieldName).Value),
                Resolution = "RecordHeld"
            }).ToList();
        }

        var adoptedPayload = new EntityPayload(adopted);
        var shouldApply = !recordHeld && !PayloadEquals(adoptedPayload, adoptedExists, current);
        return new ConflictResolution(adoptedPayload, adoptedExists, conflicts, shouldApply, hasHeldConflict);
    }

    public static ConflictResolution ResolveDelete(
        SyncSnapshot? baseline,
        EntityPayload deleted,
        EntityPayload? current,
        SyncRouteDefinition route)
    {
        if (current is null)
        {
            return new ConflictResolution(EntityPayload.Empty, false, [], false, false);
        }

        var referenceFields = baseline is null
            ? deleted.Fields
            : baseline.DestinationPayload?.Fields ?? EmptyFields;
        var changedFields = referenceFields.Keys
            .Concat(current.Fields.Keys)
            .Distinct(StringComparer.Ordinal)
            .Where(fieldName => !SlotEquals(
                ReadSlot(referenceFields, fieldName),
                ReadSlot(current.Fields, fieldName)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (changedFields.Length == 0)
        {
            return new ConflictResolution(EntityPayload.Empty, false, [], true, false);
        }

        var conflicts = new List<FieldConflict>();
        var deleteAllowed = true;
        var held = false;
        foreach (var fieldName in changedFields)
        {
            var policy = route.FieldPolicies.GetValueOrDefault(fieldName, route.DefaultConflictPolicy);
            var currentValue = Clone(ReadSlot(current.Fields, fieldName).Value);
            var resolution = policy switch
            {
                ConflictPolicy.ApplyIncomingAndNotify => "AppliedIncomingDelete",
                ConflictPolicy.KeepCurrentAndNotify => "DeleteKeptCurrent",
                ConflictPolicy.HoldAndNotify => "DeleteHeld",
                ConflictPolicy.MergeAndNotify => "DeleteMergeUnavailableHeld",
                _ => throw new ArgumentOutOfRangeException(nameof(route), policy, "Unsupported conflict policy.")
            };
            if (policy != ConflictPolicy.ApplyIncomingAndNotify)
            {
                deleteAllowed = false;
            }
            if (policy is ConflictPolicy.HoldAndNotify or ConflictPolicy.MergeAndNotify)
            {
                held = true;
            }
            conflicts.Add(new FieldConflict(
                fieldName,
                Clone(ReadSlot(referenceFields, fieldName).Value),
                null,
                currentValue,
                policy == ConflictPolicy.ApplyIncomingAndNotify ? null : currentValue,
                policy,
                resolution));
        }

        return new ConflictResolution(
            deleteAllowed ? EntityPayload.Empty : new EntityPayload(Clone(current.Fields)),
            !deleteAllowed,
            conflicts,
            deleteAllowed,
            held);
    }

    private static readonly IReadOnlyDictionary<string, JsonNode?> EmptyFields =
        new Dictionary<string, JsonNode?>();

    private static Dictionary<string, JsonNode?> Clone(IReadOnlyDictionary<string, JsonNode?> fields) =>
        fields.ToDictionary(x => x.Key, x => Clone(x.Value), StringComparer.Ordinal);

    private static JsonNode? Clone(JsonNode? value) => value?.DeepClone();

    private static ValueSlot ReadSlot(IReadOnlyDictionary<string, JsonNode?> fields, string name) =>
        fields.TryGetValue(name, out var value)
            ? new ValueSlot(true, value)
            : new ValueSlot(false, null);

    private static void SetSlot(Dictionary<string, JsonNode?> fields, string name, ValueSlot slot)
    {
        if (slot.Present)
        {
            fields[name] = Clone(slot.Value);
        }
        else
        {
            fields.Remove(name);
        }
    }

    private static bool SlotEquals(ValueSlot left, ValueSlot right) =>
        left.Present == right.Present && (!left.Present || JsonNode.DeepEquals(left.Value, right.Value));

    private static bool PayloadEquals(EntityPayload left, bool leftExists, EntityPayload? right)
    {
        if (!leftExists)
        {
            return right is null;
        }
        if (right is null || left.Fields.Count != right.Fields.Count)
        {
            return false;
        }

        return left.Fields.All(x => right.Fields.TryGetValue(x.Key, out var other) &&
                                    JsonNode.DeepEquals(x.Value, other));
    }

    private readonly record struct ValueSlot(bool Present, JsonNode? Value);
}

/// <summary>
/// 業務上のマージ規則が登録されていない値を推測して結合しない、安全側の既定実装。
/// </summary>
public sealed class NoOpConflictValueMerger : IConflictValueMerger
{
    public bool TryMerge(
        string entityType,
        string fieldName,
        JsonNode? baseValue,
        JsonNode? incomingValue,
        JsonNode? currentValue,
        out JsonNode? mergedValue)
    {
        mergedValue = null;
        return false;
    }
}
