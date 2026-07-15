using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncCoordinator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddManagementSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InboxMessage_State",
                table: "InboxMessage");

            migrationBuilder.CreateTable(
                name: "ManagementSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    GlobalPaused = table.Column<bool>(type: "bit", nullable: false),
                    PollingIntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    BatchSize = table.Column<int>(type: "int", nullable: false),
                    CompletedInboxRetentionDays = table.Column<int>(type: "int", nullable: false),
                    DeliveredWebhookRetentionDays = table.Column<int>(type: "int", nullable: false),
                    FailedWebhookRetentionDays = table.Column<int>(type: "int", nullable: false),
                    AcknowledgedOperationalEventRetentionDays = table.Column<int>(type: "int", nullable: false),
                    ResolvedConflictRetentionDays = table.Column<int>(type: "int", nullable: false),
                    ConfigurationAuditRetentionDays = table.Column<int>(type: "int", nullable: false),
                    LastAutomaticCleanupAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastManualCleanupAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AutomaticCleanupLeaseUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagementSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "ManagementSettings",
                columns:
                [
                    "Id",
                    "GlobalPaused",
                    "PollingIntervalSeconds",
                    "BatchSize",
                    "CompletedInboxRetentionDays",
                    "DeliveredWebhookRetentionDays",
                    "FailedWebhookRetentionDays",
                    "AcknowledgedOperationalEventRetentionDays",
                    "ResolvedConflictRetentionDays",
                    "ConfigurationAuditRetentionDays",
                    "UpdatedAtUtc"
                ],
                values:
                [
                    1,
                    false,
                    5,
                    100,
                    90,
                    30,
                    90,
                    90,
                    180,
                    365,
                    DateTimeOffset.UnixEpoch
                ]);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDelivery_State_DeliveredAtUtc",
                table: "WebhookDelivery",
                columns: new[] { "State", "DeliveredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDelivery_State_LastAttemptAtUtc",
                table: "WebhookDelivery",
                columns: new[] { "State", "LastAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessage_State_UpdatedAtUtc",
                table: "InboxMessage",
                columns: new[] { "State", "UpdatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManagementSettings");

            migrationBuilder.DropIndex(
                name: "IX_WebhookDelivery_State_DeliveredAtUtc",
                table: "WebhookDelivery");

            migrationBuilder.DropIndex(
                name: "IX_WebhookDelivery_State_LastAttemptAtUtc",
                table: "WebhookDelivery");

            migrationBuilder.DropIndex(
                name: "IX_InboxMessage_State_UpdatedAtUtc",
                table: "InboxMessage");

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessage_State",
                table: "InboxMessage",
                column: "State");
        }
    }
}
