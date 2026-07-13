var builder = DistributedApplication.CreateBuilder(args);

var coordinatorDatabase = builder.AddSqlServer("coordinator-sql")
    .AddDatabase("coordinator-db");

builder.AddProject<Projects.SyncCoordinator_Worker>("worker")
    .WithReference(coordinatorDatabase)
    .WaitFor(coordinatorDatabase);

builder.AddProject<Projects.SyncCoordinator_Web>("web")
    .WithExternalHttpEndpoints()
    .WithReference(coordinatorDatabase)
    .WaitFor(coordinatorDatabase);

builder.Build().Run();
