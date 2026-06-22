namespace BackupService.FileSystem
{
    /// <summary>
    /// Classifies whether an exception means a source file simply <em>couldn't be read</em> for a reason
    /// that isn't the backup's fault — a file locked / in use by another process (a Win32 sharing or lock
    /// violation), or an unavailable cloud file (e.g. a OneDrive files-on-demand placeholder that can't
    /// hydrate from a session-0 service). The sync engines treat these as a non-fatal **warning** and skip
    /// the file this run, rather than failing it as an error.
    /// </summary>
    public static class FileLock
    {
        private const int ErrorSharingViolation = 32; // ERROR_SHARING_VIOLATION
        private const int ErrorLockViolation = 33;    // ERROR_LOCK_VIOLATION

        // The whole ERROR_CLOUD_FILE_* family (winerror.h), e.g. 395 = ERROR_CLOUD_FILE_ACCESS_DENIED
        // ("Access to the cloud file is denied"), 362 = provider not running, 391 = in use.
        private const int CloudFileErrorFirst = 358;  // 0x0166
        private const int CloudFileErrorLast = 405;   // 0x0195

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

        /// <summary>
        /// True if <paramref name="exception"/> (or any inner exception) is a Windows cloud-file error — an
        /// un-hydratable OneDrive files-on-demand placeholder being the common case (a service in session 0
        /// can't trigger the provider to download it).
        /// </summary>
        public static bool IsCloudFileError(Exception? exception)
        {
            for (var ex = exception; ex is not null; ex = ex.InnerException)
            {
                if (ex is IOException io)
                {
                    var code = io.HResult & 0xFFFF;
                    if (code is >= CloudFileErrorFirst and <= CloudFileErrorLast)
                    {
                        return true;
                    }
                }

                if (ex.Message.Contains("cloud file", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// If the failure means the file couldn't be read for a non-fatal reason (locked, or an unavailable
        /// cloud file), returns true and a human-readable <paramref name="reason"/> for the warning line;
        /// otherwise false (the caller should treat it as an error).
        /// </summary>
        public static bool IsSkippableReadError(Exception? exception, out string reason)
        {
            if (IsLockViolation(exception))
            {
                reason = "in use by another process (locked)";
                return true;
            }
            if (IsCloudFileError(exception))
            {
                reason = "an unavailable cloud file (e.g. a OneDrive files-on-demand placeholder)";
                return true;
            }

            reason = string.Empty;
            return false;
        }
    }
}
