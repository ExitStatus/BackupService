using SMBLibrary;

namespace BackupService.FileSystem.Smb
{
    /// <summary>
    /// Turns a failed SMB status into the exception to throw. A file locked on the server carries the Windows error
    /// code a locked local file would, so <see cref="FileLock.IsSkippableReadError"/> treats it the same way — a
    /// non-fatal "locked" Warning, retried next run — rather than an Error on every run.
    /// </summary>
    internal static class SmbStatusErrors
    {
        private const int SharingViolationHResult = unchecked((int)0x80070020); // ERROR_SHARING_VIOLATION
        private const int LockViolationHResult = unchecked((int)0x80070021);    // ERROR_LOCK_VIOLATION

        public static IOException Create(NTStatus status, string message) => status switch
        {
            NTStatus.STATUS_SHARING_VIOLATION => new IOException(message, SharingViolationHResult),
            NTStatus.STATUS_FILE_LOCK_CONFLICT or NTStatus.STATUS_LOCK_NOT_GRANTED => new IOException(message, LockViolationHResult),
            _ => new IOException(message),
        };
    }
}
