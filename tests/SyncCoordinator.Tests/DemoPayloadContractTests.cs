using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using SyncCoordinator.Core;
using SyncCoordinator.Demo.Crm.Models;
using SyncCoordinator.Infrastructure.Persistence;
using CrmSupportCase = SyncCoordinator.Demo.Crm.Models.SupportCasePayload;
using CrmWorkOrder = SyncCoordinator.Demo.Crm.Models.WorkOrderPayload;

namespace SyncCoordinator.Tests;

public sealed class DemoPayloadContractTests
{
    [Fact]
    public void DemoSeedContainsConfigurationButLeavesDeploymentToTheUi()
    {
        var seed = CoordinatorDatabaseInitializer.CreateDemoSeed();

        Assert.Equal(["CRM", "FIELD", "PORTAL"], seed.Systems.Select(system => system.Code).Order());
        Assert.All(seed.Routes, route =>
        {
            Assert.False(route.Enabled);
            Assert.Equal(DatabaseDeploymentState.Draft, route.DeploymentState);
            Assert.NotNull(route.TableMapping);
            Assert.NotEmpty(route.TableMapping.Columns);
            Assert.Contains(route.TableMapping.Columns, column => column.IsKey);
        });
        Assert.Collection(
            seed.Routes.OrderBy(route => route.EntityType),
            route =>
            {
                Assert.Equal("SupportCase", route.EntityType);
                Assert.Equal("SupportCase", route.TableMapping!.SourceTable);
                Assert.Equal("SupportCase", route.TableMapping.DestinationTable);
            },
            route =>
            {
                Assert.Equal("WorkOrder", route.EntityType);
                Assert.Equal("WorkOrder", route.TableMapping!.SourceTable);
                Assert.Equal("work_order", route.TableMapping.DestinationTable);
                Assert.Contains(route.TableMapping.Columns, column => column.DestinationColumn.Contains('_'));
                Assert.Contains(route.TableMapping.Columns, column => column.ForwardTransformJson is not null);
            });
    }

    [Theory]
    [InlineData("src/SyncCoordinator.AppHost/data/mysql/001-init.sql")]
    [InlineData("src/SyncCoordinator.AppHost/data/sqlserver/init.sql")]
    [InlineData("src/SyncCoordinator.AppHost/data/postgresql/001-init.sql")]
    public void DemoDatabaseInitializationDoesNotPredeploySynchronizationObjects(string relativePath)
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));

        Assert.DoesNotContain("SyncChangeQueue", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncAppliedMessage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncEntityOrigin", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncDeleteTombstone", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TRIGGER", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LaravelPortalAndCrmSupportCaseFieldsMatch()
    {
        string[] expected =
        [
            "CaseNumber", "CustomerName", "Email", "Phone", "ProductName", "SerialNumber",
            "Subject", "Description", "PreferredVisitDate", "Status", "ResponseMessage"
        ];

        var portalFields = ContractFieldsFromSource(
            "demos/CustomerPortal/app/Repositories/SupportCaseRepository.php",
            "private const PAYLOAD_FIELDS = [",
            "];");

        Assert.Equal(expected.Order(), portalFields.Order());
        Assert.Equal(expected.Order(), PropertyNames<CrmSupportCase>());
    }

    [Fact]
    public void CrmAndNextFieldServiceWorkOrderFieldsMatch()
    {
        string[] expected =
        [
            "WorkOrderNumber", "CaseId", "CaseNumber", "CustomerName", "Address", "Phone",
            "ProductName", "ProblemSummary", "ScheduledAt", "TechnicianName", "Status",
            "WorkResult", "CompletedAt"
        ];

        var fieldServiceFields = ContractFieldsFromSource(
            "demos/FieldService/lib/db.ts",
            "const CONTRACT_FIELDS = [",
            "] as const;");

        Assert.Equal(expected.Order(), PropertyNames<CrmWorkOrder>());
        Assert.Equal(expected.Order(), fieldServiceFields.Order());
    }

    [Fact]
    public void CrmSyncEntityMapsFromRepositoryProjection()
    {
        var updatedAtUtc = new DateTimeOffset(2026, 7, 15, 6, 41, 50, TimeSpan.Zero);
        var table = new DataTable();
        table.Columns.Add("EntityId", typeof(string));
        table.Columns.Add("OriginSystem", typeof(string));
        table.Columns.Add("UpdatedAtUtc", typeof(DateTimeOffset));
        table.Columns.Add("PayloadJson", typeof(string));
        table.Rows.Add("SC-20260715-75BB1A9E", "PORTAL", updatedAtUtc, "{\"Status\":\"New\"}");

        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());

        var deserialize = SqlMapper.GetTypeDeserializer(typeof(SyncEntity), reader);
        var entity = Assert.IsType<SyncEntity>(deserialize(reader));

        Assert.Equal("SC-20260715-75BB1A9E", entity.EntityId);
        Assert.Equal("PORTAL", entity.OriginSystem);
        Assert.Equal(updatedAtUtc, entity.UpdatedAtUtc);
        Assert.Equal("{\"Status\":\"New\"}", entity.PayloadJson);
    }

    private static IEnumerable<string> PropertyNames<T>() =>
        typeof(T).GetProperties().Select(property => property.Name).Order();

    private static string[] ContractFieldsFromSource(
        string relativePath,
        string startMarker,
        string endMarker)
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Contract start marker was not found in {relativePath}.");
        start += startMarker.Length;

        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Contract end marker was not found in {relativePath}.");

        return Regex.Matches(source[start..end], "[\\\"']([A-Za-z][A-Za-z0-9]*)[\\\"']")
            .Select(match => match.Groups[1].Value)
            .ToArray();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SyncCoordinator.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("SyncCoordinator repository root was not found.");
    }
}
