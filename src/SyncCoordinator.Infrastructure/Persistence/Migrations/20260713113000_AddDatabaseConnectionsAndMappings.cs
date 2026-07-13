using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncCoordinator.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CoordinatorDbContext))]
[Migration("20260713113000_AddDatabaseConnectionsAndMappings")]
public sealed class AddDatabaseConnectionsAndMappings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ConnectionUpdatedAtUtc",
            table: "SystemDefinition",
            type: "datetimeoffset",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProtectedConnectionString",
            table: "SystemDefinition",
            type: "nvarchar(max)",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "RouteTableMapping",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DestinationSystem = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                SourceSchema = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                SourceTable = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                DestinationSchema = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                DestinationTable = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RouteTableMapping", x => x.Id);
                table.ForeignKey(
                    name: "FK_RouteTableMapping_SyncRoute_RouteId",
                    column: x => x.RouteId,
                    principalTable: "SyncRoute",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "RouteColumnMapping",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TableMappingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SourceColumn = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                DestinationColumn = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                IsKey = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RouteColumnMapping", x => x.Id);
                table.ForeignKey(
                    name: "FK_RouteColumnMapping_RouteTableMapping_TableMappingId",
                    column: x => x.TableMappingId,
                    principalTable: "RouteTableMapping",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_RouteTableMapping_RouteId_DestinationSystem",
            table: "RouteTableMapping",
            columns: new[] { "RouteId", "DestinationSystem" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RouteColumnMapping_TableMappingId_DestinationColumn",
            table: "RouteColumnMapping",
            columns: new[] { "TableMappingId", "DestinationColumn" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RouteColumnMapping_TableMappingId_SourceColumn",
            table: "RouteColumnMapping",
            columns: new[] { "TableMappingId", "SourceColumn" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "RouteColumnMapping");
        migrationBuilder.DropTable(name: "RouteTableMapping");
        migrationBuilder.DropColumn(name: "ConnectionUpdatedAtUtc", table: "SystemDefinition");
        migrationBuilder.DropColumn(name: "ProtectedConnectionString", table: "SystemDefinition");
    }
}
