using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SyncCoordinator.Core;

namespace SyncCoordinator.Infrastructure.Persistence;

public sealed class ManagementSettingsService(
    CoordinatorDbContext dbContext,
    TimeProvider timeProvider) : IManagementSettingsService
{
    private const int SettingsId = 1;
    private const int MaxRetentionDays = 3650;
    private static readonly TimeSpan AutomaticCleanupInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan AutomaticCleanupLease = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ManagementSettings> GetAsync(CancellationToken cancellationToken) =>
        ToModel(await GetOrCreateAsync(cancellationToken));

    public async Task SetGlobalPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        if (entity.GlobalPaused == paused)
        {
            return;
        }

        var before = ToModel(entity);
        entity.GlobalPaused = paused;
        entity.UpdatedAtUtc = timeProvider.GetUtcNow();

        AddAudit(paused ? "Paused" : "Resumed", "management-ui", before, ToModel(entity), entity.UpdatedAtUtc);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(ManagementSettings settings, CancellationToken cancellationToken)
    {
        Validate(settings);
        var entity = await GetOrCreateAsync(cancellationToken);
        var before = ToModel(entity);

        entity.PollingIntervalSeconds = settings.PollingIntervalSeconds;
        entity.BatchSize = settings.BatchSize;
        entity.CompletedInboxRetentionDays = settings.CompletedInboxRetentionDays;
        entity.DeliveredWebhookRetentionDays = settings.DeliveredWebhookRetentionDays;
        entity.FailedWebhookRetentionDays = settings.FailedWebhookRetentionDays;
        entity.AcknowledgedOperationalEventRetentionDays = settings.AcknowledgedOperationalEventRetentionDays;
        entity.ConfigurationAuditRetentionDays = settings.ConfigurationAuditRetentionDays;
        entity.UpdatedAtUtc = timeProvider.GetUtcNow();

        AddAudit("Updated", "management-ui", before, ToModel(entity), entity.UpdatedAtUtc);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ManagementCleanupPreview> PreviewCleanupAsync(CancellationToken cancellationToken)
    {
        var settings = await GetOrCreateAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        return new ManagementCleanupPreview(
            await CountAsync(settings, now, cancellationToken),
            now);
    }

    public Task<ManagementCleanupResult> CleanupNowAsync(CancellationToken cancellationToken) =>
        ExecuteCleanupAsync(automatic: false, cancellationToken);

    public async Task<ManagementCleanupResult?> RunAutomaticCleanupIfDueAsync(
        CancellationToken cancellationToken)
    {
        await GetOrCreateAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var dueBefore = now.Subtract(AutomaticCleanupInterval);
        var leaseUntil = now.Add(AutomaticCleanupLease);
        var claimed = await dbContext.ManagementSettings
            .Where(x => x.Id == SettingsId &&
                        (x.LastAutomaticCleanupAtUtc == null || x.LastAutomaticCleanupAtUtc <= dueBefore) &&
                        (x.AutomaticCleanupLeaseUntilUtc == null || x.AutomaticCleanupLeaseUntilUtc <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.AutomaticCleanupLeaseUntilUtc, leaseUntil), cancellationToken);
        if (claimed == 0)
        {
            return null;
        }

        try
        {
            return await ExecuteCleanupAsync(automatic: true, cancellationToken);
        }
        catch
        {
            await dbContext.ManagementSettings
                .Where(x => x.Id == SettingsId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.AutomaticCleanupLeaseUntilUtc, (DateTimeOffset?)null),
                    CancellationToken.None);
            throw;
        }
    }

    private async Task<ManagementCleanupResult> ExecuteCleanupAsync(
        bool automatic,
        CancellationToken cancellationToken)
    {
        var settings = await GetOrCreateAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var completedInbox = await DeleteCompletedInboxAsync(settings, now, cancellationToken);
        var deliveredWebhooks = await DeleteDeliveredWebhooksAsync(settings, now, cancellationToken);
        var failedWebhooks = await DeleteFailedWebhooksAsync(settings, now, cancellationToken);
        await dbContext.WebhookEvents.Where(x => !x.Deliveries.Any()).ExecuteDeleteAsync(cancellationToken);
        var acknowledgedEvents = await DeleteAcknowledgedOperationalEventsAsync(settings, now, cancellationToken);
        var configurationAudits = await DeleteConfigurationAuditsAsync(settings, now, cancellationToken);

        var deleted = new ManagementCleanupCounts(
            completedInbox,
            deliveredWebhooks,
            failedWebhooks,
            acknowledgedEvents,
            configurationAudits);

        if (automatic)
        {
            settings.LastAutomaticCleanupAtUtc = now;
            settings.AutomaticCleanupLeaseUntilUtc = null;
            dbContext.Entry(settings).Property(x => x.AutomaticCleanupLeaseUntilUtc).IsModified = true;
        }
        else
        {
            settings.LastManualCleanupAtUtc = now;
        }
        settings.UpdatedAtUtc = now;
        AddAudit(
            automatic ? "AutomaticCleanup" : "ManualCleanup",
            automatic ? "worker" : "management-ui",
            null,
            deleted,
            now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ManagementCleanupResult(deleted, now, automatic);
    }

    private async Task<ManagementCleanupCounts> CountAsync(
        ManagementSettingsEntity settings,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        new(
            await CountCompletedInboxAsync(settings, now, cancellationToken),
            await CountDeliveredWebhooksAsync(settings, now, cancellationToken),
            await CountFailedWebhooksAsync(settings, now, cancellationToken),
            await CountAcknowledgedOperationalEventsAsync(settings, now, cancellationToken),
            await CountConfigurationAuditsAsync(settings, now, cancellationToken));

    private Task<long> CountCompletedInboxAsync(ManagementSettingsEntity settings, DateTimeOffset now, CancellationToken token) =>
        settings.CompletedInboxRetentionDays == 0
            ? Task.FromResult(0L)
            : dbContext.InboxMessages.LongCountAsync(
                x => (x.State == InboxState.Completed || x.State == InboxState.Superseded) &&
                     x.UpdatedAtUtc < now.AddDays(-settings.CompletedInboxRetentionDays), token);

    private Task<long> CountDeliveredWebhooksAsync(ManagementSettingsEntity settings, DateTimeOffset now, CancellationToken token) =>
        settings.DeliveredWebhookRetentionDays == 0
            ? Task.FromResult(0L)
            : dbContext.WebhookDeliveries.LongCountAsync(
                x => x.State == "Delivered" &&
                     x.DeliveredAtUtc < now.AddDays(-settings.DeliveredWebhookRetentionDays), token);

    private Task<long> CountFailedWebhooksAsync(ManagementSettingsEntity settings, DateTimeOffset now, CancellationToken token) =>
        settings.FailedWebhookRetentionDays == 0
            ? Task.FromResult(0L)
            : dbContext.WebhookDeliveries.LongCountAsync(
                x => x.State == "Failed" &&
                     x.LastAttemptAtUtc < now.AddDays(-settings.FailedWebhookRetentionDays), token);

    private Task<long> CountAcknowledgedOperationalEventsAsync(ManagementSettingsEntity settings, DateTimeOffset now, CancellationToken token) =>
        settings.AcknowledgedOperationalEventRetentionDays == 0
            ? Task.FromResult(0L)
            : dbContext.OperationalEvents.LongCountAsync(
                x => x.AcknowledgedAtUtc != null &&
                     x.LastOccurredAtUtc < now.AddDays(-settings.AcknowledgedOperationalEventRetentionDays), token);

    private Task<long> CountConfigurationAuditsAsync(ManagementSettingsEntity settings, DateTimeOffset now, CancellationToken token) =>
        settings.ConfigurationAuditRetentionDays == 0
            ? Task.FromResult(0L)
            : dbContext.ConfigurationAudits.LongCountAsync(
                x => x.ChangedAtUtc < now.AddDays(-settings.ConfigurationAuditRetentionDays), token);

    private Task<int> DeleteCompletedInboxAsync(ManagementSettingsEntity settings, DateTimeOffset now, CancellationToken token) =>
        settings.CompletedInboxRetentionDays == 0
            ? Task.FromResult(0)
            : dbContext.InboxMessages.Where(
                x => (x.State == InboxState.Completed || x.State == InboxState.Superseded) &&
                     x.UpdatedAtUtc < now.AddDays(-settings.CompletedInboxRetentionDays)).ExecuteDeleteAsync(token);

    private Task<int> DeleteDeliveredWebhooksAsync(ManagementSettingsEntity settings, DateTimeOffset now, CancellationToken token) =>
        settings.DeliveredWebhookRetentionDays == 0
            ? Task.FromResult(0)
            : dbContext.WebhookDeliveries.Where(
                x => x.State == "Delivered" &&
                     x.DeliveredAtUtc < now.AddDays(-settings.DeliveredWebhookRetentionDays)).ExecuteDeleteAsync(token);

    private Task<int> DeleteFailedWebhooksAsync(ManagementSettingsEntity settings, DateTimeOffset now, CancellationToken token) =>
        settings.FailedWebhookRetentionDays == 0
            ? Task.FromResult(0)
            : dbContext.WebhookDeliveries.Where(
                x => x.State == "Failed" &&
                     x.LastAttemptAtUtc < now.AddDays(-settings.FailedWebhookRetentionDays)).ExecuteDeleteAsync(token);

    private Task<int> DeleteAcknowledgedOperationalEventsAsync(ManagementSettingsEntity settings, DateTimeOffset now, CancellationToken token) =>
        settings.AcknowledgedOperationalEventRetentionDays == 0
            ? Task.FromResult(0)
            : dbContext.OperationalEvents.Where(
                x => x.AcknowledgedAtUtc != null &&
                     x.LastOccurredAtUtc < now.AddDays(-settings.AcknowledgedOperationalEventRetentionDays)).ExecuteDeleteAsync(token);

    private Task<int> DeleteConfigurationAuditsAsync(ManagementSettingsEntity settings, DateTimeOffset now, CancellationToken token) =>
        settings.ConfigurationAuditRetentionDays == 0
            ? Task.FromResult(0)
            : dbContext.ConfigurationAudits.Where(
                x => x.ChangedAtUtc < now.AddDays(-settings.ConfigurationAuditRetentionDays)).ExecuteDeleteAsync(token);

    private async Task<ManagementSettingsEntity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var entity = await dbContext.ManagementSettings.FindAsync([SettingsId], cancellationToken);
        if (entity is not null)
        {
            return entity;
        }

        entity = new ManagementSettingsEntity
        {
            Id = SettingsId,
            UpdatedAtUtc = timeProvider.GetUtcNow()
        };
        dbContext.ManagementSettings.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return entity;
    }

    private void AddAudit(
        string action,
        string changedBy,
        object? before,
        object after,
        DateTimeOffset changedAtUtc) =>
        dbContext.ConfigurationAudits.Add(new ConfigurationAuditEntity
        {
            Id = Guid.NewGuid(),
            ConfigurationType = "ManagementSettings",
            ConfigurationId = SettingsId.ToString(CultureInfo.InvariantCulture),
            ConfigurationName = "Management settings",
            Action = action,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before, JsonOptions),
            AfterJson = JsonSerializer.Serialize(after, JsonOptions),
            ChangedBy = changedBy,
            ChangedAtUtc = changedAtUtc
        });

    private static ManagementSettings ToModel(ManagementSettingsEntity entity) => new()
    {
        GlobalPaused = entity.GlobalPaused,
        PollingIntervalSeconds = entity.PollingIntervalSeconds,
        BatchSize = entity.BatchSize,
        CompletedInboxRetentionDays = entity.CompletedInboxRetentionDays,
        DeliveredWebhookRetentionDays = entity.DeliveredWebhookRetentionDays,
        FailedWebhookRetentionDays = entity.FailedWebhookRetentionDays,
        AcknowledgedOperationalEventRetentionDays = entity.AcknowledgedOperationalEventRetentionDays,
        ConfigurationAuditRetentionDays = entity.ConfigurationAuditRetentionDays,
        LastAutomaticCleanupAtUtc = entity.LastAutomaticCleanupAtUtc,
        LastManualCleanupAtUtc = entity.LastManualCleanupAtUtc
    };

    internal static void Validate(ManagementSettings settings)
    {
        var errors = new List<string>();
        if (settings.PollingIntervalSeconds is < 1 or > 300)
        {
            errors.Add("Settings_ErrorPollingInterval");
        }
        if (settings.BatchSize is < 1 or > 5000)
        {
            errors.Add("Settings_ErrorBatchSize");
        }
        ValidateRetention(errors, settings.CompletedInboxRetentionDays, 30, "Settings_ErrorCompletedInboxRetention");
        ValidateRetention(errors, settings.DeliveredWebhookRetentionDays, 1, "Settings_ErrorRetention");
        ValidateRetention(errors, settings.FailedWebhookRetentionDays, 1, "Settings_ErrorRetention");
        ValidateRetention(errors, settings.AcknowledgedOperationalEventRetentionDays, 1, "Settings_ErrorRetention");
        ValidateRetention(errors, settings.ConfigurationAuditRetentionDays, 1, "Settings_ErrorRetention");
        if (errors.Count > 0)
        {
            throw new ConfigurationValidationException(errors.Distinct(StringComparer.Ordinal).ToArray());
        }
    }

    private static void ValidateRetention(List<string> errors, int days, int minimum, string error)
    {
        if (days != 0 && (days < minimum || days > MaxRetentionDays))
        {
            errors.Add(error);
        }
    }
}
