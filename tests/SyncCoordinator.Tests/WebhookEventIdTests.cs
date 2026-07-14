using SyncCoordinator.Core;
using SyncCoordinator.Infrastructure.Persistence;

namespace SyncCoordinator.Tests;

public sealed class WebhookEventIdTests
{
    [Fact]
    public void SameBusinessEventProducesStableId()
    {
        var deliveryId = Guid.NewGuid();

        var first = WebhookEventId.Create(WebhookEventTypes.SyncUpserted, deliveryId);
        var second = WebhookEventId.Create(WebhookEventTypes.SyncUpserted, deliveryId);

        Assert.Equal(first, second);
    }

    [Fact]
    public void DifferentEventTypesProduceDifferentIds()
    {
        var deliveryId = Guid.NewGuid();

        Assert.NotEqual(
            WebhookEventId.Create(WebhookEventTypes.SyncUpserted, deliveryId),
            WebhookEventId.Create(WebhookEventTypes.SyncFailed, deliveryId));
    }

    [Fact]
    public void SignatureCoversTimestampAndExactPayload()
    {
        var secret = Convert.ToBase64String("01234567890123456789012345678901"u8.ToArray());

        var signature = WebhookDeliveryService.CreateSignature(secret, "1720951200", "{\"value\":1}");

        Assert.StartsWith("v1=", signature, StringComparison.Ordinal);
        Assert.NotEqual(signature, WebhookDeliveryService.CreateSignature(secret, "1720951201", "{\"value\":1}"));
        Assert.NotEqual(signature, WebhookDeliveryService.CreateSignature(secret, "1720951200", "{\"value\":2}"));
    }
}
