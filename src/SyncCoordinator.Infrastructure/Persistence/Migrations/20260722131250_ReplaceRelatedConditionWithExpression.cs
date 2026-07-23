using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncCoordinator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceRelatedConditionWithExpression : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConditionExpression",
                table: "RouteRelatedTable",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE RouteRelatedTable
                SET ConditionExpression = CASE ConditionOperator
                    WHEN 'IsNotNull' THEN '{related}.' + ConditionColumn + ' IS NOT NULL'
                    WHEN 'IsNull' THEN '{related}.' + ConditionColumn + ' IS NULL'
                    WHEN 'Equals' THEN '{related}.' + ConditionColumn + ' = ''' + REPLACE(COALESCE(ConditionValue, ''), '''', '''''') + ''''
                    WHEN 'NotEquals' THEN '{related}.' + ConditionColumn + ' <> ''' + REPLACE(COALESCE(ConditionValue, ''), '''', '''''') + ''''
                    ELSE NULL
                END;
                """);

            migrationBuilder.DropColumn(
                name: "ConditionColumn",
                table: "RouteRelatedTable");

            migrationBuilder.DropColumn(
                name: "ConditionOperator",
                table: "RouteRelatedTable");

            migrationBuilder.DropColumn(
                name: "ConditionValue",
                table: "RouteRelatedTable");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConditionColumn",
                table: "RouteRelatedTable",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConditionOperator",
                table: "RouteRelatedTable",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "ConditionValue",
                table: "RouteRelatedTable",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.DropColumn(
                name: "ConditionExpression",
                table: "RouteRelatedTable");
        }
    }
}
