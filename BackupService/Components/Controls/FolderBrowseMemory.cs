namespace BackupService.Components.Controls
{
    /// <summary>
    /// Per-profile-edit-session memory of the last folder browsed to, keyed by connection id (null = the
    /// local machine). While a profile is being created or edited, opening the folder picker for a
    /// subsequent backup item starts where the previous browse ended, instead of back at This PC / the
    /// connection root. Provided as a cascading value by <c>ProfileDialog</c> and consumed by
    /// <see cref="ConnectionLocationField"/>; a fresh instance per dialog scopes it to that one session.
    /// </summary>
    public sealed class FolderBrowseMemory
    {
        // The local machine (null connection id) is keyed under this sentinel — connection ids are positive
        // autoincrement, so it can't collide. Keeps the dictionary key non-nullable.
        private const int LocalKey = int.MinValue;

        // The last path browsed for each location. The value's meaning matches how that location's picker
        // reports a selection: an absolute path for local, or a root-relative path for a remote/USB connection.
        private readonly Dictionary<int, string> _lastPaths = [];

        /// <summary>The last folder browsed for the given connection (null = local), or null if none yet.</summary>
        public string? Get(int? connectionId) =>
            _lastPaths.TryGetValue(connectionId ?? LocalKey, out var path) ? path : null;

        /// <summary>Records the last folder browsed for the given connection (blank clears it).</summary>
        public void Set(int? connectionId, string? path)
        {
            var key = connectionId ?? LocalKey;
            if (string.IsNullOrWhiteSpace(path))
            {
                _lastPaths.Remove(key);
            }
            else
            {
                _lastPaths[key] = path;
            }
        }
    }
}
