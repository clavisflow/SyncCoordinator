using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
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

    public static string GetPassword(string provider, string connectionString) =>
        string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase)
            ? new SqlConnectionStringBuilder(connectionString).Password
            : new MySqlConnectionStringBuilder(connectionString).Password;

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
    ProtectedConnectionStringService protector) : IDatabaseMetadataService
{
    public async Task<ConnectionTestResult> TestConnectionAsync(Guid systemId, CancellationToken cancellationToken)
    {
        try
        {
            var connection = await CreateConnectionAsync(systemId, cancellationToken);
            await using (connection)
            {
                await connection.OpenAsync(cancellationToken);
            }
            return new ConnectionTestResult(true, "接続に成功しました。");
        }
        catch (Exception exception) when (exception is SqlException or MySqlException or InvalidOperationException)
        {
            return new ConnectionTestResult(false, $"接続に失敗しました: {exception.Message}");
        }
    }

    public async Task<IReadOnlyList<DatabaseTableInfo>> GetTablesAsync(Guid systemId, CancellationToken cancellationToken)
    {
        var (system, connection) = await GetSystemAndConnectionAsync(systemId, cancellationToken);
        await using (connection)
        {
            await connection.OpenAsync(cancellationToken);
            const string sql = """
                SELECT TABLE_SCHEMA AS [Schema], TABLE_NAME AS [Name]
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_TYPE = 'BASE TABLE'
                  AND TABLE_NAME NOT IN ('SyncChangeQueue', 'SyncAppliedMessage')
                ORDER BY TABLE_SCHEMA, TABLE_NAME
                """;
            var mysqlSql = sql.Replace(" AS [Schema]", " AS `Schema`").Replace(" AS [Name]", " AS `Name`");
            var rows = await connection.QueryAsync<DatabaseTableInfo>(
                new CommandDefinition(system.Provider == "MySql" ? mysqlSql : sql, cancellationToken: cancellationToken));
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
            var sql = system.Provider == "MySql"
                ? """
                  SELECT c.COLUMN_NAME AS Name, c.DATA_TYPE AS DataType,
                         CASE WHEN c.IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS IsNullable,
                         c.ORDINAL_POSITION AS Ordinal,
                         CASE WHEN k.COLUMN_NAME IS NULL THEN 0 ELSE 1 END AS IsPrimaryKey
                  FROM INFORMATION_SCHEMA.COLUMNS c
                  LEFT JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
                    ON k.TABLE_SCHEMA = c.TABLE_SCHEMA AND k.TABLE_NAME = c.TABLE_NAME
                   AND k.COLUMN_NAME = c.COLUMN_NAME AND k.CONSTRAINT_NAME = 'PRIMARY'
                  WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @table
                  ORDER BY c.ORDINAL_POSITION
                  """
                : """
                  SELECT c.COLUMN_NAME AS Name, c.DATA_TYPE AS DataType,
                         CAST(CASE WHEN c.IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS bit) AS IsNullable,
                         c.ORDINAL_POSITION AS Ordinal,
                         CAST(CASE WHEN k.COLUMN_NAME IS NULL THEN 0 ELSE 1 END AS bit) AS IsPrimaryKey
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
                  """;
            var rows = await connection.QueryAsync<DatabaseColumnInfo>(
                new CommandDefinition(sql, new { schema, table }, cancellationToken: cancellationToken));
            return rows.AsList();
        }
    }

    private async Task<System.Data.Common.DbConnection> CreateConnectionAsync(Guid systemId, CancellationToken cancellationToken) =>
        (await GetSystemAndConnectionAsync(systemId, cancellationToken)).Connection;

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
        System.Data.Common.DbConnection connection = string.Equals(system.Provider, "SqlServer", StringComparison.OrdinalIgnoreCase)
            ? new SqlConnection(value)
            : string.Equals(system.Provider, "MySql", StringComparison.OrdinalIgnoreCase)
                ? new MySqlConnection(value)
                : throw new InvalidOperationException($"未対応のProviderです: {system.Provider}");
        return (system, connection);
    }
}
