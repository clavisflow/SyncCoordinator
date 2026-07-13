using SyncCoordinator.Core;

namespace SyncCoordinator.Tests;

public sealed class ConfigurationValidatorTests
{
    [Theory]
    [InlineData("A", "B")]
    [InlineData("B", "A")]
    public void DirectRouteBetweenAAndBIsRejected(string source, string destination)
    {
        var input = ValidRoute();
        input.SourceSystem = source;
        input.DestinationSystem = destination;

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ConfigurationValidator.ValidateRoute(input, ["A", "B", "C"]));

        Assert.Contains(exception.Errors, x => x.Contains("直接同期", StringComparison.Ordinal));
    }

    [Fact]
    public void OriginSystemRouteFromCIsAllowed()
    {
        var input = ValidRoute();
        input.SourceSystem = "C";
        input.DestinationMode = DestinationMode.OriginSystem;
        input.DestinationSystem = null;

        ConfigurationValidator.ValidateRoute(input, ["A", "B", "C"]);
    }

    [Fact]
    public void DuplicateFieldPoliciesAreRejected()
    {
        var input = ValidRoute();
        input.FieldPolicies =
        [
            new FieldPolicyInput { FieldName = "Status" },
            new FieldPolicyInput { FieldName = "Status" }
        ];

        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            ConfigurationValidator.ValidateRoute(input, ["A", "B", "C"]));

        Assert.Contains(exception.Errors, x => x.Contains("重複", StringComparison.Ordinal));
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
            DestinationSystem = "C",
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

    private static RouteConfigurationInput ValidRoute() => new()
    {
        Name = "A to C",
        SourceSystem = "A",
        EntityType = "SampleWorkRequest",
        DestinationMode = DestinationMode.FixedSystem,
        DestinationSystem = "C",
        ConflictScope = ConflictScope.Field,
        DefaultConflictPolicy = ConflictPolicy.HoldAndNotify,
        Enabled = true
    };
}
