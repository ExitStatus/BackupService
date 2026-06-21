namespace BackupService.Connections.Smb
{
    /// <summary>Outcome of an SMB connection test: success plus a human-readable message.</summary>
    public sealed record ConnectionTestResult(bool Ok, string Message)
    {
        public static ConnectionTestResult Success(string message) => new(true, message);

        public static ConnectionTestResult Failure(string message) => new(false, message);
    }
}
