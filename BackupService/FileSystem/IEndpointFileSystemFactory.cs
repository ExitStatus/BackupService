namespace BackupService.FileSystem
{
    /// <summary>
    /// A resolved endpoint: the filesystem to use, the base path to start the walk at, and a session to
    /// dispose when done (a no-op for local, the SMB connection for a remote endpoint).
    /// </summary>
    public sealed record EndpointFileSystem(IBackupFileSystem FileSystem, string BasePath, IDisposable Session);

    /// <summary>
    /// Resolves a folder-pair side (a connection id + a configured path) to the filesystem and base path
    /// that should service it. A null connection id means a local path on this machine.
    /// </summary>
    public interface IEndpointFileSystemFactory
    {
        Task<EndpointFileSystem> ResolveAsync(int? connectionId, string configuredPath, CancellationToken cancellationToken = default);
    }
}
