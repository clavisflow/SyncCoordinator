using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncCoordinator.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CoordinatorDbContext))]
[Migration("20260723090000_AddFixedValueKeys")]
public sealed class AddFixedValueKeys : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsKey",
            table: "RouteFixedValueMapping",
            type: "bit",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "IsKey",
            table: "RouteFixedValueMapping");
    }
}
