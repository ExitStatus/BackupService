namespace BackupService.Profiles
{
    /// <summary>
    /// A running profile's progress snapshot: the overall <paramref name="TotalPercent"/> across the whole
    /// run, plus the current step — a one way sync / archive / sync item — by <paramref name="StepName"/> and
    /// its own <paramref name="StepPercent"/>, how many steps the run has (<paramref name="StepCount"/>), and
    /// the <paramref name="CurrentFile"/> currently being copied (null when unknown / between files).
    /// The progress window shows "{step name} {total%}" when a run has a single step, or
    /// "{step name} {step%}" with a separate "Total {total%}" line when it has more than one.
    /// </summary>
    public sealed record ProfileProgress(int TotalPercent, string? StepName, int StepPercent, int StepCount, string? CurrentFile = null);
}
