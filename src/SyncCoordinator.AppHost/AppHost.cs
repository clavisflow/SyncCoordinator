var builder = DistributedApplication.CreateBuilder(args);

var worker = builder.AddProject<Projects.SyncCoordinator_Worker>("worker");
var web = builder.AddProject<Projects.SyncCoordinator_Web>("web")
    .WithExternalHttpEndpoints();

var useContainerDatabase = bool.TryParse(
    builder.Configuration["CoordinatorDatabase:UseContainer"],
    out var configuredUseContainer) && configuredUseContainer;

if (useContainerDatabase)
{
    var coordinatorDatabase = builder.AddSqlServer("coordinator-sql")
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

builder.Build().Run();
