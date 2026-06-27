using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledTaskStepKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Command",
                table: "ScheduledTaskSteps",
                type: "TEXT",
                maxLength: 1024,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 1024);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "ScheduledTaskSteps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Script",
                table: "ScheduledTaskSteps",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "ScheduledTaskSteps");

            migrationBuilder.DropColumn(
                name: "Script",
                table: "ScheduledTaskSteps");

            migrationBuilder.AlterColumn<string>(
                name: "Command",
                table: "ScheduledTaskSteps",
                type: "TEXT",
                maxLength: 1024,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 1024,
                oldNullable: true);
        }
    }
}
