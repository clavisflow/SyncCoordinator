using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
var runMode = ResolveRunMode(builder.Configuration);

if (runMode is AppHostRunMode.Demo or AppHostRunMode.E2E)
{
    builder.AddDemoTopology(runMode == AppHostRunMode.E2E
        ? DemoTopologyOptions.CreateE2E(builder.Configuration["E2E:KeyRingPath"])
        : DemoTopologyOptions.Demo);
}
else
{
    var worker = builder.AddProject<Projects.SyncCoordinator_Worker>("worker");
    var web = builder.AddProject<Projects.SyncCoordinator_Web>("web")
        .WithExternalHttpEndpoints();

    var useContainerDatabase = bool.TryParse(
        builder.Configuration["CoordinatorDatabase:UseContainer"],
        out var configuredUseContainer) && configuredUseContainer;

    if (useContainerDatabase)
    {
        var coordinatorDatabase = builder.AddSqlServer("sqlserver")
            .AddDatabase("coordinator-db");

        worker.WithReference(coordinatorDatabase)
            .WaitFor(coordinatorDatabase);
        web.WithReference(coordinatorDatabase)
            .WaitFor(coordinatorDatabase);
    }
    else
    {
        var coordinatorDatabase = builder.AddConnectionString("coordinator-db");
        worker.WithReference(coordinatorDatabase);
        web.WithReference(coordinatorDatabase);
    }
}

builder.Build().Run();

static AppHostRunMode ResolveRunMode(IConfiguration configuration)
{
    var configured = configuration["RunMode"];
    if (!string.IsNullOrWhiteSpace(configured))
    {
        if (Enum.TryParse<AppHostRunMode>(configured, ignoreCase: true, out var runMode) &&
            Enum.IsDefined(runMode))
        {
            return runMode;
        }

        throw new InvalidOperationException(
            $"RunMode '{configured}' is invalid. Use Demo, E2E, or Core.");
    }

    var demoEnabled = !bool.TryParse(
        configuration["Demo:Enabled"],
        out var configuredDemoEnabled) || configuredDemoEnabled;
    return demoEnabled ? AppHostRunMode.Demo : AppHostRunMode.Core;
}

internal enum AppHostRunMode
{
    Demo,
    E2E,
    Core
}
