using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupFilters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArchiveSyncFilters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ArchiveSyncItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveSyncFilters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArchiveSyncFilters_ArchiveSyncItems_ArchiveSyncItemId",
                        column: x => x.ArchiveSyncItemId,
                        principalTable: "ArchiveSyncItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FolderPairFilters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FolderPairId = table.Column<int>(type: "INTEGER", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderPairFilters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FolderPairFilters_FolderPairs_FolderPairId",
                        column: x => x.FolderPairId,
                        principalTable: "FolderPairs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveSyncFilters_ArchiveSyncItemId",
                table: "ArchiveSyncFilters",
                column: "ArchiveSyncItemId");

            migrationBuilder.CreateIndex(
                name: "IX_FolderPairFilters_FolderPairId",
                table: "FolderPairFilters",
                column: "FolderPairId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArchiveSyncFilters");

            migrationBuilder.DropTable(
                name: "FolderPairFilters");
        }
    }
}
