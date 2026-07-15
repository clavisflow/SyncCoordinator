using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SyncCoordinator.Core;

namespace SyncCoordinator.Infrastructure.Persistence;

public sealed class ProtectedWebhookSecretService(IDataProtectionProvider provider)
{
    private readonly IDataProtector protector = provider.CreateProtector("SyncCoordinator.WebhookSecret.v1");

    public string Protect(string secret) => protector.Protect(secret);
    public string Unprotect(string protectedSecret) => protector.Unprotect(protectedSecret);
}

public sealed class WebhookOutboxWriter(CoordinatorDbContext dbContext)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task AddAsync(WebhookEventNotification notification, CancellationToken cancellationToken)
    {
        var endpoints = await dbContext.WebhookEndpoints
            .Where(x => x.Enabled)
            .ToListAsync(cancellationToken);
        var selected = endpoints.Where(x => ParseEventTypes(x.EventTypesJson).Contains(notification.EventType, StringComparer.Ordinal)).ToArray();
        await AddAsync(notification, selected, cancellationToken);
    }

    public async Task AddForEndpointAsync(
        WebhookEventNotification notification,
        Guid endpointId,
        CancellationToken cancellationToken)
    {
        var endpoint = await dbContext.WebhookEndpoints.SingleOrDefaultAsync(x => x.Id == endpointId, cancellationToken) ??
                       throw new ConfigurationValidationException(["Webhooks_ErrorNotFound"]);
        await AddAsync(notification, [endpoint], cancellationToken);
    }

    private async Task AddAsync(
        WebhookEventNotification notification,
        WebhookEndpointEntity[] endpoints,
        CancellationToken cancellationToken)
    {
        if (endpoints.Length == 0 || await dbContext.WebhookEvents.AnyAsync(x => x.Id == notification.EventId, cancellationToken))
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            eventId = notification.EventId,
            eventType = notification.EventType,
            occurredAt = notification.OccurredAtUtc,
            routeId = notification.RouteId,
            routeName = notification.RouteName,
            sourceSystem = notification.SourceSystem,
            destinationSystem = notification.DestinationSystem,
            entityType = notification.EntityType,
            entityId = notification.EntityId,
            sourceMessageId = notification.SourceMessageId,
            deliveryMessageId = notification.DeliveryMessageId,
            systemCode = notification.SystemCode,
            systemName = notification.SystemName
        }, JsonOptions);
        var webhookEvent = new WebhookEventEntity
        {
            Id = notification.EventId,
            EventType = notification.EventType,
            OccurredAtUtc = notification.OccurredAtUtc,
            PayloadJson = payload
        };
        dbContext.WebhookEvents.Add(webhookEvent);
        foreach (var endpoint in endpoints)
        {
            dbContext.WebhookDeliveries.Add(new WebhookDeliveryEntity
            {
                Id = Guid.NewGuid(),
                EventId = notification.EventId,
                EndpointId = endpoint.Id,
                State = "Pending",
                NextAttemptAtUtc = notification.OccurredAtUtc
            });
        }
    }

    internal static IReadOnlyList<string> ParseEventTypes(string json) =>
        JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
}

public sealed class WebhookAdminService(
    CoordinatorDbContext dbContext,
    ProtectedWebhookSecretService secretProtector,
    WebhookOutboxWriter outbox,
    TimeProvider timeProvider) : IWebhookAdminService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<WebhookEndpointListItem>> GetEndpointsAsync(CancellationToken cancellationToken) =>
        (await dbContext.WebhookEndpoints.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken))
        .Select(ToListItem).ToArray();

    public async Task<WebhookEndpointInput?> GetEndpointAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.WebhookEndpoints.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return entity is null ? null : new WebhookEndpointInput
        {
            Id = entity.Id,
            Name = entity.Name,
            Url = entity.Url,
            Enabled = entity.Enabled,
            SignatureEnabled = entity.SignatureEnabled,
            EventTypes = WebhookOutboxWriter.ParseEventTypes(entity.EventTypesJson).ToList()
        };
    }

    public async Task<WebhookEndpointSaveResult> SaveEndpointAsync(WebhookEndpointInput input, CancellationToken cancellationToken)
    {
        input.Name = input.Name.Trim();
        input.Url = input.Url.Trim();
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 200)
        {
            throw new ConfigurationValidationException(["Webhooks_ErrorName"]);
        }
        if (!Uri.TryCreate(input.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ConfigurationValidationException(["Webhooks_ErrorUrl"]);
        }
        var events = input.EventTypes.Distinct(StringComparer.Ordinal).ToArray();
        if (events.Length == 0 || events.Any(x => !WebhookEventTypes.All.Contains(x, StringComparer.Ordinal)))
        {
            throw new ConfigurationValidationException(["Webhooks_ErrorEvents"]);
        }

        var now = timeProvider.GetUtcNow();
        var entity = input.Id is { } id
            ? await dbContext.WebhookEndpoints.SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ??
              throw new ConfigurationValidationException(["Webhooks_ErrorNotFound"])
            : new WebhookEndpointEntity
            {
                Id = Guid.NewGuid(),
                Name = input.Name,
                Url = input.Url,
                EventTypesJson = "[]",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
        if (input.Id is null)
        {
            dbContext.WebhookEndpoints.Add(entity);
        }

        string? newSecret = null;
        if (input.SignatureEnabled && (entity.ProtectedSecret is null || input.RegenerateSecret))
        {
            newSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            entity.ProtectedSecret = secretProtector.Protect(newSecret);
        }
        entity.Name = input.Name;
        entity.Url = input.Url;
        entity.Enabled = input.Enabled;
        entity.SignatureEnabled = input.SignatureEnabled;
        entity.EventTypesJson = JsonSerializer.Serialize(events, JsonOptions);
        entity.UpdatedAtUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return new WebhookEndpointSaveResult(entity.Id, newSecret);
    }

    public async Task DeleteEndpointAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.WebhookEndpoints.SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ??
                     throw new ConfigurationValidationException(["Webhooks_ErrorNotFound"]);
        dbContext.WebhookEndpoints.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task QueueTestAsync(Guid id, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await outbox.AddForEndpointAsync(new WebhookEventNotification(
            Guid.NewGuid(), WebhookEventTypes.Test, now), id, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WebhookDeliveryListItem>> GetRecentDeliveriesAsync(int take, CancellationToken cancellationToken) =>
        await dbContext.WebhookDeliveries.AsNoTracking()
            .OrderByDescending(x => x.Event.OccurredAtUtc)
            .Take(take)
            .Select(x => new WebhookDeliveryListItem(
                x.Id, x.EventId, x.Endpoint.Name, x.Event.EventType, x.State, x.AttemptCount,
                x.HttpStatusCode, x.LastError, x.Event.OccurredAtUtc, x.LastAttemptAtUtc, x.DeliveredAtUtc))
            .ToListAsync(cancellationToken);

    private static WebhookEndpointListItem ToListItem(WebhookEndpointEntity entity) =>
        new(entity.Id, entity.Name, entity.Url, entity.Enabled, entity.SignatureEnabled,
            entity.ProtectedSecret is not null, WebhookOutboxWriter.ParseEventTypes(entity.EventTypesJson), entity.UpdatedAtUtc);
}

public sealed class WebhookDeliveryService(
    CoordinatorDbContext dbContext,
    IHttpClientFactory httpClientFactory,
    ProtectedWebhookSecretService secretProtector,
    TimeProvider timeProvider,
    IOperationalEventRecorder operationalEvents) : IWebhookDeliveryService
{
    public const string HttpClientName = "SyncCoordinator.Webhooks";
    private static readonly TimeSpan[] RetryDelays =
    [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(12)];

    public async Task<int> DeliverDueAsync(int take, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var deliveries = await dbContext.WebhookDeliveries
            .Include(x => x.Event).Include(x => x.Endpoint)
            .Where(x => x.Endpoint.Enabled && x.NextAttemptAtUtc <= now &&
                        (x.State == "Pending" || x.State == "Retry" || x.State == "Processing" && x.LockedUntilUtc <= now))
            .OrderBy(x => x.NextAttemptAtUtc).Take(take).ToListAsync(cancellationToken);
        foreach (var delivery in deliveries)
        {
            delivery.State = "Processing";
            delivery.LockedUntilUtc = now.AddMinutes(2);
            await dbContext.SaveChangesAsync(cancellationToken);
            await DeliverAsync(delivery, cancellationToken);
        }
        await CleanupAsync(now, cancellationToken);
        return deliveries.Count;
    }

    private async Task DeliverAsync(WebhookDeliveryEntity delivery, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        delivery.AttemptCount++;
        delivery.LastAttemptAtUtc = now;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, delivery.Endpoint.Url);
            request.Content = new StringContent(delivery.Event.PayloadJson, Encoding.UTF8, "application/json");
            request.Headers.Add("Webhook-Id", delivery.EventId.ToString("D"));
            request.Headers.Add("Webhook-Timestamp", now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            if (delivery.Endpoint.SignatureEnabled && delivery.Endpoint.ProtectedSecret is not null)
            {
                var timestamp = request.Headers.GetValues("Webhook-Timestamp").Single();
                var signature = CreateSignature(
                    secretProtector.Unprotect(delivery.Endpoint.ProtectedSecret), timestamp, delivery.Event.PayloadJson);
                request.Headers.Add("Webhook-Signature", signature);
            }
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            delivery.HttpStatusCode = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                delivery.State = "Delivered";
                delivery.DeliveredAtUtc = now;
                delivery.LastError = null;
                delivery.LockedUntilUtc = null;
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }
            await MarkFailedAsync(delivery, $"HTTP {(int)response.StatusCode}", now, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await MarkFailedAsync(delivery, exception.Message, now, cancellationToken);
        }
    }

    private async Task MarkFailedAsync(WebhookDeliveryEntity delivery, string error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        delivery.LastError = error[..Math.Min(error.Length, 2000)];
        delivery.LockedUntilUtc = null;
        if (delivery.AttemptCount <= RetryDelays.Length)
        {
            delivery.State = "Retry";
            delivery.NextAttemptAtUtc = now.Add(RetryDelays[delivery.AttemptCount - 1]);
        }
        else
        {
            delivery.State = "Failed";
            delivery.NextAttemptAtUtc = DateTimeOffset.MaxValue;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        if (delivery.State == "Failed")
        {
            await operationalEvents.RecordAsync(new OperationalEventInput(
                OperationalEventSeverity.Error,
                OperationalEventCategories.Webhook,
                OperationalEventCodes.WebhookDeliveryFailed,
                "worker",
                delivery.Endpoint.Name,
                delivery.LastError), CancellationToken.None);
        }
    }

    private async Task CleanupAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await dbContext.WebhookDeliveries
            .Where(x => x.State == "Delivered" && x.DeliveredAtUtc < now.AddDays(-30) ||
                        x.State == "Failed" && x.LastAttemptAtUtc < now.AddDays(-90))
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.WebhookEvents.Where(x => !x.Deliveries.Any()).ExecuteDeleteAsync(cancellationToken);
    }

    internal static string CreateSignature(string base64Secret, string timestamp, string payload)
    {
        var secret = Convert.FromBase64String(base64Secret);
        var signed = timestamp + "." + payload;
        return "v1=" + Convert.ToBase64String(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(signed)));
    }
}
