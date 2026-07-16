using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncCoordinator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HybridConflictChainResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SyncConflict_ActiveRecord",
                table: "SyncConflict");

            migrationBuilder.AddColumn<Guid>(
                name: "PreviousConflictId",
                table: "SyncConflict",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflict_RecordChain",
                table: "SyncConflict",
                columns: new[] { "RouteId", "DestinationSystem", "EntityType", "EntityId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SyncConflict_RecordChain",
                table: "SyncConflict");

            // 旧バージョンの一意制約へ戻せるよう、同一レコードの有効な競合を
            // 最新1件に集約する。古い競合と対応するInboxは履歴を残して後続優先にする。
            migrationBuilder.Sql(
                """
                ;WITH [RankedActive] AS
                (
                    SELECT [Id],
                           ROW_NUMBER() OVER
                           (
                               PARTITION BY [RouteId], [DestinationSystem], [EntityType], [EntityId]
                               ORDER BY [DetectedAtUtc] DESC, [Id] DESC
                           ) AS [Rank]
                    FROM [SyncConflict]
                    WHERE [ResolutionState] IN
                          (N'AwaitingDecision', N'Pending', N'Processing', N'Failed', N'WaitingForPrevious')
                ),
                [Superseded] AS
                (
                    SELECT [Id]
                    FROM [RankedActive]
                    WHERE [Rank] > 1
                )
                UPDATE [Conflict]
                SET [ResolutionState] = N'Superseded',
                    [SupersededAtUtc] = SYSUTCDATETIME(),
                    [ResolutionRequestJson] = NULL,
                    [ResolutionLockedUntilUtc] = NULL
                FROM [SyncConflict] AS [Conflict]
                INNER JOIN [Superseded] ON [Superseded].[Id] = [Conflict].[Id];

                UPDATE [Inbox]
                SET [State] = N'Superseded',
                    [UpdatedAtUtc] = SYSUTCDATETIME(),
                    [LockedUntilUtc] = NULL
                FROM [InboxMessage] AS [Inbox]
                INNER JOIN [SyncConflict] AS [Conflict]
                    ON [Conflict].[SourceMessageId] = [Inbox].[SourceMessageId]
                   AND [Conflict].[RouteId] = [Inbox].[RouteId]
                   AND [Conflict].[DestinationSystem] = [Inbox].[DestinationSystem]
                WHERE [Conflict].[ResolutionState] = N'Superseded'
                  AND [Conflict].[SupersededAtUtc] IS NOT NULL;

                UPDATE [Conflict]
                SET [ResolutionState] = N'AwaitingDecision'
                FROM [SyncConflict] AS [Conflict]
                WHERE [Conflict].[ResolutionState] = N'WaitingForPrevious';

                UPDATE [Inbox]
                SET [State] = N'Held',
                    [UpdatedAtUtc] = SYSUTCDATETIME(),
                    [LockedUntilUtc] = NULL
                FROM [InboxMessage] AS [Inbox]
                INNER JOIN [SyncConflict] AS [Conflict]
                    ON [Conflict].[SourceMessageId] = [Inbox].[SourceMessageId]
                   AND [Conflict].[RouteId] = [Inbox].[RouteId]
                   AND [Conflict].[DestinationSystem] = [Inbox].[DestinationSystem]
                WHERE [Conflict].[ResolutionState] = N'AwaitingDecision'
                  AND [Inbox].[State] = N'WaitingForPrevious';
                """);

            migrationBuilder.DropColumn(
                name: "PreviousConflictId",
                table: "SyncConflict");

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflict_ActiveRecord",
                table: "SyncConflict",
                columns: new[] { "RouteId", "DestinationSystem", "EntityType", "EntityId" },
                unique: true,
                filter: "[ResolutionState] IN (N'AwaitingDecision', N'Pending', N'Processing', N'Failed')");
        }
    }
}
