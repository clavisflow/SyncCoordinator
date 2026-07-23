using System.Data;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Dapper;
using SyncCoordinator.Core;
using SyncCoordinator.Demo.Crm.Models;
using SyncCoordinator.Infrastructure.Persistence;
using SyncCoordinator.Contracts;
using CrmSupportCase = SyncCoordinator.Demo.Crm.Models.SupportCasePayload;
using CrmWorkOrder = SyncCoordinator.Demo.Crm.Models.WorkOrderPayload;

namespace SyncCoordinator.Tests;

public sealed class DemoPayloadContractTests
{
    [Fact]
    public void DemoConflictScenariosUseDistinctRecordKeys()
    {
        string[] recordKeys =
        [
            DemoConflictSeeder.EntityId,
            DemoConflictSeeder.DeleteEntityId,
            DemoConflictSeeder.UpdateThenDeleteEntityId,
            DemoConflictSeeder.DeleteThenUpdateEntityId,
            DemoConflictSeeder.ResolvedEntityId,
            DemoConflictSeeder.DateConflictEntityId,
            DemoConflictSeeder.DateTimeConflictEntityId,
            DemoConflictSeeder.TextConflictEntityId,
            DemoConflictSeeder.IntegerConflictEntityId,
            DemoConflictSeeder.DecimalConflictEntityId,
            DemoConflictSeeder.BooleanConflictEntityId,
            DemoConflictSeeder.NullConflictEntityId,
            DemoConflictSeeder.StatusConflictEntityId,
            DemoConflictSeeder.GuidConflictEntityId
        ];

        Assert.Equal(14, recordKeys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("CASE-UPDATE-1001", DemoConflictSeeder.EntityId);
        Assert.Equal("CASE-DELETE-1001", DemoConflictSeeder.DeleteEntityId);
        Assert.Equal("CASE-UPDATE-THEN-DELETE-1001", DemoConflictSeeder.UpdateThenDeleteEntityId);
        Assert.Equal("CASE-DELETE-THEN-UPDATE-1001", DemoConflictSeeder.DeleteThenUpdateEntityId);
        Assert.Equal("CASE-RESOLVED-1001", DemoConflictSeeder.ResolvedEntityId);
        Assert.Equal("CASE-DATE-CONFLICT-1001", DemoConflictSeeder.DateConflictEntityId);
        Assert.Equal("WO-DATETIME-CONFLICT-1001", DemoConflictSeeder.DateTimeConflictEntityId);
    }

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
                Assert.Collection(
                    route.TableMapping.RelatedTables.OrderBy(x => x.Alias),
                    assignment =>
                    {
                        Assert.Equal("assignment", assignment.Alias);
                        Assert.Equal(RelatedTableUsage.Eligibility, assignment.Usage);
                        Assert.Equal("{related}.StaffNo IS NOT NULL", assignment.ConditionExpression);
                    },
                    supportCase =>
                    {
                        Assert.Equal("case_info", supportCase.Alias);
                        Assert.Equal(RelatedTableUsage.Projection, supportCase.Usage);
                        Assert.True(supportCase.DetectChanges);
                    });
            });
    }

    [Fact]
    public void DemoConflictScenarioCreatesTwoHeldFieldConflicts()
    {
        var route = DemoSupportCaseRoute();
        var template = new EntityPayload(new Dictionary<string, System.Text.Json.Nodes.JsonNode?>
        {
            ["CaseNumber"] = System.Text.Json.Nodes.JsonValue.Create(DemoConflictSeeder.EntityId),
            ["Subject"] = System.Text.Json.Nodes.JsonValue.Create("original"),
            ["Description"] = System.Text.Json.Nodes.JsonValue.Create("original")
        });
        var scenario = DemoConflictSeeder.CreateScenario(template);
        var baseline = new SyncSnapshot(
            route.Id, route.DestinationSystem, route.EntityType, DemoConflictSeeder.EntityId,
            scenario.Baseline, scenario.Baseline);

        var resolution = new ConflictResolver(new NoOpConflictValueMerger()).Resolve(
            route.EntityType, baseline, scenario.Incoming, scenario.Current, route);

        Assert.True(resolution.IsHeld);
        Assert.Equal(["Description", "Subject"], resolution.Conflicts.Select(x => x.FieldName));
        Assert.All(resolution.Conflicts, conflict => Assert.Equal("Held", conflict.Resolution));
        Assert.Equal(DemoConflictSeeder.EntityId, scenario.Baseline.Fields["CaseNumber"]!.GetValue<string>());
    }

    [Fact]
    public void DemoDateConflictScenarioCreatesOneHeldDateConflict()
    {
        var route = DemoSupportCaseRoute();
        var template = new EntityPayload(new Dictionary<string, System.Text.Json.Nodes.JsonNode?>
        {
            ["CaseNumber"] = System.Text.Json.Nodes.JsonValue.Create(DemoConflictSeeder.DateConflictEntityId),
            ["Subject"] = System.Text.Json.Nodes.JsonValue.Create("original"),
            ["Description"] = System.Text.Json.Nodes.JsonValue.Create("original"),
            ["PreferredVisitDate"] = System.Text.Json.Nodes.JsonValue.Create("2026-07-22")
        });
        var scenario = DemoConflictSeeder.CreateDateConflictScenario(
            template, DemoConflictSeeder.DateConflictEntityId);
        var baseline = new SyncSnapshot(
            route.Id, route.DestinationSystem, route.EntityType, DemoConflictSeeder.DateConflictEntityId,
            scenario.Baseline, scenario.Baseline);

        var resolution = new ConflictResolver(new NoOpConflictValueMerger()).Resolve(
            route.EntityType, baseline, scenario.Incoming, scenario.Current, route);

        var conflict = Assert.Single(resolution.Conflicts);
        Assert.True(resolution.IsHeld);
        Assert.Equal("PreferredVisitDate", conflict.FieldName);
        Assert.Equal("2026-07-22", conflict.BaseValue!.GetValue<string>());
        Assert.Equal("2026-07-24", conflict.IncomingValue!.GetValue<string>());
        Assert.Equal("2026-07-23", conflict.CurrentValue!.GetValue<string>());
    }

    [Fact]
    public void DemoDateTimeConflictScenarioCreatesOneHeldScheduledAtConflict()
    {
        var route = DemoRoute(DemoConflictSeeder.WorkOrderEntityType);
        var scenario = DemoConflictSeeder.CreateDateTimeConflictScenario(
            DemoConflictSeeder.DateTimeConflictEntityId);
        var baseline = new SyncSnapshot(
            route.Id, route.DestinationSystem, route.EntityType, DemoConflictSeeder.DateTimeConflictEntityId,
            scenario.Baseline, scenario.Baseline);

        var resolution = new ConflictResolver(new NoOpConflictValueMerger()).Resolve(
            route.EntityType, baseline, scenario.Incoming, scenario.Current, route);

        var conflict = Assert.Single(resolution.Conflicts);
        Assert.True(resolution.IsHeld);
        Assert.Equal("ScheduledAt", conflict.FieldName);
        Assert.Equal("2026-07-27T12:00:00+09:00", conflict.BaseValue!.GetValue<string>());
        Assert.Equal("2026-07-27T14:00:00+09:00", conflict.IncomingValue!.GetValue<string>());
        Assert.Equal("2026-07-27T11:30:00+09:00", conflict.CurrentValue!.GetValue<string>());
    }

    [Fact]
    public void DemoWorkOrderScenariosCoverDifferentConflictTypes()
    {
        var route = DemoRoute(DemoConflictSeeder.WorkOrderEntityType);
        var scenarios = new[]
        {
            ("ProblemSummary", DemoConflictSeeder.CreateWorkOrderConflictScenario("TEXT", "ProblemSummary",
                System.Text.Json.Nodes.JsonValue.Create("base"), System.Text.Json.Nodes.JsonValue.Create("crm"), System.Text.Json.Nodes.JsonValue.Create("field"))),
            ("EstimatedMinutes", DemoConflictSeeder.CreateWorkOrderConflictScenario("INT", "EstimatedMinutes",
                System.Text.Json.Nodes.JsonValue.Create(60m), System.Text.Json.Nodes.JsonValue.Create(90m), System.Text.Json.Nodes.JsonValue.Create(120m))),
            ("EstimatedCost", DemoConflictSeeder.CreateWorkOrderConflictScenario("DECIMAL", "EstimatedCost",
                System.Text.Json.Nodes.JsonValue.Create(100m), System.Text.Json.Nodes.JsonValue.Create(125.25m), System.Text.Json.Nodes.JsonValue.Create(140.75m))),
            ("RequiresParts", DemoConflictSeeder.CreateWorkOrderConflictScenario("BOOL", "RequiresParts",
                null, System.Text.Json.Nodes.JsonValue.Create(true), System.Text.Json.Nodes.JsonValue.Create(false))),
            ("WorkNote", DemoConflictSeeder.CreateWorkOrderConflictScenario("NULL", "WorkNote",
                System.Text.Json.Nodes.JsonValue.Create("base"), null, System.Text.Json.Nodes.JsonValue.Create("field"))),
            ("Status", DemoConflictSeeder.CreateWorkOrderConflictScenario("STATUS", "Status",
                System.Text.Json.Nodes.JsonValue.Create("Scheduled"), System.Text.Json.Nodes.JsonValue.Create("InProgress"), System.Text.Json.Nodes.JsonValue.Create("Completed"))),
            ("ExternalTrackingId", DemoConflictSeeder.CreateWorkOrderConflictScenario("GUID", "ExternalTrackingId",
                System.Text.Json.Nodes.JsonValue.Create("11111111-1111-1111-1111-111111111111"),
                System.Text.Json.Nodes.JsonValue.Create("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
                System.Text.Json.Nodes.JsonValue.Create("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")))
        };

        foreach (var (fieldName, scenario) in scenarios)
        {
            var baseline = new SyncSnapshot(route.Id, route.DestinationSystem, route.EntityType, "demo",
                scenario.Baseline, scenario.Baseline);
            var resolution = new ConflictResolver(new NoOpConflictValueMerger()).Resolve(
                route.EntityType, baseline, scenario.Incoming, scenario.Current, route);

            Assert.True(resolution.IsHeld);
            Assert.Equal(fieldName, Assert.Single(resolution.Conflicts).FieldName);
        }
    }

    [Fact]
    public void DemoDeleteConflictScenarioCreatesHeldDeleteConflict()
    {
        var route = DemoSupportCaseRoute();
        var template = new EntityPayload(new Dictionary<string, System.Text.Json.Nodes.JsonNode?>
        {
            ["CaseNumber"] = System.Text.Json.Nodes.JsonValue.Create(DemoConflictSeeder.EntityId),
            ["Subject"] = System.Text.Json.Nodes.JsonValue.Create("original"),
            ["Description"] = System.Text.Json.Nodes.JsonValue.Create("original")
        });
        var scenario = DemoConflictSeeder.CreateDeleteScenario(template);
        var baseline = new SyncSnapshot(
            route.Id, route.DestinationSystem, route.EntityType, DemoConflictSeeder.DeleteEntityId,
            scenario.Baseline, scenario.Baseline);

        var resolution = ConflictResolver.ResolveDelete(
            baseline, scenario.Baseline, scenario.Current, route);

        Assert.True(resolution.IsHeld);
        Assert.False(resolution.ShouldApply);
        Assert.Equal(["Description", "Subject"], resolution.Conflicts.Select(x => x.FieldName));
        Assert.All(resolution.Conflicts, conflict => Assert.Equal("DeleteHeld", conflict.Resolution));
        Assert.Equal(DemoConflictSeeder.DeleteEntityId, scenario.Baseline.Fields["CaseNumber"]!.GetValue<string>());
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
    public void CrmAndNextFieldServiceExposeTheTypedWorkOrderFields()
    {
        string[] typedFields =
        [
            "EstimatedMinutes", "EstimatedCost", "RequiresParts", "WorkNote", "ExternalTrackingId"
        ];

        var fieldServiceFields = ContractFieldsFromSource(
            "demos/FieldService/lib/db.ts",
            "const CONTRACT_FIELDS = [",
            "] as const;");

        Assert.All(typedFields, field => Assert.Contains(field, PropertyNames<CrmWorkOrder>()));
        Assert.All(typedFields, field => Assert.Contains(field, fieldServiceFields));
        Assert.Contains("CaseRef", PropertyNames<CrmWorkOrder>());
        Assert.Contains("CaseNumber", fieldServiceFields);
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

    private static SyncRouteDefinition DemoSupportCaseRoute()
        => DemoRoute(DemoConflictSeeder.EntityType);

    private static SyncRouteDefinition DemoRoute(string entityType)
    {
        var seed = CoordinatorDatabaseInitializer.CreateDemoSeed();
        var routeEntity = seed.Routes.Single(x => x.EntityType == entityType);
        return new SyncRouteDefinition(
            routeEntity.Id,
            routeEntity.Name,
            routeEntity.SourceSystem.Code,
            routeEntity.DestinationSystem.Code,
            routeEntity.EntityType,
            routeEntity.Direction,
            new DeletionBehavior(DeletionMode.Physical),
            new DeletionBehavior(DeletionMode.Physical),
            routeEntity.ConflictScope,
            routeEntity.DefaultConflictPolicy,
            true,
            new Dictionary<string, ConflictPolicy>())
        {
            ValueMappings = routeEntity.TableMapping!.Columns.ToDictionary(
                x => string.IsNullOrWhiteSpace(x.SourceTableAlias)
                    ? x.SourceColumn
                    : $"{x.SourceTableAlias}.{x.SourceColumn}",
                x => new ColumnValueMappingDefinition(
                    string.IsNullOrWhiteSpace(x.SourceTableAlias)
                        ? x.SourceColumn
                        : $"{x.SourceTableAlias}.{x.SourceColumn}",
                    x.DestinationColumn,
                    ColumnValueContract.Unknown,
                    ColumnValueContract.Unknown,
                    new ValueTransformInput(),
                    new ValueTransformInput())
                {
                    Direction = x.Direction ?? SyncFieldDirection.Bidirectional
                },
                StringComparer.Ordinal)
        };
    }

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

    private static string RepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        foreach (var startPath in new[]
                 {
                     Path.GetDirectoryName(sourceFilePath) ?? string.Empty,
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory()
                 })
        {
            if (string.IsNullOrWhiteSpace(startPath)) continue;
            var directory = new DirectoryInfo(startPath);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SyncCoordinator.sln")))
            {
                directory = directory.Parent;
            }
            if (directory is not null)
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("SyncCoordinator repository root was not found.");
    }
}
