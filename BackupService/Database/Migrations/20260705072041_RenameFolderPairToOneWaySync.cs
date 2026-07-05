using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class RenameFolderPairToOneWaySync : Migration
    {
        // NOTE: hand-edited to RENAME the tables/columns/indexes rather than the drop-and-recreate EF scaffolded
        // (which would delete every existing one-way-sync item + filter). The FolderPair* → OneWaySync* rename
        // preserves data; the physical PK/FK constraint names keep their old FolderPair-based names in SQLite
        // (ALTER TABLE RENAME doesn't rename constraints) — cosmetic only, invisible to EF's model diffing.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(name: "FolderPairs", newName: "OneWaySyncItems");
            migrationBuilder.RenameTable(name: "FolderPairFilters", newName: "OneWaySyncFilters");

            migrationBuilder.RenameColumn(name: "FolderPairId", table: "OneWaySyncFilters", newName: "OneWaySyncItemId");

            migrationBuilder.DropIndex(name: "IX_FolderPairs_ProfileId", table: "OneWaySyncItems");
            migrationBuilder.CreateIndex(name: "IX_OneWaySyncItems_ProfileId", table: "OneWaySyncItems", column: "ProfileId");

            migrationBuilder.DropIndex(name: "IX_FolderPairFilters_FolderPairId", table: "OneWaySyncFilters");
            migrationBuilder.CreateIndex(name: "IX_OneWaySyncFilters_OneWaySyncItemId", table: "OneWaySyncFilters", column: "OneWaySyncItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_OneWaySyncFilters_OneWaySyncItemId", table: "OneWaySyncFilters");
            migrationBuilder.CreateIndex(name: "IX_FolderPairFilters_FolderPairId", table: "OneWaySyncFilters", column: "OneWaySyncItemId");

            migrationBuilder.DropIndex(name: "IX_OneWaySyncItems_ProfileId", table: "OneWaySyncItems");
            migrationBuilder.CreateIndex(name: "IX_FolderPairs_ProfileId", table: "OneWaySyncItems", column: "ProfileId");

            migrationBuilder.RenameColumn(name: "OneWaySyncItemId", table: "OneWaySyncFilters", newName: "FolderPairId");

            migrationBuilder.RenameTable(name: "OneWaySyncFilters", newName: "FolderPairFilters");
            migrationBuilder.RenameTable(name: "OneWaySyncItems", newName: "FolderPairs");
        }
    }
}
