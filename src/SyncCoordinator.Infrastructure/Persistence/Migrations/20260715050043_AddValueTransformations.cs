using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SyncCoordinator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddValueTransformations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TargetDataType",
                table: "RouteFixedValueMapping",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "TargetIsNullable",
                table: "RouteFixedValueMapping",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetMaxLength",
                table: "RouteFixedValueMapping",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetNumericPrecision",
                table: "RouteFixedValueMapping",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetNumericScale",
                table: "RouteFixedValueMapping",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DestinationDataType",
                table: "RouteColumnMapping",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "DestinationIsNullable",
                table: "RouteColumnMapping",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "DestinationMaxLength",
                table: "RouteColumnMapping",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DestinationNumericPrecision",
                table: "RouteColumnMapping",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DestinationNumericScale",
                table: "RouteColumnMapping",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ForwardTransformJson",
                table: "RouteColumnMapping",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReverseTransformJson",
                table: "RouteColumnMapping",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceDataType",
                table: "RouteColumnMapping",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "SourceIsNullable",
                table: "RouteColumnMapping",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceMaxLength",
                table: "RouteColumnMapping",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceNumericPrecision",
                table: "RouteColumnMapping",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceNumericScale",
                table: "RouteColumnMapping",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetDataType",
                table: "RouteFixedValueMapping");

            migrationBuilder.DropColumn(
                name: "TargetIsNullable",
                table: "RouteFixedValueMapping");

            migrationBuilder.DropColumn(
                name: "TargetMaxLength",
                table: "RouteFixedValueMapping");

            migrationBuilder.DropColumn(
                name: "TargetNumericPrecision",
                table: "RouteFixedValueMapping");

            migrationBuilder.DropColumn(
                name: "TargetNumericScale",
                table: "RouteFixedValueMapping");

            migrationBuilder.DropColumn(
                name: "DestinationDataType",
                table: "RouteColumnMapping");

            migrationBuilder.DropColumn(
                name: "DestinationIsNullable",
                table: "RouteColumnMapping");

            migrationBuilder.DropColumn(
                name: "DestinationMaxLength",
                table: "RouteColumnMapping");

            migrationBuilder.DropColumn(
                name: "DestinationNumericPrecision",
                table: "RouteColumnMapping");

            migrationBuilder.DropColumn(
                name: "DestinationNumericScale",
                table: "RouteColumnMapping");

            migrationBuilder.DropColumn(
                name: "ForwardTransformJson",
                table: "RouteColumnMapping");

            migrationBuilder.DropColumn(
                name: "ReverseTransformJson",
                table: "RouteColumnMapping");

            migrationBuilder.DropColumn(
                name: "SourceDataType",
                table: "RouteColumnMapping");

            migrationBuilder.DropColumn(
                name: "SourceIsNullable",
                table: "RouteColumnMapping");

            migrationBuilder.DropColumn(
                name: "SourceMaxLength",
                table: "RouteColumnMapping");

            migrationBuilder.DropColumn(
                name: "SourceNumericPrecision",
                table: "RouteColumnMapping");

            migrationBuilder.DropColumn(
                name: "SourceNumericScale",
                table: "RouteColumnMapping");
        }
    }
}
