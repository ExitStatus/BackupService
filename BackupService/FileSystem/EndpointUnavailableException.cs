namespace BackupService.FileSystem
{
    /// <summary>
    /// Thrown by an <see cref="IBackupFileSystem"/> when its underlying endpoint has gone away mid-run and cannot
    /// be re-established — e.g. an MTP camera switched off or unplugged. Unlike a per-file read error, this is
    /// <b>fatal</b> to the run: the sync engines and handlers let it propagate and stop the run promptly and
    /// cleanly (no leftover temp files), rather than failing every remaining file in turn against a dead device.
    /// </summary>
    public sealed class EndpointUnavailableException(string message, Exception? innerException = null)
        : Exception(message, innerException);
}
