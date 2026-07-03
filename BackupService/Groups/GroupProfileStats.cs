namespace BackupService.Groups
{
    /// <summary>
    /// Per-group aggregate over its member profiles, for the Groups grid: how many profiles are in the
    /// group, when the group last had a run, and the member profile ids (so the grid can count how many
    /// are running live via <c>IProfileStatusService</c>).
    /// </summary>
    public sealed record GroupProfileStats(int Count, DateTimeOffset? LastRunStarted, IReadOnlyList<int> ProfileIds);
}
