using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddUsbConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UsbConnectionSettings",
                columns: table => new
                {
                    ConnectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    HardwareSerial = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    VolumeSerial = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DeviceLabel = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RootFolder = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsbConnectionSettings", x => x.ConnectionId);
                    table.ForeignKey(
                        name: "FK_UsbConnectionSettings_Connections_ConnectionId",
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
                name: "UsbConnectionSettings");
        }
    }
}
