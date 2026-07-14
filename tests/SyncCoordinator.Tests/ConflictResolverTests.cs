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
            Snapshot(Payload(("sourceField", "old"), ("destinationField", "old"))),
            Payload(("sourceField", "from-source"), ("destinationField", "old")),
            Payload(("sourceField", "old"), ("destinationField", "from-destination")),
            route);

        Assert.Empty(result.Conflicts);
        Assert.Equal("from-source", result.AdoptedPayload.Fields["sourceField"]!.GetValue<string>());
        Assert.Equal("from-destination", result.AdoptedPayload.Fields["destinationField"]!.GetValue<string>());
        Assert.True(result.ShouldApply);
    }

    [Fact]
    public void ResolveUsesSeparateSourceAndDestinationObservations()
    {
        var resolver = new ConflictResolver(new NoOpConflictValueMerger());
        var baseline = new SyncSnapshot(
            Guid.NewGuid(),
            "C",
            "Sample",
            "1",
            Payload(("city", "Tokyo"), ("phone", "1")),
            Payload(("city", "Nagoya"), ("phone", "1")));

        var result = resolver.Resolve(
            "Sample",
            baseline,
            Payload(("city", "Tokyo"), ("phone", "2")),
            Payload(("city", "Nagoya"), ("phone", "1")),
            Route(ConflictScope.Field, ConflictPolicy.HoldAndNotify));

        Assert.Empty(result.Conflicts);
        Assert.Equal("Nagoya", result.AdoptedPayload.Fields["city"]!.GetValue<string>());
        Assert.Equal("2", result.AdoptedPayload.Fields["phone"]!.GetValue<string>());
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
            Snapshot(Payload(("value", "base"))),
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
            Snapshot(Payload(("conflict", "base"), ("safe", "base"))),
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
            Snapshot(Payload(("value", "base"))),
            Payload(("value", "incoming")),
            Payload(("value", "current")),
            Route(ConflictScope.Field, ConflictPolicy.MergeAndNotify));

        Assert.Equal("merged", result.AdoptedPayload.Fields["value"]!.GetValue<string>());
        Assert.Equal("Merged", Assert.Single(result.Conflicts).Resolution);
        Assert.False(result.IsHeld);
    }

    [Fact]
    public void DeleteIsAppliedWhenDestinationStillMatchesSnapshot()
    {
        var payload = Payload(("value", "base"));

        var result = ConflictResolver.ResolveDelete(
            Snapshot(payload),
            payload,
            Payload(("value", "base")),
            Route(ConflictScope.Field, ConflictPolicy.HoldAndNotify));

        Assert.True(result.ShouldApply);
        Assert.Empty(result.Conflicts);
        Assert.Empty(result.AdoptedPayload.Fields);
    }

    [Fact]
    public void DeleteUsesLastDestinationObservationAfterPreviousDivergence()
    {
        var sourceBeforeDelete = Payload(("value", "source-version"));
        var destinationKeptEarlier = Payload(("value", "destination-version"));
        var baseline = new SyncSnapshot(
            Guid.NewGuid(), "C", "Sample", "1", sourceBeforeDelete, destinationKeptEarlier);

        var result = ConflictResolver.ResolveDelete(
            baseline,
            sourceBeforeDelete,
            destinationKeptEarlier,
            Route(ConflictScope.Field, ConflictPolicy.HoldAndNotify));

        Assert.True(result.ShouldApply);
        Assert.False(result.AdoptedExists);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void ResolveCanKeepDestinationDeletionWithoutRecreatingEmptyRecord()
    {
        var baselinePayload = Payload(("value", "base"));
        var result = new ConflictResolver(new NoOpConflictValueMerger()).Resolve(
            "Sample",
            Snapshot(baselinePayload),
            Payload(("value", "changed-at-source")),
            null,
            Route(ConflictScope.Field, ConflictPolicy.KeepCurrentAndNotify));

        Assert.False(result.AdoptedExists);
        Assert.False(result.ShouldApply);
        Assert.Single(result.Conflicts);
    }

    [Theory]
    [InlineData(ConflictPolicy.HoldAndNotify, false, true)]
    [InlineData(ConflictPolicy.KeepCurrentAndNotify, false, false)]
    [InlineData(ConflictPolicy.ApplyIncomingAndNotify, true, false)]
    [InlineData(ConflictPolicy.MergeAndNotify, false, true)]
    public void DeleteConflictUsesConfiguredPolicy(
        ConflictPolicy policy,
        bool shouldDelete,
        bool expectedHeld)
    {
        var result = ConflictResolver.ResolveDelete(
            Snapshot(Payload(("value", "base"))),
            Payload(("value", "base")),
            Payload(("value", "changed-at-destination")),
            Route(ConflictScope.Field, policy));

        Assert.Equal(shouldDelete, result.ShouldApply);
        Assert.Equal(expectedHeld, result.IsHeld);
        Assert.Single(result.Conflicts);
    }

    private static EntityPayload Payload(params (string Name, string Value)[] fields) =>
        new(fields.ToDictionary(x => x.Name, x => (JsonNode?)JsonValue.Create(x.Value)));

    private static SyncSnapshot Snapshot(EntityPayload payload) =>
        new(Guid.NewGuid(), "C", "Sample", "1", payload, payload);

    private static SyncRouteDefinition Route(ConflictScope scope, ConflictPolicy policy) => new(
        Guid.NewGuid(),
        "route",
        "A",
        "C",
        "Sample",
        SyncDirection.OneWay,
        null,
        null,
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
