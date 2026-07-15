using SyncCoordinator.Contracts;
using SyncCoordinator.Core;

namespace SyncCoordinator.Tests;

public sealed class ConfigurationValidatorTests
{
    [Theory]
    [InlineData("A", "B")]
    [InlineData("B", "A")]
    public void DirectRouteBetweenDistinctSystemsIsAllowed(string source, string destination)
    {
        var input = ValidRoute();
        input.SourceSystem = source;
        input.DestinationSystem = destination;

        ConfigurationValidator.ValidateRoute(input, ["A", "B", "C"]);
    }

    [Fact]
    public void BidirectionalRouteBetweenAAndCIsAllowed()
    {
        var input = ValidRoute();
        input.Direction = SyncDirection.Bidirectional;

        ConfigurationValidator.ValidateRoute(input, ["A", "B", "C"]);
    }

    [Fact]
    public void PasswordIsRequiredForNewSqlLoginConnection()
    {
        var input = new DatabaseConnectionInput
        {
            Server = "db-host",
            Database = "business",
            UserName = "sync-user"
        };

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ConfigurationValidator.ValidateConnection(input, "SqlServer"));

        Assert.Contains(exception.Errors, x => x.Contains("パスワード", StringComparison.Ordinal));
    }

    [Fact]
    public void StoredPasswordCanBeKeptWhenConnectionIsUpdated()
    {
        var input = new DatabaseConnectionInput
        {
            Server = "db-host",
            Database = "business",
            UserName = "sync-user",
            HasStoredPassword = true
        };

        ConfigurationValidator.ValidateConnection(input, "SqlServer");
    }

    [Fact]
    public void TableMappingRequiresAKeyColumn()
    {
        var input = new TableMappingInput
        {
            RouteId = Guid.NewGuid(),
            SourceSchema = "dbo",
            SourceTable = "Source",
            DestinationSchema = "dbo",
            DestinationTable = "Destination",
            Columns = [new ColumnMappingInput { SourceColumn = "Id", DestinationColumn = "Id" }]
        };

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ConfigurationValidator.ValidateTableMapping(input, ValidRoute()));

        Assert.Contains(exception.Errors, x => x.Contains("キー列", StringComparison.Ordinal));
    }

    [Fact]
    public void DirectionalFixedValuesAreAllowedForBidirectionalRule()
    {
        var route = ValidRoute();
        route.Direction = SyncDirection.Bidirectional;
        var input = ValidTableMapping();
        input.FixedValues =
        [
            new FixedValueMappingInput
            {
                Direction = MappingWriteDirection.Forward,
                TargetColumn = "UpdatedUserId",
                Value = "0"
            },
            new FixedValueMappingInput
            {
                Direction = MappingWriteDirection.Reverse,
                TargetColumn = "UpdatedUserId",
                Value = "SYNC_COORDINATOR"
            }
        ];

        ConfigurationValidator.ValidateTableMapping(input, route);
    }

    [Fact]
    public void ReverseFixedValueIsRejectedForOneWayRule()
    {
        var input = ValidTableMapping();
        input.FixedValues =
        [
            new FixedValueMappingInput
            {
                Direction = MappingWriteDirection.Reverse,
                TargetColumn = "UpdatedUserId",
                Value = "SYNC_COORDINATOR"
            }
        ];

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ConfigurationValidator.ValidateTableMapping(input, ValidRoute()));

        Assert.Contains(exception.Errors, x => x.Contains("片方向", StringComparison.Ordinal));
    }

    [Fact]
    public void FixedValueCannotOverwriteNormallyMappedColumn()
    {
        var input = ValidTableMapping();
        input.FixedValues =
        [
            new FixedValueMappingInput
            {
                Direction = MappingWriteDirection.Forward,
                TargetColumn = "Id",
                Value = "0"
            }
        ];

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ConfigurationValidator.ValidateTableMapping(input, ValidRoute()));

        Assert.Contains(exception.Errors, x => x.Contains("通常の列マッピング", StringComparison.Ordinal));
    }

    [Fact]
    public void LogicalDeletionRequiresColumnAndValue()
    {
        var input = ValidTableMapping();
        input.SyncDeletes = true;
        input.SourceDeletionMode = DeletionMode.Logical;

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ConfigurationValidator.ValidateTableMapping(input, ValidRoute()));

        Assert.Contains(exception.Errors, x => x.Contains("論理削除列", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, x => x.Contains("削除時の値", StringComparison.Ordinal));
    }

    [Fact]
    public void PhysicalAndLogicalDeletionCanBeCombined()
    {
        var input = ValidTableMapping();
        input.SyncDeletes = true;
        input.SourceDeletionMode = DeletionMode.Physical;
        input.DestinationDeletionMode = DeletionMode.Logical;
        input.DestinationLogicalDeleteColumn = "IsDeleted";
        input.DestinationLogicalDeleteValue = "1";

        ConfigurationValidator.ValidateTableMapping(input, ValidRoute());
    }

    [Fact]
    public void KeyColumnsCannotUseValueTransformations()
    {
        var input = ValidTableMapping();
        input.Columns[0].ForwardTransform.UseNullFallback = true;
        input.Columns[0].ForwardTransform.NullFallback = "0";

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ConfigurationValidator.ValidateTableMapping(input, ValidRoute()));

        Assert.Contains(exception.Errors, error => error.Contains("キー列", StringComparison.Ordinal));
    }

    [Fact]
    public void FixedValuesMustFitTheSelectedTargetContract()
    {
        var input = ValidTableMapping();
        input.FixedValues =
        [
            new FixedValueMappingInput
            {
                Direction = MappingWriteDirection.Forward,
                TargetColumn = "SourceCode",
                Value = "too-long",
                TargetContract = new ColumnValueContract("varchar", false, 3, null, null)
            }
        ];

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ConfigurationValidator.ValidateTableMapping(input, ValidRoute()));

        Assert.Contains(exception.Errors, error => error.Contains("列制約", StringComparison.Ordinal));
    }

    private static TableMappingInput ValidTableMapping() => new()
    {
        RouteId = Guid.NewGuid(),
        SourceSchema = "dbo",
        SourceTable = "Source",
        DestinationSchema = "dbo",
        DestinationTable = "Destination",
        Columns =
        [
            new ColumnMappingInput
            {
                SourceColumn = "Id",
                DestinationColumn = "Id",
                IsKey = true
            }
        ]
    };

    private static RouteConfigurationInput ValidRoute() => new()
    {
        Name = "A to C",
        SourceSystem = "A",
        DestinationSystem = "C",
        Direction = SyncDirection.OneWay,
        ConflictScope = ConflictScope.Field,
        DefaultConflictPolicy = ConflictPolicy.HoldAndNotify,
        Enabled = true
    };
}
