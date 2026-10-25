using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcessingModule.Migrations
{
    public partial class AddResultChain : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResultChain",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StepId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScriptId = table.Column<string>(type: "TEXT", nullable: false),
                    ScriptVersion = table.Column<string>(type: "TEXT", nullable: true),
                    ScriptBuildId = table.Column<int>(type: "INTEGER", nullable: true),
                    ArtifactHash = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StampId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserBound = table.Column<bool>(type: "INTEGER", nullable: false),
                    Seal = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResultChain", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResultChain_OwnerId_Sequence",
                table: "ResultChain",
                columns: new[] { "OwnerId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResultChain_StepId",
                table: "ResultChain",
                column: "StepId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResultChain");
        }
    }
}
