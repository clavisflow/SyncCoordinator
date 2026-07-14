using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncCoordinator.Core;
using SyncCoordinator.Infrastructure.Connectors;
using SyncCoordinator.Infrastructure.Persistence;

namespace SyncCoordinator.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSyncCoordinator(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var coordinatorConnection = configuration.GetConnectionString("coordinator-db") ??
            "Server=(localdb)\\mssqllocaldb;Database=SyncCoordinator;Trusted_Connection=True;MultipleActiveResultSets=true";
        services.AddDbContext<CoordinatorDbContext>(options => options.UseSqlServer(coordinatorConnection));
        var dataProtection = services.AddDataProtection().SetApplicationName("SyncCoordinator");
        var keyRingPath = configuration["DataProtection:KeyRingPath"];
        if (!string.IsNullOrWhiteSpace(keyRingPath))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
        }
        services.Configure<CoordinatorDatabaseOptions>(configuration.GetSection("CoordinatorDatabase"));
        services.Configure<RelationalConnectorOptions>(configuration.GetSection("SyncCoordinator:Connectors"));
        services.Configure<DatabaseDeploymentOptions>(configuration.GetSection("DatabaseDeployment"));

        services.AddScoped<ICoordinatorStore, EfCoordinatorStore>();
        services.AddScoped<ICoordinatorReadService, EfCoordinatorReadService>();
        services.AddScoped<ICoordinatorAdminService, EfCoordinatorAdminService>();
        services.AddScoped<IDatabaseMetadataService, DatabaseMetadataService>();
        services.AddScoped<IDatabaseDeploymentService, DatabaseDeploymentService>();
        services.AddScoped<IWebhookAdminService, WebhookAdminService>();
        services.AddScoped<IWebhookDeliveryService, WebhookDeliveryService>();
        services.AddScoped<WebhookOutboxWriter>();
        services.AddScoped<ProtectedWebhookSecretService>();
        services.AddHttpClient(WebhookDeliveryService.HttpClientName, client =>
            client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<ProtectedConnectionStringService>();
        services.AddScoped<CoordinatorDatabaseInitializer>();
        services.AddScoped<ConflictResolver>();
        services.AddScoped<SynchronizationCoordinator>();
        services.AddSingleton<IConflictValueMerger, NoOpConflictValueMerger>();
        services.AddSingleton(TimeProvider.System);

        foreach (var system in configuration
                     .GetSection("SyncCoordinator:Connectors:Systems")
                     .Get<List<RelationalSystemOptions>>() ?? [])
        {
            if (!system.Enabled)
            {
                continue;
            }

            services.AddSingleton<ISyncConnector>(provider =>
            {
                var connectionString = configuration.GetConnectionString(system.ConnectionStringName) ??
                    throw new InvalidOperationException(
                        $"Connection string '{system.ConnectionStringName}' is not configured.");
                InfrastructureLog.SampleConnectorEnabled(
                    provider.GetRequiredService<ILogger<SampleJsonRelationalConnector>>(),
                    system.SystemCode);
                return new SampleJsonRelationalConnector(system, connectionString);
            });
        }

        services.AddSingleton<IConnectorCatalog, ConnectorCatalog>();
        return services;
    }
}

internal static partial class InfrastructureLog
{
    [LoggerMessage(LogLevel.Warning, "System {systemCode} uses the sample JSON connector; replace it before production")]
    public static partial void SampleConnectorEnabled(ILogger logger, string systemCode);
}
