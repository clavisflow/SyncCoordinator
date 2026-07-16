using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncCoordinator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupersedeOverlappingConflicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SyncConflict_RouteId",
                table: "SyncConflict");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupersededAtUtc",
                table: "SyncConflict",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersededByConflictId",
                table: "SyncConflict",
                type: "uniqueidentifier",
                nullable: true);

            // 同じ同期先レコードに複数の未完了競合が既にある場合は、
            // 検出日時が最新の1件だけを操作対象として残す。
            migrationBuilder.Sql("""
                ;WITH [RankedActiveConflicts] AS
                (
                    SELECT
                        [Id],
                        ROW_NUMBER() OVER (
                            PARTITION BY [RouteId], [DestinationSystem], [EntityType], [EntityId]
                            ORDER BY [DetectedAtUtc] DESC, [Id] DESC) AS [RowNumber],
                        FIRST_VALUE([Id]) OVER (
                            PARTITION BY [RouteId], [DestinationSystem], [EntityType], [EntityId]
                            ORDER BY [DetectedAtUtc] DESC, [Id] DESC) AS [LatestId]
                    FROM [SyncConflict]
                    WHERE [ResolutionState] IN (N'AwaitingDecision', N'Pending', N'Processing', N'Failed')
                )
                UPDATE [conflict]
                SET [ResolutionState] = N'Superseded',
                    [SupersededByConflictId] = [ranked].[LatestId],
                    [SupersededAtUtc] = SYSDATETIMEOFFSET(),
                    [ResolutionRequestJson] = NULL,
                    [ResolutionLockedUntilUtc] = NULL
                FROM [SyncConflict] AS [conflict]
                INNER JOIN [RankedActiveConflicts] AS [ranked] ON [conflict].[Id] = [ranked].[Id]
                WHERE [ranked].[RowNumber] > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflict_ActiveRecord",
                table: "SyncConflict",
                columns: new[] { "RouteId", "DestinationSystem", "EntityType", "EntityId" },
                unique: true,
                filter: "[ResolutionState] IN (N'AwaitingDecision', N'Pending', N'Processing', N'Failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SyncConflict_ActiveRecord",
                table: "SyncConflict");

            migrationBuilder.DropColumn(
                name: "SupersededAtUtc",
                table: "SyncConflict");

            migrationBuilder.DropColumn(
                name: "SupersededByConflictId",
                table: "SyncConflict");

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflict_RouteId",
                table: "SyncConflict",
                column: "RouteId");
        }
    }
}
