using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddTwoWaySync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TwoWaySyncItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SourceFolder = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    TargetFolder = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    IncludeSubFolders = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConflictResolution = table.Column<int>(type: "INTEGER", nullable: false),
                    PropagateDeletions = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TwoWaySyncItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TwoWaySyncItems_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TwoWaySyncFilters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TwoWaySyncItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TwoWaySyncFilters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TwoWaySyncFilters_TwoWaySyncItems_TwoWaySyncItemId",
                        column: x => x.TwoWaySyncItemId,
                        principalTable: "TwoWaySyncItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TwoWaySyncFilters_TwoWaySyncItemId",
                table: "TwoWaySyncFilters",
                column: "TwoWaySyncItemId");

            migrationBuilder.CreateIndex(
                name: "IX_TwoWaySyncItems_ProfileId",
                table: "TwoWaySyncItems",
                column: "ProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TwoWaySyncFilters");

            migrationBuilder.DropTable(
                name: "TwoWaySyncItems");
        }
    }
}
