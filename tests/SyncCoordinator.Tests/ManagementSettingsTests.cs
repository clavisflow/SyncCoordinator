using SyncCoordinator.Core;
using SyncCoordinator.Infrastructure.Persistence;

namespace SyncCoordinator.Tests;

public sealed class ManagementSettingsTests
{
    [Fact]
    public void DefaultSettingsAreValid()
    {
        ManagementSettingsService.Validate(new ManagementSettings());
    }

    [Fact]
    public void ZeroRetentionKeepsDataIndefinitely()
    {
        ManagementSettingsService.Validate(new ManagementSettings
        {
            CompletedInboxRetentionDays = 0,
            DeliveredWebhookRetentionDays = 0,
            FailedWebhookRetentionDays = 0,
            AcknowledgedOperationalEventRetentionDays = 0,
            ConfigurationAuditRetentionDays = 0
        });
    }

    [Fact]
    public void CompletedInboxRequiresASafeRetentionWindow()
    {
        var settings = new ManagementSettings { CompletedInboxRetentionDays = 29 };

        var exception = Assert.Throws<ConfigurationValidationException>(
            () => ManagementSettingsService.Validate(settings));

        Assert.Contains("Settings_ErrorCompletedInboxRetention", exception.Errors);
    }

    [Fact]
    public void CleanupTotalIncludesEveryManagedHistoryType()
    {
        var counts = new ManagementCleanupCounts(1, 2, 3, 4, 6);

        Assert.Equal(16, counts.Total);
    }
}
