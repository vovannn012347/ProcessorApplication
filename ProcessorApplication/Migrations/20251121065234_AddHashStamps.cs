using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcessorApplication.Migrations
{
    public partial class AddHashStamps : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HashStamps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StampTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MasterKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PreviousHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HashStamps", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HashStamps_StampTime",
                table: "HashStamps",
                column: "StampTime",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HashStamps");
        }
    }
}
