using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddDatabaseBackupSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DatabaseBackupSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TargetConnectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    TargetFolder = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Schedule = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    MaxBackups = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseBackupSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatabaseBackupSettings_Connections_TargetConnectionId",
                        column: x => x.TargetConnectionId,
                        principalTable: "Connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseBackupSettings_TargetConnectionId",
                table: "DatabaseBackupSettings",
                column: "TargetConnectionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DatabaseBackupSettings");
        }
    }
}
