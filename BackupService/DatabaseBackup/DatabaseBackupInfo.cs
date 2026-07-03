namespace BackupService.DatabaseBackup
{
    /// <summary>One backup archive found in the target folder (for the restore picker).</summary>
    public sealed record DatabaseBackupInfo(string FileName, DateTime Timestamp, long SizeBytes);
}
