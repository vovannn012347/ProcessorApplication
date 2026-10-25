using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcessorApplication.Migrations
{
    public partial class ProtectServerKeys : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KeyCheck",
                table: "HashStamps",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "KeyMode",
                table: "HashStamps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "WrappedKey",
                table: "HashStamps",
                type: "TEXT",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeyCheck",
                table: "HashStamps");

            migrationBuilder.DropColumn(
                name: "KeyMode",
                table: "HashStamps");

            migrationBuilder.DropColumn(
                name: "WrappedKey",
                table: "HashStamps");
        }
    }
}
