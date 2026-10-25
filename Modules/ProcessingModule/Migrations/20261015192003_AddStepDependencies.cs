using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcessingModule.Migrations
{
    public partial class AddStepDependencies : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DependsOn",
                table: "ProcessingJobs",
                type: "TEXT",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DependsOn",
                table: "ProcessingJobs");
        }
    }
}
