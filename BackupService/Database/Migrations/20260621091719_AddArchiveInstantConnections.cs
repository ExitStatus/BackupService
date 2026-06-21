using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddArchiveInstantConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SourceConnectionId",
                table: "InstantSyncItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetConnectionId",
                table: "InstantSyncItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceConnectionId",
                table: "ArchiveSyncItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetConnectionId",
                table: "ArchiveSyncItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstantSyncItems_SourceConnectionId",
                table: "InstantSyncItems",
                column: "SourceConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_InstantSyncItems_TargetConnectionId",
                table: "InstantSyncItems",
                column: "TargetConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveSyncItems_SourceConnectionId",
                table: "ArchiveSyncItems",
                column: "SourceConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveSyncItems_TargetConnectionId",
                table: "ArchiveSyncItems",
                column: "TargetConnectionId");

            migrationBuilder.AddForeignKey(
                name: "FK_ArchiveSyncItems_Connections_SourceConnectionId",
                table: "ArchiveSyncItems",
                column: "SourceConnectionId",
                principalTable: "Connections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ArchiveSyncItems_Connections_TargetConnectionId",
                table: "ArchiveSyncItems",
                column: "TargetConnectionId",
                principalTable: "Connections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InstantSyncItems_Connections_SourceConnectionId",
                table: "InstantSyncItems",
                column: "SourceConnectionId",
                principalTable: "Connections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InstantSyncItems_Connections_TargetConnectionId",
                table: "InstantSyncItems",
                column: "TargetConnectionId",
                principalTable: "Connections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ArchiveSyncItems_Connections_SourceConnectionId",
                table: "ArchiveSyncItems");

            migrationBuilder.DropForeignKey(
                name: "FK_ArchiveSyncItems_Connections_TargetConnectionId",
                table: "ArchiveSyncItems");

            migrationBuilder.DropForeignKey(
                name: "FK_InstantSyncItems_Connections_SourceConnectionId",
                table: "InstantSyncItems");

            migrationBuilder.DropForeignKey(
                name: "FK_InstantSyncItems_Connections_TargetConnectionId",
                table: "InstantSyncItems");

            migrationBuilder.DropIndex(
                name: "IX_InstantSyncItems_SourceConnectionId",
                table: "InstantSyncItems");

            migrationBuilder.DropIndex(
                name: "IX_InstantSyncItems_TargetConnectionId",
                table: "InstantSyncItems");

            migrationBuilder.DropIndex(
                name: "IX_ArchiveSyncItems_SourceConnectionId",
                table: "ArchiveSyncItems");

            migrationBuilder.DropIndex(
                name: "IX_ArchiveSyncItems_TargetConnectionId",
                table: "ArchiveSyncItems");

            migrationBuilder.DropColumn(
                name: "SourceConnectionId",
                table: "InstantSyncItems");

            migrationBuilder.DropColumn(
                name: "TargetConnectionId",
                table: "InstantSyncItems");

            migrationBuilder.DropColumn(
                name: "SourceConnectionId",
                table: "ArchiveSyncItems");

            migrationBuilder.DropColumn(
                name: "TargetConnectionId",
                table: "ArchiveSyncItems");
        }
    }
}
