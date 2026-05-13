using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcessingModule.Migrations
{
    public partial class InitialProcessing : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ResultHash = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    InitiatorUserId = table.Column<string>(type: "TEXT", nullable: false),
                    PhysicalPathRoot = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Scripts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScriptIdentifier = table.Column<string>(type: "TEXT", nullable: false),
                    ScriptLabel = table.Column<string>(type: "TEXT", nullable: false),
                    ScriptVersion = table.Column<string>(type: "TEXT", nullable: false),
                    ProcessorVersion = table.Column<string>(type: "TEXT", nullable: false),
                    ArtifactHash = table.Column<string>(type: "TEXT", nullable: false),
                    HashMatch = table.Column<bool>(type: "INTEGER", nullable: false),
                    ManifestDirectoryPath = table.Column<string>(type: "TEXT", nullable: false),
                    IsAvailable = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Scripts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProcessingJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ResultHash = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    ScriptId = table.Column<string>(type: "TEXT", nullable: false),
                    ResultMessage = table.Column<string>(type: "TEXT", nullable: true),
                    StepDirectoryName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessingJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProcessingJobs_Jobs_ParentJobId",
                        column: x => x.ParentJobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingJobs_ParentJobId",
                table: "ProcessingJobs",
                column: "ParentJobId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessingJobs");

            migrationBuilder.DropTable(
                name: "Scripts");

            migrationBuilder.DropTable(
                name: "Jobs");
        }
    }
}
