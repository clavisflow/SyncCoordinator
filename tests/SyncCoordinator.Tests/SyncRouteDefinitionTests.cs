using SyncCoordinator.Core;

namespace SyncCoordinator.Tests;

public sealed class SyncRouteDefinitionTests
{
    [Fact]
    public void ForwardChangeIsSentToConfiguredDestination()
    {
        var rule = Rule("A", "C", SyncDirection.OneWay);

        Assert.Equal("C", rule.ResolveDestination("A", "A"));
    }

    [Fact]
    public void BidirectionalChangeReturnsOnlyToItsOriginalSource()
    {
        var rule = Rule("A", "C", SyncDirection.Bidirectional);

        Assert.Equal("A", rule.ResolveDestination("C", "A"));
        Assert.Null(rule.ResolveDestination("C", "B"));
        Assert.Null(rule.ResolveDestination("C", "C"));
    }

    [Fact]
    public void OneWayRuleDoesNotReturnDestinationChanges()
    {
        var rule = Rule("A", "C", SyncDirection.OneWay);

        Assert.Null(rule.ResolveDestination("C", "A"));
    }

    private static SyncRouteDefinition Rule(string source, string destination, SyncDirection direction) => new(
        Guid.NewGuid(),
        "rule",
        source,
        destination,
        "Sample",
        direction,
        null,
        null,
        ConflictScope.Field,
        ConflictPolicy.HoldAndNotify,
        true,
        new Dictionary<string, ConflictPolicy>());
}
