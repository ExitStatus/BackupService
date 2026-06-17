using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddInstantSyncItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InstantSyncItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SourceFolder = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    TargetFolder = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    DebounceMilliseconds = table.Column<int>(type: "INTEGER", nullable: false),
                    IncludeSubFolders = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowDeletions = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstantSyncItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstantSyncItems_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InstantSyncItems_ProfileId",
                table: "InstantSyncItems",
                column: "ProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstantSyncItems");
        }
    }
}
