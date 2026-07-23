using Dapper;
using SyncCoordinator.Contracts;
using SyncCoordinator.Core;
using SyncCoordinator.Infrastructure.Connectors;
using SyncCoordinator.Infrastructure.Persistence;

namespace SyncCoordinator.Tests;

public sealed class FixedValueKeyTests
{
    private static readonly ColumnValueContract RequiredText =
        new("nvarchar", false, 64);

    [Fact]
    public void PhysicalCompositeKeySelectsRouteAndReturnsCanonicalSourceId()
    {
        var mapping = Mapping("A");

        Assert.True(mapping.MatchesPhysicalEntityId(
            """{"Id":42,"@fixed:SourceSystem":"A"}"""));
        Assert.False(mapping.MatchesPhysicalEntityId(
            """{"Id":42,"@fixed:SourceSystem":"B"}"""));
        Assert.Equal(
            "42",
            mapping.ToCanonicalEntityId(
                """{"Id":42,"@fixed:SourceSystem":"A"}"""));
        Assert.Equal(
            """{"Id":42,"@fixed:SourceSystem":"A"}""",
            MappedRelationalConnector.CreatePhysicalEntityId(mapping, "42"));
    }

    [Fact]
    public void ReadPredicateUsesMappedAndFixedKeyColumns()
    {
        var connector = new MappedRelationalConnector(
            "C",
            RelationalProvider.SqlServer,
            "Server=(local)",
            null!);

        var sql = connector.BuildReadEntitySql(Mapping("A"), new DynamicParameters());

        Assert.Contains("CAST([sc_base].[SourceId] AS nvarchar(4000)) = @Key0", sql);
        Assert.Contains("CAST([sc_base].[SourceSystem] AS nvarchar(4000)) = @Key1", sql);
    }

    [Fact]
    public void SqlServerTriggerOnlyQueuesRowsForItsFixedKey()
    {
        var sql = string.Join("\n", DatabaseDeploymentService.BuildSqlServerBatches(
            "Customer",
            "dbo",
            "Customer",
            Keys(),
            [new DatabaseDeploymentService.DeploymentColumn("SourceId", "Id")],
            "C",
            new DeletionBehavior(DeletionMode.Physical),
            "TR_SC_TEST",
            createTrigger: true));

        Assert.Contains(
            "CONVERT(nvarchar(4000), i.[SourceSystem]) = N'A'",
            sql);
        Assert.Contains(
            "CONVERT(nvarchar(4000), d.[SourceSystem]) = N'A'",
            sql);
        Assert.Contains("AS [@fixed:SourceSystem]", sql);
    }

    [Fact]
    public void MySqlAndPostgreSqlTriggersOnlyQueueRowsForTheirFixedKey()
    {
        var mysql = string.Join("\n", DatabaseDeploymentService.BuildMySqlBatches(
            "Customer", "app", "Customer", Keys(),
            [new DatabaseDeploymentService.DeploymentColumn("SourceId", "Id")],
            "C", new DeletionBehavior(DeletionMode.Physical), "TR_SC_TEST", createTrigger: true));
        var postgres = string.Join("\n", DatabaseDeploymentService.BuildPostgreSqlBatches(
            "Customer", "app", "Customer", Keys(),
            [new DatabaseDeploymentService.DeploymentColumn("SourceId", "Id")],
            "C", new DeletionBehavior(DeletionMode.Physical), "TR_SC_TEST", createTrigger: true));

        Assert.Contains("IF CAST(NEW.`SourceSystem` AS CHAR) = 'A' THEN", mysql);
        Assert.Contains("IF CAST(OLD.`SourceSystem` AS CHAR) = 'A' THEN", mysql);
        Assert.Contains("'@fixed:SourceSystem', NEW.`SourceSystem`", mysql);
        Assert.Contains("IF CAST(NEW.\"SourceSystem\" AS text) = 'A' THEN", postgres);
        Assert.Contains("IF CAST(OLD.\"SourceSystem\" AS text) = 'A' THEN", postgres);
        Assert.Contains("'@fixed:SourceSystem', NEW.\"SourceSystem\"", postgres);
    }

    private static RelationalEntityMapping Mapping(string sourceSystem) =>
        new(
            "Customer",
            "dbo",
            "Customer",
            [new RelationalColumnBinding(
                "Id",
                "SourceId",
                null,
                true,
                new ColumnValueContract("int", false))],
            [new RelationalFixedValue(
                "SourceSystem",
                sourceSystem,
                true,
                RequiredText)],
            []);

    private static DatabaseDeploymentService.DeploymentColumn[] Keys() =>
    [
        new("SourceId", "Id"),
        new("SourceSystem", "@fixed:SourceSystem", "A")
    ];
}
