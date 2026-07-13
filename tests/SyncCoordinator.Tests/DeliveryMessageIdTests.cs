using SyncCoordinator.Core;

namespace SyncCoordinator.Tests;

public sealed class DeliveryMessageIdTests
{
    [Fact]
    public void CreateIsDeterministicPerSourceRouteAndDestination()
    {
        var source = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var route = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var first = DeliveryMessageId.Create(source, route, "C");
        var second = DeliveryMessageId.Create(source, route, "C");

        Assert.Equal(first, second);
        Assert.NotEqual(first, DeliveryMessageId.Create(source, route, "B"));
    }
}
