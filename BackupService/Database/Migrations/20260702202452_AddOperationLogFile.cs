using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationLogFile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE: the OperationLogDetails table is intentionally NOT dropped here. Its rows must first be
            // copied into per-log files by OperationLogFileMigrator (runtime, after Database.Migrate), which
            // then drops the table itself — a migration can't do the file I/O, and dropping the table in this
            // same Migrate() pass would destroy the data before it could be copied.
            migrationBuilder.AddColumn<string>(
                name: "LogFile",
                table: "OperationLogs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LogFile",
                table: "OperationLogs");

            migrationBuilder.CreateTable(
                name: "OperationLogDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OperationLogId = table.Column<int>(type: "INTEGER", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationLogDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperationLogDetails_OperationLogs_OperationLogId",
                        column: x => x.OperationLogId,
                        principalTable: "OperationLogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperationLogDetails_OperationLogId",
                table: "OperationLogDetails",
                column: "OperationLogId");
        }
    }
}
