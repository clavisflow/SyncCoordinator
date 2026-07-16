using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncCoordinator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddManualConflictResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SyncConflict_ResolvedAtUtc_DetectedAtUtc",
                table: "SyncConflict");

            migrationBuilder.AddColumn<string>(
                name: "BaselineDestinationPayloadJson",
                table: "SyncConflict",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BaselineSourcePayloadJson",
                table: "SyncConflict",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentPayloadJson",
                table: "SyncConflict",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HadBaseline",
                table: "SyncConflict",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "IncomingPayloadJson",
                table: "SyncConflict",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "Operation",
                table: "SyncConflict",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Upsert");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RequestedAtUtc",
                table: "SyncConflict",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestedBy",
                table: "SyncConflict",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResolutionAttemptCount",
                table: "SyncConflict",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ResolutionComment",
                table: "SyncConflict",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolutionLastError",
                table: "SyncConflict",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolutionLockedUntilUtc",
                table: "SyncConflict",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolutionRequestJson",
                table: "SyncConflict",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolutionState",
                table: "SyncConflict",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Resolved");

            migrationBuilder.AddColumn<string>(
                name: "ResolvedBy",
                table: "SyncConflict",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "SyncConflict",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: Array.Empty<byte>());

            // 既存行は解決に必要な発生時点の全payloadを保持していないため、
            // 安全に再適用できない履歴として扱う。新規競合だけを手動解決対象にする。
            migrationBuilder.Sql("""
                UPDATE [SyncConflict]
                SET [ResolvedAtUtc] = COALESCE([ResolvedAtUtc], [DetectedAtUtc]),
                    [ResolvedBy] = COALESCE([ResolvedBy], N'legacy-history');
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflict_ResolutionState_DetectedAtUtc",
                table: "SyncConflict",
                columns: new[] { "ResolutionState", "DetectedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SyncConflict_ResolutionState_DetectedAtUtc",
                table: "SyncConflict");

            migrationBuilder.DropColumn(
                name: "BaselineDestinationPayloadJson",
                table: "SyncConflict");

            migrationBuilder.DropColumn(
                name: "BaselineSourcePayloadJson",
                table: "SyncConflict");

            migrationBuilder.DropColumn(
                name: "CurrentPayloadJson",
                table: "SyncConflict");

            migrationBuilder.DropColumn(
                name: "HadBaseline",
                table: "SyncConflict");

            migrationBuilder.DropColumn(
                name: "IncomingPayloadJson",
                table: "SyncConflict");

            migrationBuilder.DropColumn(
                name: "Operation",
                table: "SyncConflict");

            migrationBuilder.DropColumn(
                name: "RequestedAtUtc",
                table: "SyncConflict");

            migrationBuilder.DropColumn(
                name: "RequestedBy",
                table: "SyncConflict");

            migrationBuilder.DropColumn(
                name: "ResolutionAttemptCount",
                table: "SyncConflict");

            migrationBuilder.DropColumn(
                name: "ResolutionComment",
                table: "SyncConflict");

            migrationBuilder.DropColumn(
                name: "ResolutionLastError",
                table: "SyncConflict");

            migrationBuilder.DropColumn(
                name: "ResolutionLockedUntilUtc",
                table: "SyncConflict");

            migrationBuilder.DropColumn(
                name: "ResolutionRequestJson",
                table: "SyncConflict");

            migrationBuilder.DropColumn(
                name: "ResolutionState",
                table: "SyncConflict");

            migrationBuilder.DropColumn(
                name: "ResolvedBy",
                table: "SyncConflict");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "SyncConflict");

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflict_ResolvedAtUtc_DetectedAtUtc",
                table: "SyncConflict",
                columns: new[] { "ResolvedAtUtc", "DetectedAtUtc" });
        }
    }
}
