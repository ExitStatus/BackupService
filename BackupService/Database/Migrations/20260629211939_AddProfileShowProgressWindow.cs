using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileShowProgressWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShowProgressWindow",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShowProgressWindow",
                table: "Profiles");
        }
    }
}
