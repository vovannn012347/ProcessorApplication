using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcessingModule.Migrations
{
    public partial class AddScriptBuildToSubJob : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ScriptIndexId",
                table: "ProcessingJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScriptVersion",
                table: "ProcessingJobs",
                type: "TEXT",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScriptIndexId",
                table: "ProcessingJobs");

            migrationBuilder.DropColumn(
                name: "ScriptVersion",
                table: "ProcessingJobs");
        }
    }
}
