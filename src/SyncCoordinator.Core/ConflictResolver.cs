using System.Text.Json.Nodes;
using SyncCoordinator.Contracts;

namespace SyncCoordinator.Core;

public sealed class ConflictResolver(IConflictValueMerger valueMerger)
{
    public ConflictResolution Resolve(
        string entityType,
        EntityPayload? baseline,
        EntityPayload incoming,
        EntityPayload? current,
        SyncRouteDefinition route)
    {
        var baseFields = baseline?.Fields ?? EmptyFields;
        var currentFields = current?.Fields ?? EmptyFields;
        var fieldNames = baseFields.Keys
            .Concat(incoming.Fields.Keys)
            .Concat(currentFields.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        var adopted = Clone(currentFields);
        var conflicts = new List<FieldConflict>();

        foreach (var fieldName in fieldNames)
        {
            var baseSlot = ReadSlot(baseFields, fieldName);
            var incomingSlot = ReadSlot(incoming.Fields, fieldName);
            var currentSlot = ReadSlot(currentFields, fieldName);

            // 初回同期では incoming が管理する項目だけを反映し、宛先固有項目は保持する。
            var incomingChanged = baseline is null
                ? incomingSlot.Present
                : !SlotEquals(incomingSlot, baseSlot);
            var currentChanged = baseline is not null && !SlotEquals(currentSlot, baseSlot);
            var isConflict = incomingChanged && currentChanged && !SlotEquals(incomingSlot, currentSlot);

            if (!isConflict)
            {
                if (incomingChanged)
                {
                    SetSlot(adopted, fieldName, incomingSlot);
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
                    resolution = "AppliedIncoming";
                    break;
                case ConflictPolicy.KeepCurrentAndNotify:
                    break;
                case ConflictPolicy.MergeAndNotify:
                    if (valueMerger.TryMerge(
                        entityType,
                        fieldName,
                        baseSlot.Value,
                        incomingSlot.Value,
                        currentSlot.Value,
                        out var merged))
                    {
                        selected = new ValueSlot(true, Clone(merged));
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
                Clone(baseSlot.Value),
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
            conflicts = conflicts.Select(x => x with
            {
                AdoptedValue = Clone(ReadSlot(currentFields, x.FieldName).Value),
                Resolution = "RecordHeld"
            }).ToList();
        }

        var adoptedPayload = new EntityPayload(adopted);
        var shouldApply = !recordHeld && !PayloadEquals(adoptedPayload, current);
        return new ConflictResolution(adoptedPayload, conflicts, shouldApply, hasHeldConflict);
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

    private static bool PayloadEquals(EntityPayload left, EntityPayload? right)
    {
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
