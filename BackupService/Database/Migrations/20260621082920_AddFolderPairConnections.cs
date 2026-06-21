using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddFolderPairConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SourceConnectionId",
                table: "FolderPairs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetConnectionId",
                table: "FolderPairs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_FolderPairs_SourceConnectionId",
                table: "FolderPairs",
                column: "SourceConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_FolderPairs_TargetConnectionId",
                table: "FolderPairs",
                column: "TargetConnectionId");

            migrationBuilder.AddForeignKey(
                name: "FK_FolderPairs_Connections_SourceConnectionId",
                table: "FolderPairs",
                column: "SourceConnectionId",
                principalTable: "Connections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FolderPairs_Connections_TargetConnectionId",
                table: "FolderPairs",
                column: "TargetConnectionId",
                principalTable: "Connections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FolderPairs_Connections_SourceConnectionId",
                table: "FolderPairs");

            migrationBuilder.DropForeignKey(
                name: "FK_FolderPairs_Connections_TargetConnectionId",
                table: "FolderPairs");

            migrationBuilder.DropIndex(
                name: "IX_FolderPairs_SourceConnectionId",
                table: "FolderPairs");

            migrationBuilder.DropIndex(
                name: "IX_FolderPairs_TargetConnectionId",
                table: "FolderPairs");

            migrationBuilder.DropColumn(
                name: "SourceConnectionId",
                table: "FolderPairs");

            migrationBuilder.DropColumn(
                name: "TargetConnectionId",
                table: "FolderPairs");
        }
    }
}
