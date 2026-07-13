using System.Text.Json.Nodes;
using SyncCoordinator.Contracts;
using SyncCoordinator.Core;

namespace SyncCoordinator.Tests;

public sealed class ConflictResolverTests
{
    [Fact]
    public void ResolveAutomaticallyMergesChangesToDifferentFields()
    {
        var resolver = new ConflictResolver(new NoOpConflictValueMerger());
        var route = Route(ConflictScope.Field, ConflictPolicy.HoldAndNotify);

        var result = resolver.Resolve(
            "Sample",
            Payload(("sourceField", "old"), ("destinationField", "old")),
            Payload(("sourceField", "from-source"), ("destinationField", "old")),
            Payload(("sourceField", "old"), ("destinationField", "from-destination")),
            route);

        Assert.Empty(result.Conflicts);
        Assert.Equal("from-source", result.AdoptedPayload.Fields["sourceField"]!.GetValue<string>());
        Assert.Equal("from-destination", result.AdoptedPayload.Fields["destinationField"]!.GetValue<string>());
        Assert.True(result.ShouldApply);
    }

    [Theory]
    [InlineData(ConflictPolicy.HoldAndNotify, "current", true)]
    [InlineData(ConflictPolicy.ApplyIncomingAndNotify, "incoming", false)]
    [InlineData(ConflictPolicy.KeepCurrentAndNotify, "current", false)]
    public void ResolveAppliesConfiguredFieldPolicy(
        ConflictPolicy policy,
        string expected,
        bool expectedHeld)
    {
        var resolver = new ConflictResolver(new NoOpConflictValueMerger());

        var result = resolver.Resolve(
            "Sample",
            Payload(("value", "base")),
            Payload(("value", "incoming")),
            Payload(("value", "current")),
            Route(ConflictScope.Field, policy));

        Assert.Single(result.Conflicts);
        Assert.Equal(expected, result.AdoptedPayload.Fields["value"]!.GetValue<string>());
        Assert.Equal(expectedHeld, result.IsHeld);
    }

    [Fact]
    public void ResolveRecordScopeHoldsWholeRecordWhenOneFieldConflicts()
    {
        var resolver = new ConflictResolver(new NoOpConflictValueMerger());

        var result = resolver.Resolve(
            "Sample",
            Payload(("conflict", "base"), ("safe", "base")),
            Payload(("conflict", "incoming"), ("safe", "incoming")),
            Payload(("conflict", "current"), ("safe", "base")),
            Route(ConflictScope.Record, ConflictPolicy.HoldAndNotify));

        Assert.True(result.IsHeld);
        Assert.False(result.ShouldApply);
        Assert.Equal("base", result.AdoptedPayload.Fields["safe"]!.GetValue<string>());
    }

    [Fact]
    public void ResolveUsesRegisteredMergerForMergePolicy()
    {
        var resolver = new ConflictResolver(new FixedMerger());

        var result = resolver.Resolve(
            "Sample",
            Payload(("value", "base")),
            Payload(("value", "incoming")),
            Payload(("value", "current")),
            Route(ConflictScope.Field, ConflictPolicy.MergeAndNotify));

        Assert.Equal("merged", result.AdoptedPayload.Fields["value"]!.GetValue<string>());
        Assert.Equal("Merged", Assert.Single(result.Conflicts).Resolution);
        Assert.False(result.IsHeld);
    }

    private static EntityPayload Payload(params (string Name, string Value)[] fields) =>
        new(fields.ToDictionary(x => x.Name, x => (JsonNode?)JsonValue.Create(x.Value)));

    private static SyncRouteDefinition Route(ConflictScope scope, ConflictPolicy policy) => new(
        Guid.NewGuid(),
        "route",
        "A",
        "Sample",
        DestinationMode.FixedSystem,
        "C",
        scope,
        policy,
        true,
        new Dictionary<string, ConflictPolicy>());

    private sealed class FixedMerger : IConflictValueMerger
    {
        public bool TryMerge(
            string entityType,
            string fieldName,
            JsonNode? baseValue,
            JsonNode? incomingValue,
            JsonNode? currentValue,
            out JsonNode? mergedValue)
        {
            mergedValue = JsonValue.Create("merged");
            return true;
        }
    }
}
