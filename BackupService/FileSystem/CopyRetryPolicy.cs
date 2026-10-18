namespace BackupService.FileSystem
{
    /// <summary>
    /// When a single file copy is worth trying again: the connection dropped mid-transfer, or the target received
    /// fewer bytes than the source holds. Both are usually a passing network fault, so the engines wait
    /// <see cref="Delay"/> and copy the file again (from scratch, through a fresh temp) up to
    /// <see cref="MaxAttempts"/> times in all before logging it as an error.
    /// </summary>
    public sealed class CopyRetryPolicy(int maxAttempts, TimeSpan delay)
    {
        public static readonly CopyRetryPolicy Default = new(3, TimeSpan.FromSeconds(5));

        public int MaxAttempts { get; } = Math.Max(1, maxAttempts);

        public TimeSpan Delay { get; } = delay;

        public static bool IsRetryable(Exception ex) => ex is ConnectionLostException or IncompleteCopyException;

        /// <summary>True when the copy that just failed on <paramref name="attempt"/> (1-based) should be retried.</summary>
        public bool ShouldRetry(Exception ex, int attempt) => attempt < MaxAttempts && IsRetryable(ex);
    }

    /// <summary>A filesystem call failed because the connection or session to the remote endpoint was lost.</summary>
    public sealed class ConnectionLostException(string message, Exception? innerException = null)
        : IOException(message, innerException);

    /// <summary>A copy finished with fewer bytes written than the source holds.</summary>
    public sealed class IncompleteCopyException(string message) : IOException(message);
}
