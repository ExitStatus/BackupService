namespace BackupService.FileSystem.Mtp
{
    /// <summary>
    /// The pure rules <see cref="MtpBackupFileSystem"/> uses to verify a download against the size the device
    /// reports for the object (extracted so they're unit-testable without a device).
    /// <para>
    /// A WPD transfer can end early without throwing, so a download <b>shorter</b> than the reported size is refused
    /// and re-fetched. Anything else is accepted: devices legitimately deliver more than they report (an iPhone
    /// converting HEIC to JPEG, a PTP camera capping a &gt;4 GB video's size at 0xFFFFFFFF), and a reported size of
    /// zero or one that doesn't fit a <see cref="long"/> gives nothing to verify against.
    /// </para>
    /// </summary>
    internal static class MtpDownloadCheck
    {
        /// <summary>The device-reported object size as a <see cref="long"/>, or -1 ("not known") when it doesn't fit.</summary>
        public static long ToReportedSize(ulong length) => length > long.MaxValue ? -1 : (long)length;

        /// <summary>True when a download of <paramref name="actual"/> bytes can be accepted for an object the device reports as <paramref name="expected"/> bytes.</summary>
        public static bool IsComplete(long expected, long actual) => expected <= 0 || actual >= expected;

        /// <summary>
        /// Whether a short download is worth another attempt. Not once <paramref name="maxAttempts"/> are used, and not
        /// when it came back exactly as short as the previous attempt: a dropped session doesn't cut a transfer at the
        /// same byte twice, so the device is consistently delivering that much and re-fetching won't change it.
        /// </summary>
        public static bool ShouldRetry(long actual, long? previousActual, int attempt, int maxAttempts) =>
            attempt < maxAttempts && actual != previousActual;
    }
}
