using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddGoogleDriveConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GoogleDriveConnectionSettings",
                columns: table => new
                {
                    ConnectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ClientSecretEncrypted = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    RefreshTokenEncrypted = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    AccountEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    RootFolder = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoogleDriveConnectionSettings", x => x.ConnectionId);
                    table.ForeignKey(
                        name: "FK_GoogleDriveConnectionSettings_Connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "Connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GoogleDriveConnectionSettings");
        }
    }
}
