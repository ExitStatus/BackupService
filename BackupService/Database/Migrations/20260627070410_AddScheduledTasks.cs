using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "ProfileId",
                table: "BackupRuns",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "BackupRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ScheduledTaskId",
                table: "BackupRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ScheduledTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Schedule = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DateNextRun = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    HandleMissedSync = table.Column<bool>(type: "INTEGER", nullable: false),
                    DateCreated = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DateLastRun = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledTaskSteps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScheduledTaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RunViaShell = table.Column<bool>(type: "INTEGER", nullable: false),
                    Command = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Arguments = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    WorkingDirectory = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledTaskSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduledTaskSteps_ScheduledTasks_ScheduledTaskId",
                        column: x => x.ScheduledTaskId,
                        principalTable: "ScheduledTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackupRuns_ScheduledTaskId",
                table: "BackupRuns",
                column: "ScheduledTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTaskSteps_ScheduledTaskId",
                table: "ScheduledTaskSteps",
                column: "ScheduledTaskId");

            migrationBuilder.AddForeignKey(
                name: "FK_BackupRuns_ScheduledTasks_ScheduledTaskId",
                table: "BackupRuns",
                column: "ScheduledTaskId",
                principalTable: "ScheduledTasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BackupRuns_ScheduledTasks_ScheduledTaskId",
                table: "BackupRuns");

            migrationBuilder.DropTable(
                name: "ScheduledTaskSteps");

            migrationBuilder.DropTable(
                name: "ScheduledTasks");

            migrationBuilder.DropIndex(
                name: "IX_BackupRuns_ScheduledTaskId",
                table: "BackupRuns");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "BackupRuns");

            migrationBuilder.DropColumn(
                name: "ScheduledTaskId",
                table: "BackupRuns");

            migrationBuilder.AlterColumn<int>(
                name: "ProfileId",
                table: "BackupRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
