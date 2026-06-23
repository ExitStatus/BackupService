namespace BackupService.Connections
{
    /// <summary>
    /// A connection's storage capacity, as reported by the remote. <see cref="TotalBytes"/>/
    /// <see cref="FreeBytes"/> are set for a normal quota; <see cref="Unlimited"/> is true for an account
    /// with no quota (e.g. a Google Workspace Drive). A <b>null</b> <see cref="StorageSpace"/> returned from
    /// the space service means the figure couldn't be determined (the connection was unreachable).
    /// </summary>
    public sealed record StorageSpace(long? TotalBytes, long? FreeBytes, bool Unlimited);
}
