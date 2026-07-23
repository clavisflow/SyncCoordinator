using Dapper;
using SyncCoordinator.Core;
using SyncCoordinator.Infrastructure.Connectors;

namespace SyncCoordinator.Tests;

public sealed class RelatedEntityMappingTests
{
    [Fact]
    public void SqlServerReadUsesLeftJoinForProjectionAndExistsForEligibility()
    {
        var mapping = new RelationalEntityMapping(
            "WorkRequest",
            "sales",
            "WorkRequest",
            [
                new RelationalColumnBinding("WorkRequestId", "WorkRequestId", null, true, ColumnValueContract.Unknown),
                new RelationalColumnBinding("Description", "Description", null, false, ColumnValueContract.Unknown),
                new RelationalColumnBinding("reception.ReceptionName", "ReceptionName", "reception", false, ColumnValueContract.Unknown)
            ],
            [],
            [
                new RelationalRelatedTable(
                    "sales", "Reception", "reception", "{source}.ReceptionId = {related}.ReceptionId",
                    RelatedTableUsage.Projection, null),
                new RelationalRelatedTable(
                    "sales", "WorkRequestStaff", "staff", "{source}.WorkRequestId = {related}.WorkRequestId",
                    RelatedTableUsage.Eligibility, "{related}.StaffNo IS NOT NULL")
            ]);
        var connector = new MappedRelationalConnector(
            "A", RelationalProvider.SqlServer, "Server=(local)", null!);

        var sql = connector.BuildReadEntitySql(mapping, new DynamicParameters());

        Assert.Contains("LEFT JOIN [sales].[Reception] AS [reception]", sql, StringComparison.Ordinal);
        Assert.Contains("[sc_base].ReceptionId = [reception].ReceptionId", sql, StringComparison.Ordinal);
        Assert.Contains("[reception].[ReceptionName] AS [reception.ReceptionName]", sql, StringComparison.Ordinal);
        Assert.Contains("EXISTS (SELECT 1 FROM [sales].[WorkRequestStaff] AS [staff]", sql, StringComparison.Ordinal);
        Assert.Contains("[staff].StaffNo IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("CAST([sc_base].[WorkRequestId] AS nvarchar(4000)) = @Key0", sql, StringComparison.Ordinal);
        Assert.StartsWith("SELECT TOP (2)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadTreatsEmptyColumnAliasAsBaseTable()
    {
        var mapping = new RelationalEntityMapping(
            "SupportCase",
            "DemoCustomerPortal",
            "SupportCase",
            [new RelationalColumnBinding("CaseNumber", "CaseNumber", "", true, ColumnValueContract.Unknown)],
            [],
            []);
        var connector = new MappedRelationalConnector(
            "PORTAL", RelationalProvider.MySql, "Server=(local)", null!);

        var sql = connector.BuildReadEntitySql(mapping, new DynamicParameters());

        Assert.Contains("`sc_base`.`CaseNumber` AS `CaseNumber`", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("``.`CaseNumber`", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RelationalProvider.MySql)]
    [InlineData(RelationalProvider.PostgreSql)]
    public void NonSqlServerReadSupportsEligibilityAndLimitsCardinality(RelationalProvider provider)
    {
        var mapping = new RelationalEntityMapping(
            "WorkRequest",
            "sales",
            "WorkRequest",
            [new RelationalColumnBinding("WorkRequestId", "WorkRequestId", null, true, ColumnValueContract.Unknown)],
            [],
            [new RelationalRelatedTable(
                "sales", "WorkRequestStaff", "staff", "{source}.WorkRequestId = {related}.WorkRequestId",
                RelatedTableUsage.Eligibility, "{related}.StaffNo = 'S001' AND {source}.Enabled = 1")]);
        var connector = new MappedRelationalConnector(provider.ToString(), provider, "Server=(local)", null!);
        var parameters = new DynamicParameters();

        var sql = connector.BuildReadEntitySql(mapping, parameters);

        Assert.Contains("EXISTS (SELECT 1", sql, StringComparison.Ordinal);
        Assert.Contains("StaffNo = 'S001'", sql, StringComparison.Ordinal);
        Assert.Contains("Enabled = 1", sql, StringComparison.Ordinal);
        Assert.EndsWith(" LIMIT 2;", sql, StringComparison.Ordinal);
    }
}
