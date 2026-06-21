using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Connections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Connections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SmbConnectionSettings",
                columns: table => new
                {
                    ConnectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    ShareName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Domain = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PasswordEncrypted = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    RootFolder = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmbConnectionSettings", x => x.ConnectionId);
                    table.ForeignKey(
                        name: "FK_SmbConnectionSettings_Connections_ConnectionId",
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
                name: "SmbConnectionSettings");

            migrationBuilder.DropTable(
                name: "Connections");
        }
    }
}
