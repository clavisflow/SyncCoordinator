using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.Json;
using SyncCoordinator.Contracts;
using SyncCoordinator.Core;

namespace SyncCoordinator.Infrastructure.Persistence;

public sealed class CoordinatorDatabaseOptions
{
    public bool ApplyMigrations { get; set; }
    public bool SeedDemoData { get; set; }
    public Dictionary<string, string> DemoConnectionStringNames { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CoordinatorDatabaseInitializer(
    CoordinatorDbContext dbContext,
    IOptions<CoordinatorDatabaseOptions> options,
    IConfiguration configuration,
    ProtectedConnectionStringService connectionProtector,
    TimeProvider timeProvider)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.ApplyMigrations)
        {
            return;
        }

        await dbContext.Database.MigrateAsync(cancellationToken);
        if (!options.Value.SeedDemoData)
        {
            return;
        }

        if (!await dbContext.Systems.AnyAsync(cancellationToken))
        {
            await SeedDemoDataAsync(cancellationToken);
        }

        await SeedMissingDemoConnectionsAsync(cancellationToken);
    }

    private async Task SeedMissingDemoConnectionsAsync(CancellationToken cancellationToken)
    {
        if (options.Value.DemoConnectionStringNames.Count == 0)
        {
            return;
        }

        var systemCodes = options.Value.DemoConnectionStringNames.Keys.ToArray();
        var systems = await dbContext.Systems
            .Where(x => systemCodes.Contains(x.Code))
            .ToDictionaryAsync(x => x.Code, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var changed = false;

        foreach (var (systemCode, connectionStringName) in options.Value.DemoConnectionStringNames)
        {
            if (!systems.TryGetValue(systemCode, out var system) ||
                !string.IsNullOrWhiteSpace(system.ProtectedConnectionString))
            {
                continue;
            }

            var connectionString = configuration.GetConnectionString(connectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                continue;
            }

            connectionString = PrepareDemoConnectionString(system.Provider, connectionString);
            system.ProtectedConnectionString = connectionProtector.Protect(connectionString);
            system.ConnectionUpdatedAtUtc = timeProvider.GetUtcNow();
            changed = true;
        }

        if (changed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    internal static string PrepareDemoConnectionString(string provider, string connectionString)
    {
        if (!string.Equals(provider, "PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }

        return new NpgsqlConnectionStringBuilder(connectionString)
        {
            SslMode = SslMode.Disable,
            GssEncryptionMode = GssEncryptionMode.Disable
        }.ConnectionString;
    }

    private async Task SeedDemoDataAsync(CancellationToken cancellationToken)
    {
        var seed = CreateDemoSeed();
        dbContext.Systems.AddRange(seed.Systems);
        dbContext.Routes.AddRange(seed.Routes);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal static (IReadOnlyList<SystemDefinitionEntity> Systems, IReadOnlyList<SyncRouteEntity> Routes) CreateDemoSeed()
    {
        var portal = NewSystem("PORTAL", "Customer Portal", "MySql");
        var crm = NewSystem("CRM", "CRM", "SqlServer");
        var field = NewSystem("FIELD", "Field Service", "PostgreSql");
        var supportCase = NewDemoRule(
            "Customer Portal - CRM",
            portal,
            "SupportCase",
            crm,
            "DemoCustomerPortal",
            "SupportCase",
            "dbo",
            "SupportCase",
            [
                M("CaseNumber", "CaseRef", true, C("varchar", false, 64), C("nvarchar", false, 64)),
                M("CustomerName", "ContactName", false, C("varchar", true, 200), C("nvarchar", true, 100)),
                M("Email", "ContactEmail", false, C("varchar", true, 320), C("nvarchar", true, 200)),
                M("Phone", "ContactPhone", false, C("varchar", true, 40), C("nvarchar", true, 30)),
                M("ProductName", "ProductLabel", false, C("varchar", true, 200), C("nvarchar", true, 150)),
                M("SerialNumber", "DeviceSerial", false, C("varchar", true, 100), C("nvarchar", true, 100)),
                M("Subject", "CaseTitle", false, C("varchar", true, 300), C("nvarchar", true, 200)),
                M("Description", "CaseDetails", false, C("text"), C("nvarchar")),
                M("PreferredVisitDate", "RequestedVisitOn", false, C("date"), C("date")),
                M("Status", "WorkflowState", false, C("varchar", false, 40), C("nvarchar", false, 32)),
                M("ResponseMessage", "AgentReply", false, C("text"), C("nvarchar")),
                M("OriginSystem", "SourceCode", false, C("varchar", false, 64), C("nvarchar", false, 64)),
                M("UpdatedAtUtc", "ModifiedAtUtc", false, C("datetime", false), C("datetimeoffset", false))
            ]);
        var workOrder = NewDemoRule(
            "CRM - Field Service",
            crm,
            "WorkOrder",
            field,
            "dbo",
            "WorkOrder",
            "public",
            "work_order",
            [
                M("WorkOrderNumber", "work_order_no", true, C("nvarchar", false, 64), C("varchar", false, 64)),
                M("CaseId", "source_case_id", false, C("nvarchar", true, 64), C("varchar", true, 64)),
                M("CaseNumber", "case_ref", false, C("nvarchar", true, 64), C("varchar", true, 64)),
                M("CustomerName", "customer_display_name", false, C("nvarchar", true, 200), C("varchar", true, 120)),
                M("Address", "service_address", false, C("nvarchar", true, 500), C("varchar", true, 300)),
                M("Phone", "contact_phone", false, C("nvarchar", true, 40), C("varchar", true, 30)),
                M("ProductName", "product_label", false, C("nvarchar", true, 200), C("varchar", true, 120)),
                M("ProblemSummary", "problem_summary", false, C("nvarchar", true, 500), C("varchar", true, 120)),
                M("ScheduledAt", "scheduled_at", false, C("datetimeoffset"), C("timestamptz")),
                M("TechnicianName", "technician_name", false, C("nvarchar", true, 200), C("varchar", true, 80)),
                M("Status", "job_status", false, C("nvarchar", false, 40), C("varchar", false, 20), WorkOrderStatusForward(), WorkOrderStatusReverse()),
                M("WorkResult", "work_result", false, C("nvarchar"), C("varchar", true, 1000)),
                M("CompletedAt", "completed_at", false, C("datetimeoffset"), C("timestamptz")),
                M("OriginSystem", "source_code", false, C("nvarchar", false, 64), C("varchar", false, 64)),
                M("UpdatedAtUtc", "modified_at", false, C("datetimeoffset", false), C("timestamptz", false))
            ]);

        return ([portal, crm, field], [supportCase, workOrder]);
    }

    private static SystemDefinitionEntity NewSystem(string code, string name, string provider) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        DisplayName = name,
        Provider = provider,
        Enabled = true
    };

    private static SyncRouteEntity NewDemoRule(
        string name,
        SystemDefinitionEntity source,
        string entityType,
        SystemDefinitionEntity destination,
        string sourceSchema,
        string sourceTable,
        string destinationSchema,
        string destinationTable,
        IReadOnlyCollection<DemoColumn> columns)
    {
        var routeId = Guid.NewGuid();
        var mapping = new RouteTableMappingEntity
        {
            RouteId = routeId,
            SourceSchema = sourceSchema,
            SourceTable = sourceTable,
            DestinationSchema = destinationSchema,
            DestinationTable = destinationTable,
            SyncDeletes = true,
            SourceDeletionMode = DeletionMode.Physical,
            DestinationDeletionMode = DeletionMode.Physical
        };
        mapping.Columns.AddRange(columns.Select(column => new RouteColumnMappingEntity
        {
            Id = Guid.NewGuid(),
            TableMappingId = routeId,
            SourceColumn = column.Source,
            DestinationColumn = column.Destination,
            IsKey = column.IsKey,
            SourceDataType = column.SourceContract.DataType,
            SourceIsNullable = column.SourceContract.IsNullable,
            SourceMaxLength = column.SourceContract.MaxLength,
            SourceNumericPrecision = column.SourceContract.NumericPrecision,
            SourceNumericScale = column.SourceContract.NumericScale,
            DestinationDataType = column.DestinationContract.DataType,
            DestinationIsNullable = column.DestinationContract.IsNullable,
            DestinationMaxLength = column.DestinationContract.MaxLength,
            DestinationNumericPrecision = column.DestinationContract.NumericPrecision,
            DestinationNumericScale = column.DestinationContract.NumericScale,
            ForwardTransformJson = SerializeTransform(column.ForwardTransform),
            ReverseTransformJson = SerializeTransform(column.ReverseTransform),
            ConflictPolicy = null
        }));

        var route = new SyncRouteEntity
        {
            Id = routeId,
            Name = name,
            SourceSystemId = source.Id,
            SourceSystem = source,
            EntityType = entityType,
            DestinationSystemId = destination.Id,
            DestinationSystem = destination,
            Direction = SyncDirection.Bidirectional,
            DeploymentState = DatabaseDeploymentState.Draft,
            ConflictScope = ConflictScope.Field,
            DefaultConflictPolicy = ConflictPolicy.HoldAndNotify,
            Enabled = false,
            TableMapping = mapping
        };
        mapping.Route = route;
        return route;
    }

    private static ColumnValueContract C(
        string dataType,
        bool nullable = true,
        int? maxLength = null,
        int? precision = null,
        int? scale = null) =>
        new(dataType, nullable, maxLength, precision, scale);

    private static DemoColumn M(
        string source,
        string destination,
        bool key,
        ColumnValueContract sourceContract,
        ColumnValueContract destinationContract,
        ValueTransformInput? forward = null,
        ValueTransformInput? reverse = null) =>
        new(source, destination, key, sourceContract, destinationContract, forward ?? new(), reverse ?? new());

    private static ValueTransformInput WorkOrderStatusForward() => new()
    {
        RejectUnmappedValues = true,
        ValueMap =
        [
            V("Draft", "draft"), V("Assigned", "assigned"), V("Scheduled", "scheduled"),
            V("InProgress", "in_progress"), V("Completed", "done"), V("Cancelled", "cancelled")
        ]
    };

    private static ValueTransformInput WorkOrderStatusReverse() => new()
    {
        RejectUnmappedValues = true,
        ValueMap =
        [
            V("draft", "Draft"), V("assigned", "Assigned"), V("scheduled", "Scheduled"),
            V("in_progress", "InProgress"), V("done", "Completed"), V("cancelled", "Cancelled")
        ]
    };

    private static ValueMapEntryInput V(string source, string target) => new()
    {
        SourceValue = source,
        TargetValue = target
    };

    private static string? SerializeTransform(ValueTransformInput transform) =>
        transform.IsIdentity ? null : JsonSerializer.Serialize(transform);

    private sealed record DemoColumn(
        string Source,
        string Destination,
        bool IsKey,
        ColumnValueContract SourceContract,
        ColumnValueContract DestinationContract,
        ValueTransformInput ForwardTransform,
        ValueTransformInput ReverseTransform);
}
