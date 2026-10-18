using SMBLibrary;

namespace BackupService.FileSystem.Smb
{
    /// <summary>
    /// Turns a failed SMB status into the exception to throw. A file locked on the server carries the Windows error
    /// code a locked local file would, so <see cref="FileLock.IsSkippableReadError"/> treats it the same way — a
    /// non-fatal "locked" Warning, retried next run — rather than an Error on every run. A status that means the
    /// session itself is gone becomes an <see cref="SmbConnectionLostException"/>, so the filesystem reconnects.
    /// </summary>
    internal static class SmbStatusErrors
    {
        private const int SharingViolationHResult = unchecked((int)0x80070020); // ERROR_SHARING_VIOLATION
        private const int LockViolationHResult = unchecked((int)0x80070021);    // ERROR_LOCK_VIOLATION

        // SMBLibrary's own message once its socket has dropped — every later call on that client throws it.
        private const string ClientDisconnectedMessage = "no longer connected";

        public static IOException Create(NTStatus status, string message) => status switch
        {
            NTStatus.STATUS_SHARING_VIOLATION => new IOException(message, SharingViolationHResult),
            NTStatus.STATUS_FILE_LOCK_CONFLICT or NTStatus.STATUS_LOCK_NOT_GRANTED => new IOException(message, LockViolationHResult),
            NTStatus.STATUS_USER_SESSION_DELETED or NTStatus.STATUS_NETWORK_NAME_DELETED => new SmbConnectionLostException(message),
            _ => new IOException(message),
        };

        /// <summary>
        /// True when <paramref name="ex"/> means the SMB connection or session has gone (rather than this one
        /// operation failing), so a fresh connection is worth trying.
        /// </summary>
        public static bool IsConnectionLost(Exception ex) =>
            ex is SmbConnectionLostException
            || (ex is InvalidOperationException && ex.Message.Contains(ClientDisconnectedMessage, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An SMB call failed because the session or connection to the server was lost.</summary>
    internal sealed class SmbConnectionLostException(string message) : IOException(message);
}
