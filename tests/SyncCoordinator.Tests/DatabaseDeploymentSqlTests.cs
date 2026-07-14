using SyncCoordinator.Contracts;
using SyncCoordinator.Infrastructure.Persistence;

namespace SyncCoordinator.Tests;

public sealed class DatabaseDeploymentSqlTests
{
    [Fact]
    public void SqlServerPlanCreatesSupportTablesAndQuotedTrigger()
    {
        var sql = string.Join("\n", DatabaseDeploymentService.BuildSqlServerBatches(
            "rule:test",
            "sales",
            "Work Order",
            [Column("Order Id")],
            [Column("Order Id"), Column("Status")],
            "A",
            null,
            "TR_Test",
            createTrigger: true));

        Assert.Contains("dbo.SyncChangeQueue", sql, StringComparison.Ordinal);
        Assert.Contains("dbo.SyncAppliedMessage", sql, StringComparison.Ordinal);
        Assert.Contains("dbo.SyncEntityOrigin", sql, StringComparison.Ordinal);
        Assert.Contains("dbo.SyncDeleteTombstone", sql, StringComparison.Ordinal);
        Assert.Contains("ON [sales].[Work Order]", sql, StringComparison.Ordinal);
        Assert.Contains("i.[Order Id]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void MySqlPlanUsesJsonForCompositeKeyAndSeparateTriggers()
    {
        var batches = DatabaseDeploymentService.BuildMySqlBatches(
            "rule:test",
            "business",
            "WorkOrder",
            [Column("OrderId"), Column("LineNo")],
            [Column("OrderId"), Column("LineNo"), Column("Status")],
            "B",
            null,
            "TR_Test",
            createTrigger: true);
        var sql = string.Join("\n", batches);

        Assert.Contains("JSON_OBJECT", sql, StringComparison.Ordinal);
        Assert.Contains("TR_Test_I", sql, StringComparison.Ordinal);
        Assert.Contains("TR_Test_U", sql, StringComparison.Ordinal);
        Assert.Equal(9, batches.Count);
    }

    [Fact]
    public void DestinationOfOneWayRuleOnlyNeedsSupportTables()
    {
        var batches = DatabaseDeploymentService.BuildSqlServerBatches(
            "rule:test",
            "dbo",
            "WorkOrder",
            [Column("Id")],
            [Column("Id")],
            "C",
            null,
            "TR_Test",
            createTrigger: false);

        Assert.Equal(4, batches.Count);
        Assert.DoesNotContain(batches, x => x.Contains("TRIGGER", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MySqlPhysicalDeleteCreatesTombstoneDeleteTrigger()
    {
        var sql = string.Join("\n", DatabaseDeploymentService.BuildMySqlBatches(
            "rule:test", "business", "WorkOrder",
            [Column("OrderId")],
            [Column("OrderId"), Column("Status")],
            "B",
            new DeletionBehavior(DeletionMode.Physical),
            "TR_Test",
            createTrigger: true));

        Assert.Contains("TR_Test_D", sql, StringComparison.Ordinal);
        Assert.Contains("SyncDeleteTombstone", sql, StringComparison.Ordinal);
        Assert.Contains("'Delete'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlServerLogicalDeleteDetectsConfiguredValue()
    {
        var sql = string.Join("\n", DatabaseDeploymentService.BuildSqlServerBatches(
            "rule:test", "dbo", "WorkOrder",
            [Column("Id")],
            [Column("Id"), Column("DeletedFlag")],
            "A",
            new DeletionBehavior(DeletionMode.Logical, "DeletedFlag", "1"),
            "TR_Test",
            createTrigger: true));

        Assert.Contains("[DeletedFlag]", sql, StringComparison.Ordinal);
        Assert.Contains("SyncDeleteTombstone", sql, StringComparison.Ordinal);
        Assert.Contains("'Delete'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgreSqlPlanCreatesJsonbSupportTablesAndQuotedTrigger()
    {
        var batches = DatabaseDeploymentService.BuildPostgreSqlBatches(
            "rule:test", "business", "Work Order",
            [Column("Order Id"), Column("LineNo")],
            [Column("Order Id"), Column("LineNo"), Column("Status")],
            "P",
            new DeletionBehavior(DeletionMode.Physical),
            "TR_Test",
            createTrigger: true);
        var sql = string.Join("\n", batches);

        Assert.Contains("public.\"SyncChangeQueue\"", sql, StringComparison.Ordinal);
        Assert.Contains("jsonb_build_object", sql, StringComparison.Ordinal);
        Assert.Contains("ON \"business\".\"Work Order\"", sql, StringComparison.Ordinal);
        Assert.Contains("AFTER INSERT OR UPDATE OR DELETE", sql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT", sql, StringComparison.Ordinal);
        Assert.Equal(7, batches.Count);
    }

    [Fact]
    public void PostgreSqlLogicalDeleteDetectsConfiguredValue()
    {
        var sql = string.Join("\n", DatabaseDeploymentService.BuildPostgreSqlBatches(
            "rule:test", "public", "WorkOrder",
            [Column("Id")],
            [Column("Id"), Column("DeletedFlag")],
            "P",
            new DeletionBehavior(DeletionMode.Logical, "DeletedFlag", "true"),
            "TR_Test",
            createTrigger: true));

        Assert.Contains("NEW.\"DeletedFlag\"", sql, StringComparison.Ordinal);
        Assert.Contains("SyncDeleteTombstone", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("AFTER INSERT OR UPDATE OR DELETE", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SqlServer", "dbo.SyncCoordinatorDeployment")]
    [InlineData("MySql", "SyncCoordinatorDeployment")]
    [InlineData("PostgreSql", "public.\"SyncCoordinatorDeployment\"")]
    public void DeploymentMarkerRecordsTheExpectedDefinitionHash(string provider, string expectedTable)
    {
        var batches = new List<string> { "definition batch" };

        DatabaseDeploymentService.AppendDeploymentMarker(
            provider,
            batches,
            "route:Forward",
            new string('A', 64));

        var sql = string.Join("\n", batches);
        Assert.Contains(expectedTable, sql, StringComparison.Ordinal);
        Assert.Contains("route:Forward", sql, StringComparison.Ordinal);
        Assert.Contains(new string('A', 64), sql, StringComparison.Ordinal);
    }

    private static DatabaseDeploymentService.DeploymentColumn Column(string physical, string? payload = null) =>
        new(physical, payload ?? physical);
}
