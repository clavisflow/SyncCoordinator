using Microsoft.Extensions.Options;
using SyncCoordinator.Core;

namespace SyncCoordinator.Worker;

public sealed class WorkerOptions
{
    public int BatchSize { get; set; } = 100;
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);
}

public sealed class Worker(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> options,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var coordinator = scope.ServiceProvider.GetRequiredService<SynchronizationCoordinator>();
                var processed = await coordinator.RunOnceAsync(options.Value.BatchSize, stoppingToken);
                var webhooks = scope.ServiceProvider.GetRequiredService<IWebhookDeliveryService>();
                var delivered = await webhooks.DeliverDueAsync(options.Value.BatchSize, stoppingToken);
                if (processed > 0)
                {
                    WorkerLog.QueueItemsProcessed(logger, processed);
                }
                if (delivered > 0)
                {
                    WorkerLog.WebhooksProcessed(logger, delivered);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                WorkerLog.PollingCycleFailed(logger, exception);
            }

            await Task.Delay(options.Value.PollingInterval, stoppingToken);
        }
    }
}

internal static partial class WorkerLog
{
    [LoggerMessage(LogLevel.Information, "Processed {count} change queue items")]
    public static partial void QueueItemsProcessed(ILogger logger, int count);

    [LoggerMessage(LogLevel.Information, "Processed {count} webhook deliveries")]
    public static partial void WebhooksProcessed(ILogger logger, int count);

    [LoggerMessage(LogLevel.Error, "Synchronization polling cycle failed")]
    public static partial void PollingCycleFailed(ILogger logger, Exception exception);
}
