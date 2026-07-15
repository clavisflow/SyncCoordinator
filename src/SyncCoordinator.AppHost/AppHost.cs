using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);
var demoEnabled = !bool.TryParse(
    builder.Configuration["Demo:Enabled"],
    out var configuredDemoEnabled) || configuredDemoEnabled;
const int demoSqlServerPort = 14330;
const int demoMySqlPort = 13306;
const int demoPostgreSqlPort = 15432;
const string demoDatabasePasswordValue = "SyncDemo123!";

if (demoEnabled)
{
    var dataDirectory = Path.Combine(builder.AppHostDirectory, "data");
    var demosDirectory = Path.GetFullPath(
        Path.Combine(builder.AppHostDirectory, "..", "..", "demos"));
    var demoDatabasePassword = builder.AddParameter(
        "demo-database-password",
        demoDatabasePasswordValue,
        secret: true);

    var sqlServer = builder.AddSqlServer(
            "sqlserver",
            password: demoDatabasePassword,
            port: demoSqlServerPort)
        .WithDataVolume();

    var coordinatorDatabase = sqlServer.AddDatabase("coordinator-db", "SyncCoordinatorDemo");
    var crmScriptPath = Path.Combine(AppContext.BaseDirectory, "data", "sqlserver", "init.sql");
    var crmDatabase = sqlServer.AddDatabase("demo-crm-db", "DemoCrm")
        .WithCreationScript(File.ReadAllText(crmScriptPath));

    var mysql = builder.AddMySql(
            "mysql",
            password: demoDatabasePassword,
            port: demoMySqlPort)
        .WithArgs("--character-set-server=utf8mb4", "--collation-server=utf8mb4_0900_ai_ci")
        .WithEnvironment("MYSQL_DATABASE", "DemoCustomerPortal")
        .WithBindMount(
            Path.Combine(dataDirectory, "mysql"),
            "/docker-entrypoint-initdb.d",
            isReadOnly: true)
        .WithDataVolume();
    var portalDatabase = mysql.AddDatabase("demo-customer-portal-db", "DemoCustomerPortal");
    var portalConnection = ReferenceExpression.Create(
        $"{portalDatabase.Resource.ConnectionStringExpression};GuidFormat=Char36;AllowUserVariables=True");
    var portalAppKey = $"base64:{Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))}";

    var postgres = builder.AddPostgres(
            "postgres",
            password: demoDatabasePassword,
            port: demoPostgreSqlPort)
        .WithEnvironment("POSTGRES_DB", "DemoFieldService")
        .WithBindMount(
            Path.Combine(dataDirectory, "postgresql"),
            "/docker-entrypoint-initdb.d",
            isReadOnly: true)
        .WithDataVolume();
    var fieldDatabase = postgres.AddDatabase("demo-field-service-db", "DemoFieldService");

    var web = builder.AddProject<Projects.SyncCoordinator_Web>("coordinator-web")
        .WithReference(coordinatorDatabase)
        .WithReference(crmDatabase)
        .WithReference(fieldDatabase)
        .WithEnvironment("ConnectionStrings__demo-customer-portal-db", portalConnection)
        .WithEnvironment("CoordinatorDatabase__ApplyMigrations", "true")
        .WithEnvironment("CoordinatorDatabase__SeedDemoData", "true")
        .WithEnvironment("CoordinatorDatabase__DemoConnectionStringNames__PORTAL", "demo-customer-portal-db")
        .WithEnvironment("CoordinatorDatabase__DemoConnectionStringNames__CRM", "demo-crm-db")
        .WithEnvironment("CoordinatorDatabase__DemoConnectionStringNames__FIELD", "demo-field-service-db")
        .WithExternalHttpEndpoints()
        .WaitFor(coordinatorDatabase)
        .WaitFor(crmDatabase)
        .WaitFor(portalDatabase)
        .WaitFor(fieldDatabase);

    builder.AddProject<Projects.SyncCoordinator_Worker>("coordinator-worker")
        .WithReference(coordinatorDatabase)
        .WithReference(crmDatabase)
        .WithReference(fieldDatabase)
        .WithEnvironment("ConnectionStrings__demo-customer-portal-db", portalConnection)
        .WithEnvironment("SyncCoordinator__Connectors__Systems__0__SystemCode", "PORTAL")
        .WithEnvironment("SyncCoordinator__Connectors__Systems__0__Provider", "MySql")
        .WithEnvironment("SyncCoordinator__Connectors__Systems__0__ConnectionStringName", "demo-customer-portal-db")
        .WithEnvironment("SyncCoordinator__Connectors__Systems__0__Enabled", "true")
        .WithEnvironment("SyncCoordinator__Connectors__Systems__1__SystemCode", "CRM")
        .WithEnvironment("SyncCoordinator__Connectors__Systems__1__Provider", "SqlServer")
        .WithEnvironment("SyncCoordinator__Connectors__Systems__1__ConnectionStringName", "demo-crm-db")
        .WithEnvironment("SyncCoordinator__Connectors__Systems__1__Enabled", "true")
        .WithEnvironment("SyncCoordinator__Connectors__Systems__2__SystemCode", "FIELD")
        .WithEnvironment("SyncCoordinator__Connectors__Systems__2__Provider", "PostgreSql")
        .WithEnvironment("SyncCoordinator__Connectors__Systems__2__ConnectionStringName", "demo-field-service-db")
        .WithEnvironment("SyncCoordinator__Connectors__Systems__2__Enabled", "true")
        .WaitFor(web)
        .WaitFor(crmDatabase)
        .WaitFor(portalDatabase)
        .WaitFor(fieldDatabase);

    builder.AddDockerfile(
            "demo-customer-portal",
            Path.Combine(demosDirectory, "CustomerPortal"))
        .WithEnvironment("DATABASE_URL", portalDatabase.Resource.UriExpression)
        .WithEnvironment("APP_KEY", portalAppKey)
        .WithEnvironment("APP_ENV", "local")
        .WithEnvironment("APP_DEBUG", "true")
        .WithEnvironment("SESSION_DRIVER", "file")
        .WithEnvironment("SESSION_COOKIE", "sync_demo_customer_portal")
        .WithHttpEndpoint(targetPort: 80, name: "http")
        .WithExternalHttpEndpoints()
        .WaitFor(portalDatabase);

    builder.AddProject<Projects.SyncCoordinator_Demo_Crm>("demo-crm")
        .WithReference(crmDatabase)
        .WithExternalHttpEndpoints()
        .WaitFor(crmDatabase);

    builder.AddDockerfile(
            "demo-field-service",
            Path.Combine(demosDirectory, "FieldService"))
        .WithEnvironment("DATABASE_URL", fieldDatabase.Resource.UriExpression)
        .WithEnvironment("PORT", "3000")
        .WithHttpEndpoint(targetPort: 3000, name: "http")
        .WithExternalHttpEndpoints()
        .WaitFor(fieldDatabase);
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
