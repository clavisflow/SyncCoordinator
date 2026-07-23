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
    public void SourceConditionSupportsTheSourcePlaceholder()
    {
        var input = ValidTableMapping();
        input.SourceConditionExpression = "{source}.Status = 'Active'";

        ConfigurationValidator.ValidateTableMapping(input, ValidRoute());
    }

    [Theory]
    [InlineData("{source}.Status = 'Active'; DELETE FROM Source", "更新系SQL")]
    [InlineData("{source}.Status = 'Active' -- bypass", "更新系SQL")]
    [InlineData("{related}.Enabled = 1", "プレースホルダー")]
    public void SourceConditionRejectsUnsafeSqlAndUnknownPlaceholders(string expression, string errorFragment)
    {
        var input = ValidTableMapping();
        input.SourceConditionExpression = expression;

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ConfigurationValidator.ValidateTableMapping(input, ValidRoute()));

        Assert.Contains(exception.Errors, x => x.Contains(errorFragment, StringComparison.Ordinal));
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
    public void OneWayRuleRejectsReverseFieldDirection()
    {
        var input = ValidTableMapping();
        input.Columns[0].Direction = SyncFieldDirection.Reverse;

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ConfigurationValidator.ValidateTableMapping(input, ValidRoute()));

        Assert.Contains(exception.Errors, x => x.Contains("正方向", StringComparison.Ordinal));
    }

    [Fact]
    public void BidirectionalRuleAllowsSourceOwnedField()
    {
        var route = ValidRoute();
        route.Direction = SyncDirection.Bidirectional;
        var input = ValidTableMapping();
        input.Columns[0].Direction = SyncFieldDirection.Bidirectional;
        input.Columns.Add(new ColumnMappingInput
        {
            SourceColumn = "ReceptionName",
            DestinationColumn = "ReceptionName",
            Direction = SyncFieldDirection.Forward
        });

        ConfigurationValidator.ValidateTableMapping(input, route);
    }

    [Fact]
    public void RelatedProjectionFieldMustBeForwardOnly()
    {
        var route = ValidRoute();
        route.Direction = SyncDirection.Bidirectional;
        var input = ValidTableMapping();
        input.Columns[0].Direction = SyncFieldDirection.Bidirectional;
        input.RelatedTables.Add(new RelatedTableInput
        {
            Schema = "dbo",
            Table = "Reception",
            Alias = "reception",
            JoinExpression = "{source}.ReceptionId = {related}.Id",
            Usage = RelatedTableUsage.Projection
        });
        input.Columns.Add(new ColumnMappingInput
        {
            SourceTableAlias = "reception",
            SourceColumn = "ReceptionName",
            DestinationColumn = "ReceptionName",
            Direction = SyncFieldDirection.Bidirectional
        });

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ConfigurationValidator.ValidateTableMapping(input, route));

        Assert.Contains(exception.Errors, x => x.Contains("片方向", StringComparison.Ordinal));
    }

    [Fact]
    public void EligibilityTableSupportsStaffNumberCondition()
    {
        var input = ValidTableMapping();
        input.RelatedTables.Add(new RelatedTableInput
        {
            Schema = "dbo",
            Table = "WorkRequestStaff",
            Alias = "staff",
            JoinExpression = "{source}.Id = {related}.WorkRequestId",
            Usage = RelatedTableUsage.Eligibility,
            DetectChanges = true,
            ConditionExpression = "{related}.StaffNo IS NOT NULL AND {source}.Enabled = 1"
        });

        ConfigurationValidator.ValidateTableMapping(input, ValidRoute());
    }

    [Theory]
    [InlineData("{related}.StaffNo IS NOT NULL; DELETE FROM Staff")]
    [InlineData("{related}.StaffNo IS NOT NULL -- bypass")]
    [InlineData("{related}.StaffNo IS NOT NULL /* bypass */")]
    public void RelatedConditionRejectsMultipleStatementsCommentsAndMutationSql(string expression)
    {
        var input = ValidTableMapping();
        input.RelatedTables.Add(new RelatedTableInput
        {
            Schema = "dbo",
            Table = "WorkRequestStaff",
            Alias = "staff",
            JoinExpression = "{source}.Id = {related}.WorkRequestId",
            Usage = RelatedTableUsage.Eligibility,
            ConditionExpression = expression
        });

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ConfigurationValidator.ValidateTableMapping(input, ValidRoute()));

        Assert.Contains(exception.Errors, x => x.Contains("更新系SQL", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{source}.Id = WorkRequestId")]
    [InlineData("Id = {related}.WorkRequestId")]
    public void RelatedJoinRequiresSourceAndRelatedPlaceholders(string expression)
    {
        var input = ValidTableMapping();
        input.RelatedTables.Add(new RelatedTableInput
        {
            Schema = "dbo",
            Table = "WorkRequestStaff",
            Alias = "staff",
            JoinExpression = expression,
            Usage = RelatedTableUsage.Eligibility
        });

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ConfigurationValidator.ValidateTableMapping(input, ValidRoute()));

        Assert.Contains(exception.Errors, x => x.Contains("両方を参照", StringComparison.Ordinal));
    }

    [Fact]
    public void RelatedExpressionRejectsUnknownPlaceholder()
    {
        var input = ValidTableMapping();
        input.RelatedTables.Add(new RelatedTableInput
        {
            Schema = "dbo",
            Table = "WorkRequestStaff",
            Alias = "staff",
            JoinExpression = "{source}.Id = {related}.WorkRequestId",
            Usage = RelatedTableUsage.Eligibility,
            ConditionExpression = "{other}.Enabled = 1"
        });

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ConfigurationValidator.ValidateTableMapping(input, ValidRoute()));

        Assert.Contains(exception.Errors, x => x.Contains("プレースホルダー", StringComparison.Ordinal));
    }

    [Fact]
    public void ProjectionTableRequiresAtLeastOneProjectedField()
    {
        var input = ValidTableMapping();
        input.RelatedTables.Add(new RelatedTableInput
        {
            Schema = "dbo",
            Table = "Reception",
            Alias = "reception",
            JoinExpression = "{source}.ReceptionId = {related}.Id",
            Usage = RelatedTableUsage.Projection
        });

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ConfigurationValidator.ValidateTableMapping(input, ValidRoute()));

        Assert.Contains(exception.Errors, x => x.Contains("同期列を1件以上", StringComparison.Ordinal));
    }

    [Fact]
    public void RelatedTableAliasCannotUseConnectorBaseAlias()
    {
        var input = ValidTableMapping();
        input.RelatedTables.Add(new RelatedTableInput
        {
            Schema = "dbo",
            Table = "WorkRequestStaff",
            Alias = "sc_base",
            JoinExpression = "{source}.Id = {related}.WorkRequestId",
            Usage = RelatedTableUsage.Eligibility
        });

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ConfigurationValidator.ValidateTableMapping(input, ValidRoute()));

        Assert.Contains(exception.Errors, x => x.Contains("予約名", StringComparison.Ordinal));
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
