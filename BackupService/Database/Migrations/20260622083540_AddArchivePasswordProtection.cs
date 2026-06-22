using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddArchivePasswordProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EncryptionMethod",
                table: "ArchiveSyncItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PasswordEncrypted",
                table: "ArchiveSyncItems",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PasswordProtect",
                table: "ArchiveSyncItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncryptionMethod",
                table: "ArchiveSyncItems");

            migrationBuilder.DropColumn(
                name: "PasswordEncrypted",
                table: "ArchiveSyncItems");

            migrationBuilder.DropColumn(
                name: "PasswordProtect",
                table: "ArchiveSyncItems");
        }
    }
}
