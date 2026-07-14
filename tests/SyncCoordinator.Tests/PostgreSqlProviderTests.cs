using Npgsql;
using SyncCoordinator.Core;
using SyncCoordinator.Infrastructure.Persistence;

namespace SyncCoordinator.Tests;

public sealed class PostgreSqlProviderTests
{
    [Fact]
    public void ConnectionSettingsRoundTrip()
    {
        var input = new DatabaseConnectionInput
        {
            Server = "postgres.internal",
            Port = 5433,
            Database = "business",
            UserName = "sync_user",
            Encrypt = true,
            TrustServerCertificate = true
        };

        var connectionString = ManagedConnectionStringFactory.Build("PostgreSql", input, "secret");
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var parsed = ManagedConnectionStringFactory.Parse(Guid.NewGuid(), "PostgreSql", connectionString);

        Assert.Equal("postgres.internal", builder.Host);
        Assert.Equal(5433, builder.Port);
        Assert.Equal(SslMode.Require, builder.SslMode);
        Assert.Equal("postgres.internal", parsed.Server);
        Assert.Equal(5433, parsed.Port);
        Assert.True(parsed.Encrypt);
        Assert.True(parsed.TrustServerCertificate);
        Assert.True(parsed.HasStoredPassword);
    }

    [Fact]
    public void PostgreSqlIsAcceptedAsSystemProvider()
    {
        var input = new SystemConfigurationInput
        {
            Code = "P",
            DisplayName = "PostgreSQL system",
            Provider = "PostgreSql"
        };

        ConfigurationValidator.ValidateSystem(input);
    }
}
