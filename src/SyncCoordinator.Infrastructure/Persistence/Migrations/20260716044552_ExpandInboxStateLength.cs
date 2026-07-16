using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncCoordinator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExpandInboxStateLength : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "State",
                table: "InboxMessage",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(16)",
                oldMaxLength: 16);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 16文字の旧列へ戻す前に、旧版で表現できない待機状態を安全な保留へ戻す。
            migrationBuilder.Sql(
                """
                UPDATE [Conflict]
                SET [ResolutionState] = N'AwaitingDecision'
                FROM [SyncConflict] AS [Conflict]
                WHERE [Conflict].[ResolutionState] = N'WaitingForPrevious';

                UPDATE [Inbox]
                SET [State] = N'Held',
                    [UpdatedAtUtc] = SYSUTCDATETIME(),
                    [LockedUntilUtc] = NULL
                FROM [InboxMessage] AS [Inbox]
                WHERE [Inbox].[State] = N'WaitingForPrevious';
                """);

            migrationBuilder.AlterColumn<string>(
                name: "State",
                table: "InboxMessage",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(24)",
                oldMaxLength: 24);
        }
    }
}
