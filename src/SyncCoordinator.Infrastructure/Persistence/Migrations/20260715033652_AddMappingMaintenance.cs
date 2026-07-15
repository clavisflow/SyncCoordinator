using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncCoordinator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMappingMaintenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MappingMaintenanceStartedAtUtc",
                table: "SyncRoute",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncRoute_MappingMaintenanceStartedAtUtc_EntityType",
                table: "SyncRoute",
                columns: new[] { "MappingMaintenanceStartedAtUtc", "EntityType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SyncRoute_MappingMaintenanceStartedAtUtc_EntityType",
                table: "SyncRoute");

            migrationBuilder.DropColumn(
                name: "MappingMaintenanceStartedAtUtc",
                table: "SyncRoute");
        }
    }
}
