using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncCoordinator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddColumnMappingDisplayOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DisplayOrder",
                table: "RouteColumnMapping",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                WITH [OrderedColumns] AS
                (
                    SELECT
                        [Id],
                        ROW_NUMBER() OVER (
                            PARTITION BY [TableMappingId]
                            ORDER BY [SourceColumn], [Id]) - 1 AS [DisplayOrder]
                    FROM [RouteColumnMapping]
                )
                UPDATE [ColumnMapping]
                SET [DisplayOrder] = [Ordered].[DisplayOrder]
                FROM [RouteColumnMapping] AS [ColumnMapping]
                INNER JOIN [OrderedColumns] AS [Ordered] ON [Ordered].[Id] = [ColumnMapping].[Id];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayOrder",
                table: "RouteColumnMapping");
        }
    }
}
