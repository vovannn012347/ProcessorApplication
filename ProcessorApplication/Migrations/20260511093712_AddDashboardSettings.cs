using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcessorApplication.Migrations
{
    public partial class AddDashboardSettings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DashboardItemData",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    WidgetId = table.Column<string>(type: "TEXT", nullable: false),
                    GeneralSettingsJson = table.Column<string>(type: "TEXT", nullable: true),
                    SmallScreenSettingsJson = table.Column<string>(type: "TEXT", nullable: true),
                    LargeScreenSettingsJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardItemData", x => new { x.UserId, x.WidgetId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_DashboardItemData_UserId",
                table: "DashboardItemData",
                column: "UserId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DashboardItemData");
        }
    }
}
