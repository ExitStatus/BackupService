namespace BackupService.Connections
{
    /// <summary>
    /// SMB settings supplied when creating or updating a connection. <see cref="Password"/> is the
    /// plaintext as typed; null/empty on an edit means "keep the stored password unchanged".
    /// </summary>
    public sealed record SmbConnectionInput(
        string Host,
        int Port,
        string Share,
        string? Domain,
        string Username,
        string? Password,
        string? RootFolder);
}
