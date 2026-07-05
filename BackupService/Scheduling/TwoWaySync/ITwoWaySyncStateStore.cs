namespace BackupService.Scheduling.TwoWaySync
{
    /// <summary>One file's last-synced state in a two-way sync baseline manifest.</summary>
    public readonly record struct TwoWaySyncEntry(long WriteTimeUtcTicks, long Size);

    /// <summary>
    /// Persists the per-item <b>baseline</b> for a two-way sync — a manifest of the state of every file that
    /// existed on both sides after the last successful sync (relative path → last-write-time + size). The engine
    /// compares each side against this baseline to tell a deletion from a new file and to detect conflicts.
    ///
    /// File-based (one file per <c>TwoWaySyncItem.Id</c> under <c>{data dir}/two-way-sync</c>), matching the
    /// operation-log store's precedent of keeping high-cardinality per-item data out of the database.
    /// </summary>
    public interface ITwoWaySyncStateStore
    {
        /// <summary>The baseline for an item, keyed by forward-slash relative path. Empty when there is none yet.</summary>
        Task<Dictionary<string, TwoWaySyncEntry>> LoadAsync(int itemId, CancellationToken cancellationToken = default);

        /// <summary>Atomically replaces the item's baseline (temp file then rename).</summary>
        Task SaveAsync(int itemId, IReadOnlyDictionary<string, TwoWaySyncEntry> entries, CancellationToken cancellationToken = default);

        /// <summary>Removes the item's baseline (best-effort; a missing file is not an error).</summary>
        void Delete(int itemId);
    }
}
