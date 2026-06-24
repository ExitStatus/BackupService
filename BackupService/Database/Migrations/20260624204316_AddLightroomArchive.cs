using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddLightroomArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LightroomFolder",
                table: "Profiles",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RawFolderName",
                table: "Profiles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RawFormats",
                table: "Profiles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LightroomArchiveItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SourceFolder = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    TargetFolder = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    TargetConnectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    DebounceMilliseconds = table.Column<int>(type: "INTEGER", nullable: false),
                    IncludeSubFolders = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowDeletions = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LightroomArchiveItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LightroomArchiveItems_Connections_TargetConnectionId",
                        column: x => x.TargetConnectionId,
                        principalTable: "Connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LightroomArchiveItems_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LightroomArchiveItems_ProfileId",
                table: "LightroomArchiveItems",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_LightroomArchiveItems_TargetConnectionId",
                table: "LightroomArchiveItems",
                column: "TargetConnectionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LightroomArchiveItems");

            migrationBuilder.DropColumn(
                name: "LightroomFolder",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "RawFolderName",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "RawFormats",
                table: "Profiles");
        }
    }
}
