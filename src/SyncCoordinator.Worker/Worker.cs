using SyncCoordinator.Core;

namespace SyncCoordinator.Worker;

public sealed class Worker(
    IServiceScopeFactory scopeFactory,
    ILogger<Worker> logger,
    IOperationalEventRecorder operationalEvents) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollingInterval = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var settingsService = scope.ServiceProvider.GetRequiredService<IManagementSettingsService>();
                var settings = await settingsService.GetAsync(stoppingToken);
                pollingInterval = TimeSpan.FromSeconds(settings.PollingIntervalSeconds);
                var coordinator = scope.ServiceProvider.GetRequiredService<SynchronizationCoordinator>();
                var processed = await coordinator.RunOnceAsync(settings.BatchSize, stoppingToken);
                var demoConflicts = scope.ServiceProvider.GetRequiredService<IDemoConflictSeeder>();
                var seededConflicts = await demoConflicts.SeedIfReadyAsync(stoppingToken);
                var conflictResolutions = scope.ServiceProvider.GetRequiredService<IConflictResolutionService>();
                var resolved = await conflictResolutions.ProcessPendingAsync(settings.BatchSize, stoppingToken);
                var webhooks = scope.ServiceProvider.GetRequiredService<IWebhookDeliveryService>();
                var delivered = await webhooks.DeliverDueAsync(settings.BatchSize, stoppingToken);
                var cleanup = await settingsService.RunAutomaticCleanupIfDueAsync(stoppingToken);
                if (processed > 0)
                {
                    WorkerLog.QueueItemsProcessed(logger, processed);
                }
                if (delivered > 0)
                {
                    WorkerLog.WebhooksProcessed(logger, delivered);
                }
                if (resolved > 0)
                {
                    WorkerLog.ConflictResolutionsProcessed(logger, resolved);
                }
                if (seededConflicts > 0)
                {
                    WorkerLog.DemoConflictsSeeded(logger, seededConflicts);
                }
                if (cleanup is { Deleted.Total: > 0 })
                {
                    WorkerLog.ManagementRowsCleaned(logger, cleanup.Deleted.Total);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                WorkerLog.PollingCycleFailed(logger, exception);
                await operationalEvents.RecordAsync(new OperationalEventInput(
                    OperationalEventSeverity.Error,
                    OperationalEventCategories.Synchronization,
                    OperationalEventCodes.SynchronizationPollingFailed,
                    "worker",
                    null,
                    $"{exception.GetType().Name}: {exception.Message}"), CancellationToken.None);
            }

            await Task.Delay(pollingInterval, stoppingToken);
        }
    }
}

internal static partial class WorkerLog
{
    [LoggerMessage(LogLevel.Information, "Processed {count} change queue items")]
    public static partial void QueueItemsProcessed(ILogger logger, int count);

    [LoggerMessage(LogLevel.Information, "Processed {count} webhook deliveries")]
    public static partial void WebhooksProcessed(ILogger logger, int count);

    [LoggerMessage(LogLevel.Information, "Processed {count} manual conflict resolutions")]
    public static partial void ConflictResolutionsProcessed(ILogger logger, int count);

    [LoggerMessage(LogLevel.Information, "Seeded {count} demo conflicts")]
    public static partial void DemoConflictsSeeded(ILogger logger, int count);

    [LoggerMessage(LogLevel.Information, "Deleted {count} expired management database rows")]
    public static partial void ManagementRowsCleaned(ILogger logger, long count);

    [LoggerMessage(LogLevel.Error, "Synchronization polling cycle failed")]
    public static partial void PollingCycleFailed(ILogger logger, Exception exception);
}
