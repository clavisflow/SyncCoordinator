using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncCoordinator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCoordinatorSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InboxMessage",
                columns: table => new
                {
                    SourceMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DestinationSystem = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    FirstSeenAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LockedUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessage", x => new { x.SourceMessageId, x.RouteId, x.DestinationSystem });
                });

            migrationBuilder.CreateTable(
                name: "QueueCheckpoint",
                columns: table => new
                {
                    SystemCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LastQueueId = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueCheckpoint", x => x.SystemCode);
                });

            migrationBuilder.CreateTable(
                name: "SyncRoute",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DestinationMode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DestinationSystem = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ConflictScope = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DefaultConflictPolicy = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRoute", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncSnapshot",
                columns: table => new
                {
                    RouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DestinationSystem = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncSnapshot", x => new { x.RouteId, x.DestinationSystem, x.EntityType, x.EntityId });
                });

            migrationBuilder.CreateTable(
                name: "SystemDefinition",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemDefinition", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RouteFieldPolicy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FieldName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Policy = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteFieldPolicy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RouteFieldPolicy_SyncRoute_RouteId",
                        column: x => x.RouteId,
                        principalTable: "SyncRoute",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncConflict",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DestinationSystem = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DetectedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncConflict", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncConflict_SyncRoute_RouteId",
                        column: x => x.RouteId,
                        principalTable: "SyncRoute",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessage_State",
                table: "InboxMessage",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_RouteFieldPolicy_RouteId_FieldName",
                table: "RouteFieldPolicy",
                columns: new[] { "RouteId", "FieldName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflict_ResolvedAtUtc_DetectedAtUtc",
                table: "SyncConflict",
                columns: new[] { "ResolvedAtUtc", "DetectedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflict_RouteId",
                table: "SyncConflict",
                column: "RouteId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRoute_SourceSystem_EntityType_Enabled",
                table: "SyncRoute",
                columns: new[] { "SourceSystem", "EntityType", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_SystemDefinition_Code",
                table: "SystemDefinition",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxMessage");

            migrationBuilder.DropTable(
                name: "QueueCheckpoint");

            migrationBuilder.DropTable(
                name: "RouteFieldPolicy");

            migrationBuilder.DropTable(
                name: "SyncConflict");

            migrationBuilder.DropTable(
                name: "SyncSnapshot");

            migrationBuilder.DropTable(
                name: "SystemDefinition");

            migrationBuilder.DropTable(
                name: "SyncRoute");
        }
    }
}
