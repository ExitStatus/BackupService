using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupService.Database.Migrations
{
    /// <inheritdoc />
    public partial class DropOperationLogDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the legacy operation-log detail table. Detail lines now live in per-log files
            // (see IOperationLogFileStore). On an already-upgraded database this table was already
            // removed by the (now-deleted) one-time OperationLogFileMigrator, so IF EXISTS makes this
            // a safe no-op there; on a fresh install the historical AddOperationLogTables migration
            // still creates it, and this drops it — keeping the schema in step with the model.
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"OperationLogDetails\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible — the detail data has been migrated to files and discarded from the DB.
        }
    }
}
