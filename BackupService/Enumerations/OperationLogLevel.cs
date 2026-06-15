namespace BackupService.Enumerations
{
    /// <summary>
    /// Severity of an <c>OperationLogDetail</c> line. Named to avoid colliding with
    /// <see cref="Microsoft.Extensions.Logging.LogLevel"/>.
    /// </summary>
    public enum OperationLogLevel
    {
        Info = 0,
        Warning = 1,
        Error = 2,
        Debug = 3,
    }
}
