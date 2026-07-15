using Dapper;
using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Npgsql;
using SyncCoordinator.Core;

namespace SyncCoordinator.Infrastructure.Persistence;

public sealed class ProtectedConnectionStringService(IDataProtectionProvider provider)
{
    private readonly IDataProtector protector = provider.CreateProtector("SyncCoordinator.DatabaseConnection.v1");

    public string Protect(string connectionString) => protector.Protect(connectionString);
    public string Unprotect(string protectedConnectionString) => protector.Unprotect(protectedConnectionString);
}

internal static class ManagedConnectionStringFactory
{
    public static string Build(string provider, DatabaseConnectionInput input, string password)
    {
        if (string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = input.Port is { } port ? $"{input.Server},{port}" : input.Server,
                InitialCatalog = input.Database,
                IntegratedSecurity = input.IntegratedSecurity,
                Encrypt = input.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
                TrustServerCertificate = input.TrustServerCertificate,
                ApplicationName = "SyncCoordinator"
            };
            if (!input.IntegratedSecurity)
            {
                builder.UserID = input.UserName;
                builder.Password = password;
            }
            return builder.ConnectionString;
        }

        if (string.Equals(provider, "MySql", StringComparison.OrdinalIgnoreCase))
        {
            return new MySqlConnectionStringBuilder
            {
                Server = input.Server,
                Port = (uint)(input.Port ?? 3306),
                Database = input.Database,
                UserID = input.UserName,
                Password = password,
                SslMode = input.Encrypt ? MySqlSslMode.Preferred : MySqlSslMode.Disabled,
                GuidFormat = MySqlGuidFormat.Char36,
                AllowUserVariables = true,
                ApplicationName = "SyncCoordinator"
            }.ConnectionString;
        }

        if (string.Equals(provider, "PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            return new NpgsqlConnectionStringBuilder
            {
                Host = input.Server,
                Port = input.Port ?? 5432,
                Database = input.Database,
                Username = input.UserName,
                Password = password,
                SslMode = !input.Encrypt
                    ? SslMode.Disable
                    : input.TrustServerCertificate ? SslMode.Require : SslMode.VerifyFull,
                ApplicationName = "SyncCoordinator"
            }.ConnectionString;
        }

        throw new ConfigurationValidationException([$"未対応のProviderです: {provider}"]);
    }

    public static DatabaseConnectionInput Parse(Guid systemId, string provider, string connectionString)
    {
        if (string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var (server, port) = SplitSqlServerDataSource(builder.DataSource);
            return new DatabaseConnectionInput
            {
                SystemId = systemId,
                Server = server,
                Port = port,
                Database = builder.InitialCatalog,
                UserName = builder.UserID,
                IntegratedSecurity = builder.IntegratedSecurity,
                Encrypt = builder.Encrypt == SqlConnectionEncryptOption.Mandatory || builder.Encrypt == SqlConnectionEncryptOption.Strict,
                TrustServerCertificate = builder.TrustServerCertificate,
                HasStoredPassword = !builder.IntegratedSecurity && !string.IsNullOrEmpty(builder.Password)
            };
        }

        if (string.Equals(provider, "MySql", StringComparison.OrdinalIgnoreCase))
        {
            var mySql = new MySqlConnectionStringBuilder(connectionString);
            return new DatabaseConnectionInput
            {
                SystemId = systemId,
                Server = mySql.Server,
                Port = (int)mySql.Port,
                Database = mySql.Database,
                UserName = mySql.UserID,
                Encrypt = mySql.SslMode != MySqlSslMode.Disabled,
                HasStoredPassword = !string.IsNullOrEmpty(mySql.Password)
            };
        }

        if (string.Equals(provider, "PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            var postgreSql = new NpgsqlConnectionStringBuilder(connectionString);
            return new DatabaseConnectionInput
            {
                SystemId = systemId,
                Server = postgreSql.Host ?? string.Empty,
                Port = postgreSql.Port,
                Database = postgreSql.Database ?? string.Empty,
                UserName = postgreSql.Username ?? string.Empty,
                Encrypt = postgreSql.SslMode != SslMode.Disable,
                TrustServerCertificate = postgreSql.SslMode == SslMode.Require,
                HasStoredPassword = !string.IsNullOrEmpty(postgreSql.Password)
            };
        }

        throw new ConfigurationValidationException([$"未対応のProviderです: {provider}"]);
    }

    public static string GetPassword(string provider, string connectionString) => provider.ToUpperInvariant() switch
    {
        "SQLSERVER" => new SqlConnectionStringBuilder(connectionString).Password,
        "MYSQL" => new MySqlConnectionStringBuilder(connectionString).Password,
        "POSTGRESQL" => new NpgsqlConnectionStringBuilder(connectionString).Password ?? string.Empty,
        _ => throw new ConfigurationValidationException([$"未対応のProviderです: {provider}"])
    };

    private static (string Server, int? Port) SplitSqlServerDataSource(string dataSource)
    {
        var separator = dataSource.LastIndexOf(',');
        return separator > 0 && int.TryParse(dataSource[(separator + 1)..], out var port)
            ? (dataSource[..separator], port)
            : (dataSource, null);
    }
}

public sealed class DatabaseMetadataService(
    CoordinatorDbContext dbContext,
    ProtectedConnectionStringService protector,
    IOperationalEventRecorder operationalEvents) : IDatabaseMetadataService
{
    public async Task<ConnectionTestResult> TestConnectionAsync(
        string provider,
        DatabaseConnectionInput input,
        CancellationToken cancellationToken)
    {
        SystemDefinitionEntity? system = null;
        try
        {
            if (input.SystemId != Guid.Empty)
            {
                system = await dbContext.Systems
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == input.SystemId, cancellationToken);
            }

            ConfigurationValidator.ValidateConnection(input, provider);

            var password = input.Password;
            if (string.IsNullOrWhiteSpace(password)
                && input.HasStoredPassword
                && !string.IsNullOrWhiteSpace(system?.ProtectedConnectionString))
            {
                var storedConnectionString = protector.Unprotect(system.ProtectedConnectionString);
                password = ManagedConnectionStringFactory.GetPassword(system.Provider, storedConnectionString);
            }

            var connectionString = ManagedConnectionStringFactory.Build(provider, input, password);
            var connection = CreateConnection(provider, connectionString);
            await using (connection)
            {
                await connection.OpenAsync(cancellationToken);
                var unicodeResult = await TestUnicodeRoundTripAsync(
                    connection,
                    provider,
                    cancellationToken);
                if (unicodeResult is not null)
                {
                    return unicodeResult;
                }
            }
            return new ConnectionTestResult(true, "接続とUnicode文字列の往復確認に成功しました。");
        }
        catch (ConfigurationValidationException exception)
        {
            return new ConnectionTestResult(false, string.Join(" ", exception.Errors));
        }
        catch (Exception exception) when (exception is SqlException or MySqlException or NpgsqlException or InvalidOperationException)
        {
            await operationalEvents.RecordAsync(new OperationalEventInput(
                OperationalEventSeverity.Error,
                OperationalEventCategories.Database,
                OperationalEventCodes.DatabaseConnectionTestFailed,
                "web",
                system?.DisplayName ?? provider,
                $"{exception.GetType().Name}: {exception.Message}"), CancellationToken.None);
            return new ConnectionTestResult(false, $"接続に失敗しました: {exception.Message}");
        }
    }

    public async Task<IReadOnlyList<DatabaseTableInfo>> GetTablesAsync(Guid systemId, CancellationToken cancellationToken)
    {
        var (system, connection) = await GetSystemAndConnectionAsync(systemId, cancellationToken);
        await using (connection)
        {
            await connection.OpenAsync(cancellationToken);
            const string sqlServerSql = """
                SELECT TABLE_SCHEMA AS [Schema], TABLE_NAME AS [Name]
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_TYPE = 'BASE TABLE'
                  AND TABLE_NAME NOT IN ('SyncChangeQueue', 'SyncAppliedMessage', 'SyncEntityOrigin', 'SyncDeleteTombstone', 'SyncCoordinatorDeployment')
                ORDER BY TABLE_SCHEMA, TABLE_NAME
                """;
            var sql = system.Provider.ToUpperInvariant() switch
            {
                "SQLSERVER" => sqlServerSql,
                "MYSQL" => sqlServerSql.Replace(" AS [Schema]", " AS `Schema`").Replace(" AS [Name]", " AS `Name`"),
                "POSTGRESQL" => sqlServerSql.Replace(" AS [Schema]", " AS \"Schema\"").Replace(" AS [Name]", " AS \"Name\""),
                _ => throw new InvalidOperationException($"未対応のProviderです: {system.Provider}")
            };
            var rows = await connection.QueryAsync<DatabaseTableInfo>(
                new CommandDefinition(sql, cancellationToken: cancellationToken));
            return rows.AsList();
        }
    }

    public async Task<IReadOnlyList<DatabaseColumnInfo>> GetColumnsAsync(
        Guid systemId,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        var (system, connection) = await GetSystemAndConnectionAsync(systemId, cancellationToken);
        await using (connection)
        {
            await connection.OpenAsync(cancellationToken);
            var sql = system.Provider.ToUpperInvariant() switch
            {
                "MYSQL" => """
                  SELECT c.COLUMN_NAME AS Name, c.DATA_TYPE AS DataType,
                         CASE WHEN c.IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS IsNullable,
                         c.ORDINAL_POSITION AS Ordinal,
                         CASE WHEN k.COLUMN_NAME IS NULL THEN 0 ELSE 1 END AS IsPrimaryKey,
                         c.CHARACTER_MAXIMUM_LENGTH AS MaxLength,
                         c.NUMERIC_PRECISION AS NumericPrecision,
                         c.NUMERIC_SCALE AS NumericScale
                  FROM INFORMATION_SCHEMA.COLUMNS c
                  LEFT JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
                    ON k.TABLE_SCHEMA = c.TABLE_SCHEMA AND k.TABLE_NAME = c.TABLE_NAME
                   AND k.COLUMN_NAME = c.COLUMN_NAME AND k.CONSTRAINT_NAME = 'PRIMARY'
                  WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @table
                  ORDER BY c.ORDINAL_POSITION
                  """,
                "POSTGRESQL" => """
                  SELECT c.column_name AS "Name", c.data_type AS "DataType",
                         (c.is_nullable = 'YES') AS "IsNullable",
                         c.ordinal_position AS "Ordinal",
                         (k.column_name IS NOT NULL) AS "IsPrimaryKey",
                         c.character_maximum_length AS "MaxLength",
                         c.numeric_precision AS "NumericPrecision",
                         c.numeric_scale AS "NumericScale"
                  FROM information_schema.columns c
                  LEFT JOIN (
                      SELECT ku.table_schema, ku.table_name, ku.column_name
                      FROM information_schema.table_constraints tc
                      JOIN information_schema.key_column_usage ku
                        ON ku.constraint_catalog = tc.constraint_catalog
                       AND ku.constraint_schema = tc.constraint_schema
                       AND ku.constraint_name = tc.constraint_name
                      WHERE tc.constraint_type = 'PRIMARY KEY'
                  ) k ON k.table_schema = c.table_schema AND k.table_name = c.table_name AND k.column_name = c.column_name
                  WHERE c.table_schema = @schema AND c.table_name = @table
                  ORDER BY c.ordinal_position
                  """,
                "SQLSERVER" => """
                  SELECT c.COLUMN_NAME AS Name, c.DATA_TYPE AS DataType,
                         CAST(CASE WHEN c.IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS bit) AS IsNullable,
                         c.ORDINAL_POSITION AS Ordinal,
                         CAST(CASE WHEN k.COLUMN_NAME IS NULL THEN 0 ELSE 1 END AS bit) AS IsPrimaryKey,
                         c.CHARACTER_MAXIMUM_LENGTH AS MaxLength,
                         c.NUMERIC_PRECISION AS NumericPrecision,
                         c.NUMERIC_SCALE AS NumericScale
                  FROM INFORMATION_SCHEMA.COLUMNS c
                  LEFT JOIN (
                      SELECT ku.TABLE_SCHEMA, ku.TABLE_NAME, ku.COLUMN_NAME
                      FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                      JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
                        ON ku.CONSTRAINT_NAME = tc.CONSTRAINT_NAME AND ku.TABLE_SCHEMA = tc.TABLE_SCHEMA
                      WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                  ) k ON k.TABLE_SCHEMA = c.TABLE_SCHEMA AND k.TABLE_NAME = c.TABLE_NAME AND k.COLUMN_NAME = c.COLUMN_NAME
                  WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @table
                  ORDER BY c.ORDINAL_POSITION
                  """,
                _ => throw new InvalidOperationException($"未対応のProviderです: {system.Provider}")
            };
            var rows = await connection.QueryAsync(
                new CommandDefinition(sql, new { schema, table }, cancellationToken: cancellationToken));
            return rows.Select(row => MaterializeColumnInfo(
                (string)row.Name,
                (string)row.DataType,
                (object)row.IsNullable,
                (object)row.Ordinal,
                (object)row.IsPrimaryKey,
                (object?)row.MaxLength,
                (object?)row.NumericPrecision,
                (object?)row.NumericScale)).ToList();
        }
    }

    internal static DatabaseColumnInfo MaterializeColumnInfo(
        string name,
        string dataType,
        object isNullable,
        object ordinal,
        object isPrimaryKey,
        object? maxLength = null,
        object? numericPrecision = null,
        object? numericScale = null) =>
        new(
            name,
            dataType,
            Convert.ToBoolean(isNullable, CultureInfo.InvariantCulture),
            Convert.ToInt32(ordinal, CultureInfo.InvariantCulture),
            Convert.ToBoolean(isPrimaryKey, CultureInfo.InvariantCulture),
            NullableInt(maxLength, treatNegativeAsNull: true),
            NullableInt(numericPrecision),
            NullableInt(numericScale));

    private static int? NullableInt(object? value, bool treatNegativeAsNull = false)
    {
        if (value is null or DBNull) return null;
        var converted = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        if (converted > int.MaxValue || converted < int.MinValue || treatNegativeAsNull && converted < 0)
        {
            return null;
        }
        return (int)converted;
    }

    private async Task<(SystemDefinitionEntity System, System.Data.Common.DbConnection Connection)> GetSystemAndConnectionAsync(
        Guid systemId,
        CancellationToken cancellationToken)
    {
        var system = await dbContext.Systems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == systemId, cancellationToken) ??
                     throw new InvalidOperationException("指定されたシステムは存在しません。");
        if (string.IsNullOrWhiteSpace(system.ProtectedConnectionString))
        {
            throw new InvalidOperationException("接続情報が設定されていません。");
        }
        var value = protector.Unprotect(system.ProtectedConnectionString);
        return (system, CreateConnection(system.Provider, value));
    }

    private static System.Data.Common.DbConnection CreateConnection(string provider, string connectionString)
    {
        return string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase)
            ? new SqlConnection(connectionString)
            : string.Equals(provider, "MySql", StringComparison.OrdinalIgnoreCase)
                ? new MySqlConnection(connectionString)
                : string.Equals(provider, "PostgreSql", StringComparison.OrdinalIgnoreCase)
                    ? new NpgsqlConnection(connectionString)
                    : throw new InvalidOperationException($"未対応のProviderです: {provider}");
    }

    private static async Task<ConnectionTestResult?> TestUnicodeRoundTripAsync(
        System.Data.Common.DbConnection connection,
        string provider,
        CancellationToken cancellationToken)
    {
        const string probe = "日本語😀";
        var sql = provider.ToUpperInvariant() switch
        {
            "SQLSERVER" => "SELECT CAST(@value AS nvarchar(max))",
            "MYSQL" => "SELECT CAST(@value AS CHAR CHARACTER SET utf8mb4)",
            "POSTGRESQL" => "SELECT CAST(@value AS text)",
            _ => throw new InvalidOperationException($"未対応のProviderです: {provider}")
        };

        try
        {
            var returned = await connection.QuerySingleAsync<string>(new CommandDefinition(
                sql,
                new { value = probe },
                cancellationToken: cancellationToken));
            return string.Equals(returned, probe, StringComparison.Ordinal)
                ? null
                : new ConnectionTestResult(
                    true,
                    "接続には成功しましたが、日本語と絵文字の往復結果が一致しません。DBまたは実行クライアントの文字コードを確認してください。",
                    HasWarning: true);
        }
        catch (Exception exception) when (exception is SqlException or MySqlException or NpgsqlException or InvalidOperationException)
        {
            return new ConnectionTestResult(
                true,
                $"接続には成功しましたが、Unicode文字列の往復確認に失敗しました: {exception.Message}",
                HasWarning: true);
        }
    }
}
