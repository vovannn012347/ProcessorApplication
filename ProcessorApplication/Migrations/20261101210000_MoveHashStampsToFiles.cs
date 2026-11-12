using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using ProcessorApplication.Database;

#nullable disable

namespace ProcessorApplication.Migrations
{
    /// <summary>
    /// The server key chain moves out of the database into encrypted files (HashStampFileStore).
    /// The table is only renamed here; HashStampService moves its rows into the files on first use and then drops it.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261101210000_MoveHashStampsToFiles")]
    public partial class MoveHashStampsToFiles : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(name: "HashStamps", newName: "HashStamps_Legacy");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The legacy table may already be gone (moved into files); bring back an empty one so the rename works.
            migrationBuilder.Sql(
                "CREATE TABLE IF NOT EXISTS \"HashStamps_Legacy\" (" +
                "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_HashStamps\" PRIMARY KEY AUTOINCREMENT, " +
                "\"StampTime\" TEXT NOT NULL, \"MasterKey\" TEXT NOT NULL, \"PreviousHash\" TEXT NOT NULL, " +
                "\"KeyMode\" INTEGER NOT NULL DEFAULT 0, \"WrappedKey\" TEXT NULL, \"KeyCheck\" TEXT NULL);");
            migrationBuilder.RenameTable(name: "HashStamps_Legacy", newName: "HashStamps");
        }
    }
}
