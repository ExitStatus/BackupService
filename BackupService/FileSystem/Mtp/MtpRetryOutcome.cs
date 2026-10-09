namespace BackupService.FileSystem.Mtp
{
    /// <summary>
    /// What an MTP operation that kept failing amounts to once its retries are used up. WPD reports a session that
    /// has dropped and one object the device won't hand over the same way (a COMException), so the device is probed
    /// first: if it still answers, the trouble is that one object — fail just this file (an IOException, which the
    /// engines record and move past) — and only a device that has stopped answering ends the run.
    /// </summary>
    internal static class MtpRetryOutcome
    {
        public static Exception ForExhaustedRetries(Exception last, bool deviceStillAnswers, string serial) =>
            deviceStillAnswers
                ? new IOException($"The MTP device couldn't complete the operation: {last.Message}", last)
                : new EndpointUnavailableException($"The MTP device '{serial}' is not responding.", last);
    }
}
