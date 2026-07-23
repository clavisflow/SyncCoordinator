using System.Text.Json.Nodes;
using SyncCoordinator.Contracts;
using SyncCoordinator.Core;

namespace SyncCoordinator.Tests;

public sealed class SyncPayloadTransformerTests
{
    [Fact]
    public void ForwardFlowIncludesForwardAndBidirectionalFields()
    {
        var route = Route();
        var payload = Payload();

        var result = SyncPayloadTransformer.NormalizeToCanonical(
            payload,
            route,
            route.DestinationSystem,
            MappingWriteDirection.Forward);

        Assert.True(result.Fields.ContainsKey("ReceptionName"));
        Assert.True(result.Fields.ContainsKey("WorkDescription"));
        Assert.False(result.Fields.ContainsKey("DestinationMemo"));
    }

    [Fact]
    public void ReverseFlowExcludesSourceOwnedFields()
    {
        var route = Route();
        var payload = Payload();

        var result = SyncPayloadTransformer.NormalizeToCanonical(
            payload,
            route,
            route.DestinationSystem,
            MappingWriteDirection.Reverse);

        Assert.False(result.Fields.ContainsKey("ReceptionName"));
        Assert.True(result.Fields.ContainsKey("WorkDescription"));
        Assert.True(result.Fields.ContainsKey("DestinationMemo"));
    }

    [Fact]
    public void SourceOwnedFieldOverwritesDestinationWithoutConflict()
    {
        var route = Route();
        var baseline = new SyncSnapshot(
            route.Id,
            route.DestinationSystem,
            route.EntityType,
            "1",
            Fields(("ReceptionName", "before")),
            Fields(("ReceptionName", "before")));

        var result = new ConflictResolver(new NoOpConflictValueMerger()).Resolve(
            route.EntityType,
            baseline,
            Fields(("ReceptionName", "source-change")),
            Fields(("ReceptionName", "destination-change")),
            route);

        Assert.Empty(result.Conflicts);
        Assert.Equal("source-change", result.AdoptedPayload.Fields["ReceptionName"]!.GetValue<string>());
    }

    private static EntityPayload Payload() => new(new Dictionary<string, JsonNode?>
    {
        ["ReceptionName"] = JsonValue.Create("受付A"),
        ["WorkDescription"] = JsonValue.Create("作業A"),
        ["DestinationMemo"] = JsonValue.Create("メモ")
    });

    private static EntityPayload Fields(params (string Name, string Value)[] fields) =>
        new(fields.ToDictionary(
            field => field.Name,
            field => (JsonNode?)JsonValue.Create(field.Value),
            StringComparer.Ordinal));

    private static SyncRouteDefinition Route() => new(
        Guid.NewGuid(),
        "作業依頼",
        "Business",
        "Destination",
        "WorkRequest",
        SyncDirection.Bidirectional,
        null,
        null,
        ConflictScope.Field,
        ConflictPolicy.HoldAndNotify,
        true,
        new Dictionary<string, ConflictPolicy>())
    {
        ValueMappings = new Dictionary<string, ColumnValueMappingDefinition>(StringComparer.Ordinal)
        {
            ["ReceptionName"] = Mapping("ReceptionName", SyncFieldDirection.Forward),
            ["WorkDescription"] = Mapping("WorkDescription", SyncFieldDirection.Bidirectional),
            ["DestinationMemo"] = Mapping("DestinationMemo", SyncFieldDirection.Reverse)
        }
    };

    private static ColumnValueMappingDefinition Mapping(string name, SyncFieldDirection direction) =>
        new(
            name,
            name,
            ColumnValueContract.Unknown,
            ColumnValueContract.Unknown,
            new ValueTransformInput(),
            new ValueTransformInput())
        {
            Direction = direction
        };
}
