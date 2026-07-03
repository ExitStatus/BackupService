using BackupService.Enumerations;

namespace BackupService.Groups
{
    /// <summary>
    /// A minimal group projection (id + name + concurrency) for pickers (e.g. the profile-dialog
    /// group dropdown) and grid labels.
    /// </summary>
    public sealed record GroupSummary(int Id, string Name, GroupConcurrency Concurrency);
}
