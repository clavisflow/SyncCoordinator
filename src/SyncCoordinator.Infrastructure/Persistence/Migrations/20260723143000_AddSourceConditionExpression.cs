using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncCoordinator.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CoordinatorDbContext))]
[Migration("20260723143000_AddSourceConditionExpression")]
public sealed class AddSourceConditionExpression : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SourceConditionExpression",
            table: "RouteTableMapping",
            type: "nvarchar(4000)",
            maxLength: 4000,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "SourceConditionExpression",
            table: "RouteTableMapping");
    }
}
