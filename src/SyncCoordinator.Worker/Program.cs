using SyncCoordinator.Infrastructure;
using SyncCoordinator.Infrastructure.Persistence;
using SyncCoordinator.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddWindowsService(options => options.ServiceName = "SyncCoordinator Worker");
builder.Services.AddSyncCoordinator(builder.Configuration);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await using (var scope = host.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<CoordinatorDatabaseInitializer>()
        .InitializeAsync(CancellationToken.None);
}

await host.RunAsync();
