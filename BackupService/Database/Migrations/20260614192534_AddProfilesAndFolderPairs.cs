using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddProfilesAndFolderPairs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Profiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    DateCreated = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DateLastRun = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Profiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FolderPairs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceFolder = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    TargetFolder = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    WatchFolder = table.Column<bool>(type: "INTEGER", nullable: false),
                    Schedule = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    LastRunStatus = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderPairs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FolderPairs_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FolderPairs_ProfileId",
                table: "FolderPairs",
                column: "ProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FolderPairs");

            migrationBuilder.DropTable(
                name: "Profiles");
        }
    }
}
