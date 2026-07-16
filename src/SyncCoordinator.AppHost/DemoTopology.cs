using Aspire.Hosting.ApplicationModel;

internal sealed record DemoTopologyOptions(
    int? SqlServerPort,
    int? MySqlPort,
    int? PostgreSqlPort,
    bool PersistData,
    bool ExposeExternalEndpoints,
    bool SeedConflictScenarios,
    string? DataProtectionKeyRingPath)
{
    public static DemoTopologyOptions Demo { get; } = new(
        SqlServerPort: 14330,
        MySqlPort: 13306,
        PostgreSqlPort: 15432,
        PersistData: true,
        ExposeExternalEndpoints: true,
        SeedConflictScenarios: true,
        DataProtectionKeyRingPath: null);

    public static DemoTopologyOptions CreateE2E(string? dataProtectionKeyRingPath) => new(
        SqlServerPort: null,
        MySqlPort: null,
        PostgreSqlPort: null,
        PersistData: false,
        ExposeExternalEndpoints: false,
        SeedConflictScenarios: false,
        DataProtectionKeyRingPath: dataProtectionKeyRingPath);
}

internal static class DemoTopology
{
    private const string DemoDatabasePasswordValue = "SyncDemo123!";

    public static void AddDemoTopology(
        this IDistributedApplicationBuilder builder,
        DemoTopologyOptions options)
    {
        var dataDirectory = Path.Combine(builder.AppHostDirectory, "data");
        var demosDirectory = Path.GetFullPath(
            Path.Combine(builder.AppHostDirectory, "..", "..", "demos"));
        var demoDatabasePassword = builder.AddParameter(
            "demo-database-password",
            DemoDatabasePasswordValue,
            secret: true);

        var sqlServer = builder.AddSqlServer(
            "sqlserver",
            password: demoDatabasePassword,
            port: options.SqlServerPort);
        if (options.PersistData)
        {
            sqlServer.WithDataVolume();
        }

        var coordinatorDatabase = sqlServer.AddDatabase("coordinator-db", "SyncCoordinatorDemo");
        var crmScriptPath = Path.Combine(AppContext.BaseDirectory, "data", "sqlserver", "init.sql");
        var crmDatabase = sqlServer.AddDatabase("demo-crm-db", "DemoCrm")
            .WithCreationScript(File.ReadAllText(crmScriptPath));

        var mysql = builder.AddMySql(
                "mysql",
                password: demoDatabasePassword,
                port: options.MySqlPort)
            .WithArgs("--character-set-server=utf8mb4", "--collation-server=utf8mb4_0900_ai_ci")
            .WithEnvironment("MYSQL_DATABASE", "DemoCustomerPortal")
            .WithBindMount(
                Path.Combine(dataDirectory, "mysql"),
                "/docker-entrypoint-initdb.d",
                isReadOnly: true);
        if (options.PersistData)
        {
            mysql.WithDataVolume();
        }

        var portalDatabase = mysql.AddDatabase("demo-customer-portal-db", "DemoCustomerPortal");
        var portalConnection = ReferenceExpression.Create(
            $"{portalDatabase.Resource.ConnectionStringExpression};GuidFormat=Char36;AllowUserVariables=True");
        var portalAppKey = $"base64:{Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))}";

        var postgres = builder.AddPostgres(
                "postgres",
                password: demoDatabasePassword,
                port: options.PostgreSqlPort)
            .WithEnvironment("POSTGRES_DB", "DemoFieldService")
            .WithBindMount(
                Path.Combine(dataDirectory, "postgresql"),
                "/docker-entrypoint-initdb.d",
                isReadOnly: true);
        if (options.PersistData)
        {
            postgres.WithDataVolume();
        }

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
            .WithEnvironment("DatabaseDeployment__AllowDirectApply", "true")
            .WaitFor(coordinatorDatabase)
            .WaitFor(crmDatabase)
            .WaitFor(portalDatabase)
            .WaitFor(fieldDatabase);
        AddSharedKeyRingIfConfigured(web, options);
        ExposeIfRequested(web, options);

        var worker = builder.AddProject<Projects.SyncCoordinator_Worker>("coordinator-worker")
            .WithReference(coordinatorDatabase)
            .WithEnvironment(
                "CoordinatorDatabase__SeedDemoData",
                options.SeedConflictScenarios ? "true" : "false")
            .WaitFor(web)
            .WaitFor(crmDatabase)
            .WaitFor(portalDatabase)
            .WaitFor(fieldDatabase);
        AddSharedKeyRingIfConfigured(worker, options);

        var customerPortal = builder.AddDockerfile(
                "demo-customer-portal",
                Path.Combine(demosDirectory, "CustomerPortal"))
            .WithEnvironment("DATABASE_URL", portalDatabase.Resource.UriExpression)
            .WithEnvironment("APP_KEY", portalAppKey)
            .WithEnvironment("APP_ENV", "local")
            .WithEnvironment("APP_DEBUG", "true")
            .WithEnvironment("SESSION_DRIVER", "file")
            .WithEnvironment("SESSION_COOKIE", "sync_demo_customer_portal")
            .WithHttpEndpoint(targetPort: 80, name: "http")
            .WaitFor(portalDatabase);
        ExposeIfRequested(customerPortal, options);

        var crm = builder.AddProject<Projects.SyncCoordinator_Demo_Crm>("demo-crm")
            .WithReference(crmDatabase)
            .WaitFor(crmDatabase);
        ExposeIfRequested(crm, options);

        var fieldService = builder.AddDockerfile(
                "demo-field-service",
                Path.Combine(demosDirectory, "FieldService"))
            .WithEnvironment("DATABASE_URL", fieldDatabase.Resource.UriExpression)
            .WithEnvironment("PORT", "3000")
            .WithHttpEndpoint(targetPort: 3000, name: "http")
            .WaitFor(fieldDatabase);
        ExposeIfRequested(fieldService, options);
    }

    private static void AddSharedKeyRingIfConfigured<T>(
        IResourceBuilder<T> resource,
        DemoTopologyOptions options)
        where T : IResourceWithEnvironment
    {
        if (!string.IsNullOrWhiteSpace(options.DataProtectionKeyRingPath))
        {
            resource.WithEnvironment(
                "DataProtection__KeyRingPath",
                options.DataProtectionKeyRingPath);
        }
    }

    private static void ExposeIfRequested<T>(
        IResourceBuilder<T> resource,
        DemoTopologyOptions options)
        where T : IResourceWithEndpoints
    {
        if (options.ExposeExternalEndpoints)
        {
            resource.WithExternalHttpEndpoints();
        }
    }
}
