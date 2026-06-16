namespace BackupService.Profiles
{
    /// <summary>
    /// A minimal profile projection (id + name) for pickers/filters that don't need the
    /// full entity.
    /// </summary>
    public sealed record ProfileSummary(int Id, string Name);
}
