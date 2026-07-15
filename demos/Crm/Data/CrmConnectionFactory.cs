using Microsoft.Data.SqlClient;

namespace SyncCoordinator.Demo.Crm.Data;

public sealed class CrmConnectionFactory(IConfiguration configuration)
{
    private readonly string? _connectionString = configuration.GetConnectionString("demo-crm-db");

    public SqlConnection CreateConnection()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new CrmDataException(
                "接続文字列 'demo-crm-db' が設定されていません。Aspire、環境変数、またはUser Secretsで設定してください。");
        }

        return new SqlConnection(_connectionString);
    }
}

public sealed class CrmDataException : Exception
{
    public CrmDataException(string message) : base(message)
    {
    }

    public CrmDataException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
