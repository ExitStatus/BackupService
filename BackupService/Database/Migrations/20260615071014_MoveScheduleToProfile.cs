using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class MoveScheduleToProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Schedule",
                table: "FolderPairs");

            migrationBuilder.AddColumn<string>(
                name: "Schedule",
                table: "Profiles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Schedule",
                table: "Profiles");

            migrationBuilder.AddColumn<string>(
                name: "Schedule",
                table: "FolderPairs",
                type: "TEXT",
                maxLength: 256,
                nullable: true);
        }
    }
}
