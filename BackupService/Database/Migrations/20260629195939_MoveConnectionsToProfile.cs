using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class MoveConnectionsToProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Add the new profile-level connection columns (the connection is now shared by every row).
            migrationBuilder.AddColumn<int>(
                name: "SourceConnectionId",
                table: "Profiles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetConnectionId",
                table: "Profiles",
                type: "INTEGER",
                nullable: true);

            // 2. Promote each profile's connection from its first (lowest-Id) child row. A profile only has rows
            //    in the child table matching its type, so each EXISTS-guarded update touches just that type.
            migrationBuilder.Sql(@"
                UPDATE Profiles
                SET SourceConnectionId = (SELECT c.SourceConnectionId FROM FolderPairs c WHERE c.ProfileId = Profiles.Id ORDER BY c.Id LIMIT 1),
                    TargetConnectionId = (SELECT c.TargetConnectionId FROM FolderPairs c WHERE c.ProfileId = Profiles.Id ORDER BY c.Id LIMIT 1)
                WHERE EXISTS (SELECT 1 FROM FolderPairs c WHERE c.ProfileId = Profiles.Id);");

            migrationBuilder.Sql(@"
                UPDATE Profiles
                SET SourceConnectionId = (SELECT c.SourceConnectionId FROM InstantSyncItems c WHERE c.ProfileId = Profiles.Id ORDER BY c.Id LIMIT 1),
                    TargetConnectionId = (SELECT c.TargetConnectionId FROM InstantSyncItems c WHERE c.ProfileId = Profiles.Id ORDER BY c.Id LIMIT 1)
                WHERE EXISTS (SELECT 1 FROM InstantSyncItems c WHERE c.ProfileId = Profiles.Id);");

            migrationBuilder.Sql(@"
                UPDATE Profiles
                SET SourceConnectionId = (SELECT c.SourceConnectionId FROM ArchiveSyncItems c WHERE c.ProfileId = Profiles.Id ORDER BY c.Id LIMIT 1),
                    TargetConnectionId = (SELECT c.TargetConnectionId FROM ArchiveSyncItems c WHERE c.ProfileId = Profiles.Id ORDER BY c.Id LIMIT 1)
                WHERE EXISTS (SELECT 1 FROM ArchiveSyncItems c WHERE c.ProfileId = Profiles.Id);");

            // LightroomArchive has a connectable target only (its source is local).
            migrationBuilder.Sql(@"
                UPDATE Profiles
                SET TargetConnectionId = (SELECT c.TargetConnectionId FROM LightroomArchiveItems c WHERE c.ProfileId = Profiles.Id ORDER BY c.Id LIMIT 1)
                WHERE EXISTS (SELECT 1 FROM LightroomArchiveItems c WHERE c.ProfileId = Profiles.Id);");

            // 3. Drop any child row whose connection differs from its profile's promoted value (NULL-safe via IS NOT).
            migrationBuilder.Sql(@"
                DELETE FROM FolderPairs
                WHERE EXISTS (
                    SELECT 1 FROM Profiles p WHERE p.Id = FolderPairs.ProfileId
                    AND (FolderPairs.SourceConnectionId IS NOT p.SourceConnectionId
                         OR FolderPairs.TargetConnectionId IS NOT p.TargetConnectionId));");

            migrationBuilder.Sql(@"
                DELETE FROM InstantSyncItems
                WHERE EXISTS (
                    SELECT 1 FROM Profiles p WHERE p.Id = InstantSyncItems.ProfileId
                    AND (InstantSyncItems.SourceConnectionId IS NOT p.SourceConnectionId
                         OR InstantSyncItems.TargetConnectionId IS NOT p.TargetConnectionId));");

            migrationBuilder.Sql(@"
                DELETE FROM ArchiveSyncItems
                WHERE EXISTS (
                    SELECT 1 FROM Profiles p WHERE p.Id = ArchiveSyncItems.ProfileId
                    AND (ArchiveSyncItems.SourceConnectionId IS NOT p.SourceConnectionId
                         OR ArchiveSyncItems.TargetConnectionId IS NOT p.TargetConnectionId));");

            migrationBuilder.Sql(@"
                DELETE FROM LightroomArchiveItems
                WHERE EXISTS (
                    SELECT 1 FROM Profiles p WHERE p.Id = LightroomArchiveItems.ProfileId
                    AND LightroomArchiveItems.TargetConnectionId IS NOT p.TargetConnectionId);");

            // 4. Drop the old per-row connection FKs / indexes / columns.
            migrationBuilder.DropForeignKey(
                name: "FK_ArchiveSyncItems_Connections_SourceConnectionId",
                table: "ArchiveSyncItems");

            migrationBuilder.DropForeignKey(
                name: "FK_ArchiveSyncItems_Connections_TargetConnectionId",
                table: "ArchiveSyncItems");

            migrationBuilder.DropForeignKey(
                name: "FK_FolderPairs_Connections_SourceConnectionId",
                table: "FolderPairs");

            migrationBuilder.DropForeignKey(
                name: "FK_FolderPairs_Connections_TargetConnectionId",
                table: "FolderPairs");

            migrationBuilder.DropForeignKey(
                name: "FK_InstantSyncItems_Connections_SourceConnectionId",
                table: "InstantSyncItems");

            migrationBuilder.DropForeignKey(
                name: "FK_InstantSyncItems_Connections_TargetConnectionId",
                table: "InstantSyncItems");

            migrationBuilder.DropForeignKey(
                name: "FK_LightroomArchiveItems_Connections_TargetConnectionId",
                table: "LightroomArchiveItems");

            migrationBuilder.DropIndex(
                name: "IX_LightroomArchiveItems_TargetConnectionId",
                table: "LightroomArchiveItems");

            migrationBuilder.DropIndex(
                name: "IX_InstantSyncItems_SourceConnectionId",
                table: "InstantSyncItems");

            migrationBuilder.DropIndex(
                name: "IX_InstantSyncItems_TargetConnectionId",
                table: "InstantSyncItems");

            migrationBuilder.DropIndex(
                name: "IX_FolderPairs_SourceConnectionId",
                table: "FolderPairs");

            migrationBuilder.DropIndex(
                name: "IX_FolderPairs_TargetConnectionId",
                table: "FolderPairs");

            migrationBuilder.DropIndex(
                name: "IX_ArchiveSyncItems_SourceConnectionId",
                table: "ArchiveSyncItems");

            migrationBuilder.DropIndex(
                name: "IX_ArchiveSyncItems_TargetConnectionId",
                table: "ArchiveSyncItems");

            migrationBuilder.DropColumn(
                name: "TargetConnectionId",
                table: "LightroomArchiveItems");

            migrationBuilder.DropColumn(
                name: "SourceConnectionId",
                table: "InstantSyncItems");

            migrationBuilder.DropColumn(
                name: "TargetConnectionId",
                table: "InstantSyncItems");

            migrationBuilder.DropColumn(
                name: "SourceConnectionId",
                table: "FolderPairs");

            migrationBuilder.DropColumn(
                name: "TargetConnectionId",
                table: "FolderPairs");

            migrationBuilder.DropColumn(
                name: "SourceConnectionId",
                table: "ArchiveSyncItems");

            migrationBuilder.DropColumn(
                name: "TargetConnectionId",
                table: "ArchiveSyncItems");

            // 5. Index + FK the new profile-level columns (Restrict — the connection service blocks an in-use delete).
            migrationBuilder.CreateIndex(
                name: "IX_Profiles_SourceConnectionId",
                table: "Profiles",
                column: "SourceConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_TargetConnectionId",
                table: "Profiles",
                column: "TargetConnectionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Profiles_Connections_SourceConnectionId",
                table: "Profiles",
                column: "SourceConnectionId",
                principalTable: "Connections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Profiles_Connections_TargetConnectionId",
                table: "Profiles",
                column: "TargetConnectionId",
                principalTable: "Connections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Re-add the per-row connection columns / indexes / FKs.
            migrationBuilder.AddColumn<int>(
                name: "TargetConnectionId",
                table: "LightroomArchiveItems",
                type: "INTEGER",
                nullable: true);

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
                table: "FolderPairs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetConnectionId",
                table: "FolderPairs",
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

            // Best-effort: copy the profile-level connection back onto every row (rows deleted by Up are not
            // recoverable). Done while the Profiles columns still exist (they're dropped last).
            migrationBuilder.Sql(@"
                UPDATE FolderPairs SET
                    SourceConnectionId = (SELECT p.SourceConnectionId FROM Profiles p WHERE p.Id = FolderPairs.ProfileId),
                    TargetConnectionId = (SELECT p.TargetConnectionId FROM Profiles p WHERE p.Id = FolderPairs.ProfileId);");
            migrationBuilder.Sql(@"
                UPDATE InstantSyncItems SET
                    SourceConnectionId = (SELECT p.SourceConnectionId FROM Profiles p WHERE p.Id = InstantSyncItems.ProfileId),
                    TargetConnectionId = (SELECT p.TargetConnectionId FROM Profiles p WHERE p.Id = InstantSyncItems.ProfileId);");
            migrationBuilder.Sql(@"
                UPDATE ArchiveSyncItems SET
                    SourceConnectionId = (SELECT p.SourceConnectionId FROM Profiles p WHERE p.Id = ArchiveSyncItems.ProfileId),
                    TargetConnectionId = (SELECT p.TargetConnectionId FROM Profiles p WHERE p.Id = ArchiveSyncItems.ProfileId);");
            migrationBuilder.Sql(@"
                UPDATE LightroomArchiveItems SET
                    TargetConnectionId = (SELECT p.TargetConnectionId FROM Profiles p WHERE p.Id = LightroomArchiveItems.ProfileId);");

            migrationBuilder.CreateIndex(
                name: "IX_LightroomArchiveItems_TargetConnectionId",
                table: "LightroomArchiveItems",
                column: "TargetConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_InstantSyncItems_SourceConnectionId",
                table: "InstantSyncItems",
                column: "SourceConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_InstantSyncItems_TargetConnectionId",
                table: "InstantSyncItems",
                column: "TargetConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_FolderPairs_SourceConnectionId",
                table: "FolderPairs",
                column: "SourceConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_FolderPairs_TargetConnectionId",
                table: "FolderPairs",
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

            migrationBuilder.AddForeignKey(
                name: "FK_LightroomArchiveItems_Connections_TargetConnectionId",
                table: "LightroomArchiveItems",
                column: "TargetConnectionId",
                principalTable: "Connections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Finally drop the profile-level columns (after the copy-back above has read them).
            migrationBuilder.DropForeignKey(
                name: "FK_Profiles_Connections_SourceConnectionId",
                table: "Profiles");

            migrationBuilder.DropForeignKey(
                name: "FK_Profiles_Connections_TargetConnectionId",
                table: "Profiles");

            migrationBuilder.DropIndex(
                name: "IX_Profiles_SourceConnectionId",
                table: "Profiles");

            migrationBuilder.DropIndex(
                name: "IX_Profiles_TargetConnectionId",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "SourceConnectionId",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "TargetConnectionId",
                table: "Profiles");
        }
    }
}
