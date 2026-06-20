namespace BackupService.FileSystem
{
    /// <summary>
    /// Detects whether an exception was caused by a file being locked / in use by another process
    /// (a Win32 sharing or lock violation). Such a failure is treated as a non-fatal **warning** by the
    /// sync engines rather than a hard error — the file is simply skipped this run.
    /// </summary>
    public static class FileLock
    {
        private const int ErrorSharingViolation = 32; // ERROR_SHARING_VIOLATION
        private const int ErrorLockViolation = 33;    // ERROR_LOCK_VIOLATION

        /// <summary>
        /// True if <paramref name="exception"/> (or any inner exception) is a sharing/lock violation —
        /// by HRESULT Win32 code or the familiar "being used by another process" message.
        /// </summary>
        public static bool IsLockViolation(Exception? exception)
        {
            for (var ex = exception; ex is not null; ex = ex.InnerException)
            {
                if (ex is IOException io)
                {
                    var code = io.HResult & 0xFFFF; // low word of the 0x8007xxxx HRESULT is the Win32 code
                    if (code is ErrorSharingViolation or ErrorLockViolation)
                    {
                        return true;
                    }
                }

                if (ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
