using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Npgsql;
using SyncCoordinator.Core;
using SyncCoordinator.Infrastructure;

namespace SyncCoordinator.E2ETests;

public sealed partial class PortalToCrmTests
{
    [E2EFact]
    public async Task FixedValueKeysKeepFanInRecordsSeparateAndConstrainReverseSync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(7));
        var cancellationToken = timeout.Token;
        var keyRingPath = CreateKeyRingDirectory();

        try
        {
            await using var builder = await DistributedApplicationTestingBuilder
                .CreateAsync<Projects.SyncCoordinator_AppHost>(
                    ["RunMode=E2E", $"E2E:KeyRingPath={keyRingPath}"],
                    cancellationToken);
            await using var app = await builder.BuildAsync(cancellationToken);

            await app.StartAsync(cancellationToken);
            await Task.WhenAll(
                app.ResourceNotifications.WaitForResourceHealthyAsync(
                    "coordinator-web",
                    WaitBehavior.StopOnResourceUnavailable,
                    cancellationToken),
                app.ResourceNotifications.WaitForResourceHealthyAsync(
                    "coordinator-worker",
                    WaitBehavior.StopOnResourceUnavailable,
                    cancellationToken));

            var coordinatorConnection = RequireConnectionString(
                await app.GetConnectionStringAsync("coordinator-db", cancellationToken),
                "coordinator-db");
            var portalConnection = AddMySqlConnectorOptions(RequireConnectionString(
                await app.GetConnectionStringAsync("demo-customer-portal-db", cancellationToken),
                "demo-customer-portal-db"));
            var crmConnection = RequireConnectionString(
                await app.GetConnectionStringAsync("demo-crm-db", cancellationToken),
                "demo-crm-db");
            var fieldConnection = PreparePostgreSqlConnectionString(RequireConnectionString(
                await app.GetConnectionStringAsync("demo-field-service-db", cancellationToken),
                "demo-field-service-db"));

            await WaitForCoordinatorSeedAsync(
                coordinatorConnection,
                TimeSpan.FromMinutes(2),
                cancellationToken);
            await CreateFanInTablesAsync(
                portalConnection,
                crmConnection,
                fieldConnection,
                cancellationToken);
            await ConfigureFanInRoutesAsync(
                coordinatorConnection,
                keyRingPath,
                cancellationToken);

            var sourceId = $"FANIN-{Guid.NewGuid():N}";
            await InsertPortalFanInAsync(
                portalConnection,
                sourceId,
                "created by PORTAL",
                cancellationToken);
            await InsertFieldFanInAsync(
                fieldConnection,
                sourceId,
                "created by FIELD",
                cancellationToken);

            var destinationRows = await WaitForCrmFanInRowsAsync(
                crmConnection,
                sourceId,
                expectedCount: 2,
                TimeSpan.FromSeconds(90),
                cancellationToken);
            Assert.Collection(
                destinationRows.OrderBy(row => row.SourceSystem, StringComparer.Ordinal),
                row =>
                {
                    Assert.Equal("FIELD", row.SourceSystem);
                    Assert.Equal("fanin_field_item", row.SourceTable);
                    Assert.Equal("created by FIELD", row.Value);
                },
                row =>
                {
                    Assert.Equal("PORTAL", row.SourceSystem);
                    Assert.Equal("FanInPortalItem", row.SourceTable);
                    Assert.Equal("created by PORTAL", row.Value);
                });

            await UpdateCrmFanInAsync(
                crmConnection,
                "PORTAL",
                "FanInPortalItem",
                sourceId,
                "updated at CRM for PORTAL",
                cancellationToken);
            await UpdateCrmFanInAsync(
                crmConnection,
                "FIELD",
                "fanin_field_item",
                sourceId,
                "updated at CRM for FIELD",
                cancellationToken);
            Assert.Equal(
                "updated at CRM for PORTAL",
                await WaitForPortalFanInValueAsync(
                    portalConnection,
                    sourceId,
                    "updated at CRM for PORTAL",
                    TimeSpan.FromSeconds(90),
                    cancellationToken));
            Assert.Equal(
                "updated at CRM for FIELD",
                await WaitForFieldFanInValueAsync(
                    fieldConnection,
                    sourceId,
                    "updated at CRM for FIELD",
                    TimeSpan.FromSeconds(90),
                    cancellationToken));

            var destinationOnlyId = $"DEST-{Guid.NewGuid():N}";
            var destinationOnlyQueueId = await InsertCrmFanInAsync(
                crmConnection,
                "PORTAL",
                "FanInPortalItem",
                destinationOnlyId,
                "created only at CRM",
                cancellationToken);
            await WaitForCheckpointAsync(
                coordinatorConnection,
                "CRM",
                destinationOnlyQueueId,
                TimeSpan.FromSeconds(30),
                cancellationToken);
            Assert.Null(await ReadPortalFanInValueAsync(
                portalConnection,
                destinationOnlyId,
                cancellationToken));

            await DeletePortalFanInAsync(portalConnection, sourceId, cancellationToken);
            destinationRows = await WaitForCrmFanInRowsAsync(
                crmConnection,
                sourceId,
                expectedCount: 1,
                TimeSpan.FromSeconds(90),
                cancellationToken);
            Assert.Equal("FIELD", Assert.Single(destinationRows).SourceSystem);

            await DeleteCrmFanInAsync(
                crmConnection,
                "FIELD",
                "fanin_field_item",
                sourceId,
                cancellationToken);
            await WaitForFieldFanInDeletionAsync(
                fieldConnection,
                sourceId,
                TimeSpan.FromSeconds(90),
                cancellationToken);
        }
        finally
        {
            TryDeleteKeyRingDirectory(keyRingPath);
        }
    }

    private static async Task CreateFanInTablesAsync(
        string portalConnection,
        string crmConnection,
        string fieldConnection,
        CancellationToken cancellationToken)
    {
        const string portalSql = """
            CREATE TABLE FanInPortalItem
            (
                SourceId varchar(64) NOT NULL,
                Value varchar(200) NOT NULL,
                PRIMARY KEY (SourceId)
            );
            """;
        await using (var connection = new MySqlConnection(portalConnection))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand(portalSql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string crmSql = """
            CREATE TABLE dbo.FanInItem
            (
                SourceSystem nvarchar(32) NOT NULL,
                SourceTable nvarchar(64) NOT NULL,
                SourceId nvarchar(64) NOT NULL,
                Value nvarchar(200) NOT NULL,
                CONSTRAINT PK_FanInItem PRIMARY KEY (SourceSystem, SourceTable, SourceId)
            );
            """;
        await using (var connection = new SqlConnection(crmConnection))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(crmSql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string fieldSql = """
            CREATE TABLE public.fanin_field_item
            (
                source_id varchar(64) NOT NULL PRIMARY KEY,
                value varchar(200) NOT NULL
            );
            """;
        await using (var connection = new NpgsqlConnection(fieldConnection))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(fieldSql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task WaitForCoordinatorSeedAsync(
        string connectionString,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(*) FROM SystemDefinition;";
        var deadline = TimeProvider.System.GetUtcNow() + waitTimeout;
        Exception? lastError = null;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new SqlCommand(sql, connection);
                if (Convert.ToInt32(
                        await command.ExecuteScalarAsync(cancellationToken),
                        CultureInfo.InvariantCulture) >= 3)
                {
                    return;
                }
            }
            catch (SqlException exception)
            {
                lastError = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            "Coordinator database migrations and demo seed did not complete in time.",
            lastError);
    }

    private static async Task ConfigureFanInRoutesAsync(
        string coordinatorConnection,
        string keyRingPath,
        CancellationToken cancellationToken)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:coordinator-db"] = coordinatorConnection,
                ["DataProtection:KeyRingPath"] = keyRingPath,
                ["DatabaseDeployment:AllowDirectApply"] = "true"
            })
            .Build();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSyncCoordinator(configuration)
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<ICoordinatorAdminService>();
        var deployments = scope.ServiceProvider.GetRequiredService<IDatabaseDeploymentService>();

        var portalRouteId = await SaveFanInRouteAsync(
            admin,
            "Fixed-key fan-in PORTAL - CRM",
            "PORTAL",
            "CRM",
            "DemoCustomerPortal",
            "FanInPortalItem",
            "SourceId",
            "Value",
            new ColumnValueContract("varchar", false, 64),
            new ColumnValueContract("varchar", false, 200),
            "PORTAL",
            "FanInPortalItem",
            cancellationToken);
        var fieldRouteId = await SaveFanInRouteAsync(
            admin,
            "Fixed-key fan-in FIELD - CRM",
            "FIELD",
            "CRM",
            "public",
            "fanin_field_item",
            "source_id",
            "value",
            new ColumnValueContract("varchar", false, 64),
            new ColumnValueContract("varchar", false, 200),
            "FIELD",
            "fanin_field_item",
            cancellationToken);

        foreach (var routeId in new[] { portalRouteId, fieldRouteId })
        {
            var plan = await deployments.GetPlanAsync(routeId, cancellationToken);
            foreach (var target in plan.Targets)
            {
                var applied = await deployments.ApplyTargetAsync(
                    routeId,
                    target.SystemCode,
                    target.DatabaseName,
                    cancellationToken);
                Assert.True(applied.Success, applied.Message.ResourceKey);
            }

            var verified = await deployments.VerifyAsync(routeId, cancellationToken);
            Assert.True(verified.Success, verified.Message.ResourceKey);
            var enabled = await deployments.SetEnabledAsync(routeId, true, cancellationToken);
            Assert.True(enabled.Enabled);
        }
    }

    private static async Task<Guid> SaveFanInRouteAsync(
        ICoordinatorAdminService admin,
        string name,
        string sourceSystem,
        string destinationSystem,
        string sourceSchema,
        string sourceTable,
        string sourceIdColumn,
        string valueColumn,
        ColumnValueContract sourceIdContract,
        ColumnValueContract sourceValueContract,
        string sourceSystemValue,
        string sourceTableValue,
        CancellationToken cancellationToken)
    {
        var routeId = await admin.SaveRouteAsync(new RouteConfigurationInput
        {
            Name = name,
            SourceSystem = sourceSystem,
            DestinationSystem = destinationSystem,
            Direction = SyncDirection.Bidirectional,
            ConflictScope = ConflictScope.Field,
            DefaultConflictPolicy = ConflictPolicy.ApplyIncomingAndNotify
        }, cancellationToken);

        await admin.SaveTableMappingAsync(new TableMappingInput
        {
            RouteId = routeId,
            SourceSchema = sourceSchema,
            SourceTable = sourceTable,
            DestinationSchema = "dbo",
            DestinationTable = "FanInItem",
            SyncDeletes = true,
            Columns =
            [
                new ColumnMappingInput
                {
                    SourceColumn = sourceIdColumn,
                    DestinationColumn = "SourceId",
                    Direction = SyncFieldDirection.Bidirectional,
                    IsKey = true,
                    SourceContract = sourceIdContract,
                    DestinationContract = new ColumnValueContract("nvarchar", false, 64)
                },
                new ColumnMappingInput
                {
                    SourceColumn = valueColumn,
                    DestinationColumn = "Value",
                    Direction = SyncFieldDirection.Bidirectional,
                    SourceContract = sourceValueContract,
                    DestinationContract = new ColumnValueContract("nvarchar", false, 200)
                }
            ],
            FixedValues =
            [
                new FixedValueMappingInput
                {
                    Direction = MappingWriteDirection.Forward,
                    TargetColumn = "SourceSystem",
                    Value = sourceSystemValue,
                    IsKey = true,
                    TargetContract = new ColumnValueContract("nvarchar", false, 32)
                },
                new FixedValueMappingInput
                {
                    Direction = MappingWriteDirection.Forward,
                    TargetColumn = "SourceTable",
                    Value = sourceTableValue,
                    IsKey = true,
                    TargetContract = new ColumnValueContract("nvarchar", false, 64)
                }
            ]
        }, cancellationToken);
        return routeId;
    }

    private static async Task InsertPortalFanInAsync(
        string connectionString,
        string sourceId,
        string value,
        CancellationToken cancellationToken)
    {
        const string sql = "INSERT INTO FanInPortalItem (SourceId, Value) VALUES (@sourceId, @value);";
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sourceId", sourceId);
        command.Parameters.AddWithValue("@value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertFieldFanInAsync(
        string connectionString,
        string sourceId,
        string value,
        CancellationToken cancellationToken)
    {
        const string sql =
            "INSERT INTO public.fanin_field_item (source_id, value) VALUES (@sourceId, @value);";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("sourceId", sourceId);
        command.Parameters.AddWithValue("value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<FanInDestinationRow>> WaitForCrmFanInRowsAsync(
        string connectionString,
        string sourceId,
        int expectedCount,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + waitTimeout;
        IReadOnlyList<FanInDestinationRow> rows = [];
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            rows = await ReadCrmFanInRowsAsync(connectionString, sourceId, cancellationToken);
            if (rows.Count == expectedCount)
            {
                return rows;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"CRM fan-in rows for '{sourceId}' did not reach {expectedCount}. Last count: {rows.Count}.");
    }

    private static async Task<IReadOnlyList<FanInDestinationRow>> ReadCrmFanInRowsAsync(
        string connectionString,
        string sourceId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT SourceSystem, SourceTable, SourceId, Value
            FROM dbo.FanInItem
            WHERE SourceId = @sourceId;
            """;
        var rows = new List<FanInDestinationRow>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sourceId", sourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new FanInDestinationRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return rows;
    }

    private static async Task UpdateCrmFanInAsync(
        string connectionString,
        string sourceSystem,
        string sourceTable,
        string sourceId,
        string value,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.FanInItem
            SET Value = @value
            WHERE SourceSystem = @sourceSystem
              AND SourceTable = @sourceTable
              AND SourceId = @sourceId;
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sourceSystem", sourceSystem);
        command.Parameters.AddWithValue("@sourceTable", sourceTable);
        command.Parameters.AddWithValue("@sourceId", sourceId);
        command.Parameters.AddWithValue("@value", value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
    }

    private static async Task<long> InsertCrmFanInAsync(
        string connectionString,
        string sourceSystem,
        string sourceTable,
        string sourceId,
        string value,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT dbo.FanInItem (SourceSystem, SourceTable, SourceId, Value)
            VALUES (@sourceSystem, @sourceTable, @sourceId, @value);
            SELECT COALESCE(MAX(QueueId), 0) FROM dbo.SyncChangeQueue;
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sourceSystem", sourceSystem);
        command.Parameters.AddWithValue("@sourceTable", sourceTable);
        command.Parameters.AddWithValue("@sourceId", sourceId);
        command.Parameters.AddWithValue("@value", value);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task<string?> WaitForPortalFanInValueAsync(
        string connectionString,
        string sourceId,
        string expectedValue,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + waitTimeout;
        string? value = null;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            value = await ReadPortalFanInValueAsync(connectionString, sourceId, cancellationToken);
            if (string.Equals(value, expectedValue, StringComparison.Ordinal))
            {
                return value;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"PORTAL fan-in row '{sourceId}' was not updated to '{expectedValue}'. Last value: '{value}'.");
    }

    private static async Task<string?> ReadPortalFanInValueAsync(
        string connectionString,
        string sourceId,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT Value FROM FanInPortalItem WHERE SourceId = @sourceId;";
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sourceId", sourceId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<string?> WaitForFieldFanInValueAsync(
        string connectionString,
        string sourceId,
        string expectedValue,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + waitTimeout;
        string? value = null;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            value = await ReadFieldFanInValueAsync(connectionString, sourceId, cancellationToken);
            if (string.Equals(value, expectedValue, StringComparison.Ordinal))
            {
                return value;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"FIELD fan-in row '{sourceId}' was not updated to '{expectedValue}'. Last value: '{value}'.");
    }

    private static async Task<string?> ReadFieldFanInValueAsync(
        string connectionString,
        string sourceId,
        CancellationToken cancellationToken)
    {
        const string sql =
            "SELECT value FROM public.fanin_field_item WHERE source_id = @sourceId;";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("sourceId", sourceId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task DeletePortalFanInAsync(
        string connectionString,
        string sourceId,
        CancellationToken cancellationToken)
    {
        const string sql = "DELETE FROM FanInPortalItem WHERE SourceId = @sourceId;";
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sourceId", sourceId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
    }

    private static async Task DeleteCrmFanInAsync(
        string connectionString,
        string sourceSystem,
        string sourceTable,
        string sourceId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM dbo.FanInItem
            WHERE SourceSystem = @sourceSystem
              AND SourceTable = @sourceTable
              AND SourceId = @sourceId;
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sourceSystem", sourceSystem);
        command.Parameters.AddWithValue("@sourceTable", sourceTable);
        command.Parameters.AddWithValue("@sourceId", sourceId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
    }

    private static async Task WaitForFieldFanInDeletionAsync(
        string connectionString,
        string sourceId,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + waitTimeout;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            if (await ReadFieldFanInValueAsync(connectionString, sourceId, cancellationToken) is null)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException($"FIELD fan-in row '{sourceId}' was not deleted.");
    }

    private sealed record FanInDestinationRow(
        string SourceSystem,
        string SourceTable,
        string SourceId,
        string Value);
}
