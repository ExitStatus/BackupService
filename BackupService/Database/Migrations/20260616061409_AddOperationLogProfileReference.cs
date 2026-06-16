using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationLogProfileReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProfileId",
                table: "OperationLogs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperationLogs_ProfileId",
                table: "OperationLogs",
                column: "ProfileId");

            migrationBuilder.AddForeignKey(
                name: "FK_OperationLogs_Profiles_ProfileId",
                table: "OperationLogs",
                column: "ProfileId",
                principalTable: "Profiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OperationLogs_Profiles_ProfileId",
                table: "OperationLogs");

            migrationBuilder.DropIndex(
                name: "IX_OperationLogs_ProfileId",
                table: "OperationLogs");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "OperationLogs");
        }
    }
}
