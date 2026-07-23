using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncCoordinator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceRelatedJoinWithExpression : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "JoinExpression",
                table: "RouteRelatedTable",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE RouteRelatedTable
                SET JoinExpression = '{source}.' + BaseColumn + ' = {related}.' + RelatedColumn;

                UPDATE SyncRoute
                SET DeploymentState = 'Draft', Enabled = 0
                WHERE Id IN
                (
                    SELECT TableMappingId
                    FROM RouteRelatedTable
                    WHERE DetectChanges = 1
                );
                """);

            migrationBuilder.DropColumn(
                name: "BaseColumn",
                table: "RouteRelatedTable");

            migrationBuilder.DropColumn(
                name: "RelatedColumn",
                table: "RouteRelatedTable");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JoinExpression",
                table: "RouteRelatedTable");

            migrationBuilder.AddColumn<string>(
                name: "BaseColumn",
                table: "RouteRelatedTable",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RelatedColumn",
                table: "RouteRelatedTable",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");
        }
    }
}
