using BackupService.Connections.Smb;
using Google.Apis.Drive.v3;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace BackupService.Connections.GoogleDrive
{
    /// <summary>
    /// Default <see cref="IGoogleDriveConnector"/> over the Google Drive v3 API. Resolves the requested
    /// name-path to a folder id (walking from My Drive root) and lists/creates child folders.
    /// </summary>
    public sealed class GoogleDriveConnector(ILogger<GoogleDriveConnector> logger) : IGoogleDriveConnector
    {
        internal const string FolderMimeType = "application/vnd.google-apps.folder";

        public async Task<ConnectionTestResult> TestAsync(GoogleDriveConnectionInfo info, CancellationToken cancellationToken = default)
        {
            try
            {
                using var drive = GoogleDriveServiceFactory.Create(info);

                var about = drive.About.Get();
                about.Fields = "user(emailAddress)";
                var response = await about.ExecuteAsync(cancellationToken);
                var email = response.User?.EmailAddress;

                // Confirm the configured root folder is reachable (catches a bad RootFolder up front).
                await ResolveFolderIdAsync(drive, info.RootFolder, cancellationToken);

                return ConnectionTestResult.Success(
                    string.IsNullOrEmpty(email) ? "Connected successfully." : $"Connected as {email}.");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Google Drive test connection failed.");
                return ConnectionTestResult.Failure($"Connection failed: {ex.Message}");
            }
        }

        public async Task<IReadOnlyList<string>> ListDirectoriesAsync(GoogleDriveConnectionInfo info, string relativePath, CancellationToken cancellationToken = default)
        {
            using var drive = GoogleDriveServiceFactory.Create(info);
            var parentId = await ResolveFolderIdAsync(drive, relativePath, cancellationToken);

            var names = new List<string>();
            string? pageToken = null;
            do
            {
                var list = drive.Files.List();
                list.Q = $"'{Escape(parentId)}' in parents and mimeType = '{FolderMimeType}' and trashed = false";
                list.Fields = "nextPageToken, files(name)";
                list.PageSize = 1000;
                list.PageToken = pageToken;
                var response = await list.ExecuteAsync(cancellationToken);
                names.AddRange(response.Files.Select(f => f.Name));
                pageToken = response.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        public async Task CreateDirectoryAsync(GoogleDriveConnectionInfo info, string relativePath, CancellationToken cancellationToken = default)
        {
            var normalized = Normalize(relativePath);
            if (normalized.Length == 0)
            {
                throw new InvalidOperationException("A folder name is required.");
            }

            using var drive = GoogleDriveServiceFactory.Create(info);

            var index = normalized.LastIndexOf('\\');
            var parentPath = index < 0 ? string.Empty : normalized[..index];
            var name = index < 0 ? normalized : normalized[(index + 1)..];

            var parentId = await ResolveFolderIdAsync(drive, parentPath, cancellationToken);
            if (await FindChildFolderIdAsync(drive, parentId, name, cancellationToken) is not null)
            {
                throw new InvalidOperationException($"A folder named '{name}' already exists here.");
            }

            var metadata = new DriveFile { Name = name, MimeType = FolderMimeType, Parents = [parentId] };
            var create = drive.Files.Create(metadata);
            create.Fields = "id";
            await create.ExecuteAsync(cancellationToken);
        }

        public async Task<StorageSpace?> GetFreeSpaceAsync(GoogleDriveConnectionInfo info, CancellationToken cancellationToken = default)
        {
            try
            {
                using var drive = GoogleDriveServiceFactory.Create(info);

                var about = drive.About.Get();
                about.Fields = "storageQuota";
                var response = await about.ExecuteAsync(cancellationToken);

                var quota = response.StorageQuota;
                if (quota is null)
                {
                    return null;
                }
                // No limit ⇒ unlimited (e.g. some Workspace accounts). Usage is total Google usage, which is
                // what the limit applies to.
                if (quota.Limit is not { } limit)
                {
                    return new StorageSpace(null, null, Unlimited: true);
                }

                var free = Math.Max(0, limit - (quota.Usage ?? 0));
                return new StorageSpace(limit, free, Unlimited: false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read Google Drive storage quota.");
                return null;
            }
        }

        // Walks a name-path from My Drive root to a folder id, failing if a segment doesn't exist.
        private static async Task<string> ResolveFolderIdAsync(DriveService drive, string? path, CancellationToken cancellationToken)
        {
            var id = "root";
            foreach (var segment in Normalize(path).Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                id = await FindChildFolderIdAsync(drive, id, segment, cancellationToken)
                    ?? throw new InvalidOperationException($"Folder '{path}' was not found.");
            }
            return id;
        }

        private static async Task<string?> FindChildFolderIdAsync(DriveService drive, string parentId, string name, CancellationToken cancellationToken)
        {
            var list = drive.Files.List();
            list.Q = $"name = '{Escape(name)}' and '{Escape(parentId)}' in parents and mimeType = '{FolderMimeType}' and trashed = false";
            list.Fields = "files(id)";
            list.PageSize = 10;
            var response = await list.ExecuteAsync(cancellationToken);
            return response.Files.FirstOrDefault()?.Id;
        }

        // Drive query string literals are single-quoted; escape backslashes and quotes within a value.
        internal static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

        private static string Normalize(string? path) =>
            string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('/', '\\').Trim('\\');
    }
}
