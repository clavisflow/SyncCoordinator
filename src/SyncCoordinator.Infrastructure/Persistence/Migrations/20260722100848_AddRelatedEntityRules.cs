using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncCoordinator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRelatedEntityRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RouteColumnMapping_TableMappingId_SourceColumn",
                table: "RouteColumnMapping");

            migrationBuilder.AddColumn<string>(
                name: "Direction",
                table: "RouteColumnMapping",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceTableAlias",
                table: "RouteColumnMapping",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "RouteRelatedTable",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TableMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Schema = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Table = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Alias = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BaseColumn = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RelatedColumn = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Usage = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DetectChanges = table.Column<bool>(type: "bit", nullable: false),
                    ConditionColumn = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ConditionOperator = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ConditionValue = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteRelatedTable", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RouteRelatedTable_RouteTableMapping_TableMappingId",
                        column: x => x.TableMappingId,
                        principalTable: "RouteTableMapping",
                        principalColumn: "RouteId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RouteColumnMapping_TableMappingId_SourceTableAlias_SourceColumn",
                table: "RouteColumnMapping",
                columns: new[] { "TableMappingId", "SourceTableAlias", "SourceColumn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RouteRelatedTable_TableMappingId_Alias",
                table: "RouteRelatedTable",
                columns: new[] { "TableMappingId", "Alias" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RouteRelatedTable");

            migrationBuilder.DropIndex(
                name: "IX_RouteColumnMapping_TableMappingId_SourceTableAlias_SourceColumn",
                table: "RouteColumnMapping");

            migrationBuilder.DropColumn(
                name: "Direction",
                table: "RouteColumnMapping");

            migrationBuilder.DropColumn(
                name: "SourceTableAlias",
                table: "RouteColumnMapping");

            migrationBuilder.CreateIndex(
                name: "IX_RouteColumnMapping_TableMappingId_SourceColumn",
                table: "RouteColumnMapping",
                columns: new[] { "TableMappingId", "SourceColumn" },
                unique: true);
        }
    }
}
