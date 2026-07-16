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
        services.Configure<DatabaseDeploymentOptions>(configuration.GetSection("DatabaseDeployment"));

        services.AddScoped<ICoordinatorStore, EfCoordinatorStore>();
        services.AddScoped<ICoordinatorReadService, EfCoordinatorReadService>();
        services.AddScoped<ICoordinatorAdminService, EfCoordinatorAdminService>();
        services.AddScoped<IConflictResolutionService, ConflictResolutionService>();
        services.AddScoped<IDemoConflictSeeder, DemoConflictSeeder>();
        services.AddScoped<IDatabaseMetadataService, DatabaseMetadataService>();
        services.AddScoped<IDatabaseDeploymentService, DatabaseDeploymentService>();
        services.AddScoped<IWebhookAdminService, WebhookAdminService>();
        services.AddScoped<IWebhookDeliveryService, WebhookDeliveryService>();
        services.AddSingleton<IOperationalEventRecorder, OperationalEventRecorder>();
        services.AddScoped<IOperationalEventAdminService, OperationalEventAdminService>();
        services.AddScoped<IManagementSettingsService, ManagementSettingsService>();
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

        services.AddScoped<RelationalMappingProvider>();
        services.AddScoped<IManagedConnectorDefinitionSource, EfManagedConnectorDefinitionSource>();
        services.AddScoped<IManagedConnectorFactory, ManagedConnectorFactory>();
        services.AddScoped<IConnectorCatalog, ManagedConnectorCatalog>();
        return services;
    }
}
