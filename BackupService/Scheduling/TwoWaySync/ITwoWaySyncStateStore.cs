namespace BackupService.Scheduling.TwoWaySync
{
    /// <summary>One file's last-synced state in a two-way sync baseline manifest.</summary>
    public readonly record struct TwoWaySyncEntry(long WriteTimeUtcTicks, long Size);

    /// <summary>
    /// A loaded baseline. <see cref="WasReset"/> is true when a manifest existed but described a different folder
    /// pair (the item was re-pointed) and was therefore discarded.
    /// </summary>
    public sealed record TwoWaySyncBaseline(Dictionary<string, TwoWaySyncEntry> Entries, bool WasReset);

    /// <summary>
    /// Persists the per-item <b>baseline</b> for a two-way sync — a manifest of the state of every file that
    /// existed on both sides after the last successful sync (relative path → last-write-time + size). The engine
    /// compares each side against this baseline to tell a deletion from a new file and to detect conflicts.
    ///
    /// File-based (one file per <c>TwoWaySyncItem.Id</c> under <c>{data dir}/two-way-sync</c>), matching the
    /// operation-log store's precedent of keeping high-cardinality per-item data out of the database.
    ///
    /// A baseline is only valid for the folder pair it was built from, so each manifest is stamped with an
    /// <c>endpointKey</c> (both sides' connection + folder). Loading with a different key returns an empty baseline:
    /// comparing a new folder against the old pair's state would read every file missing from it as deleted.
    /// </summary>
    public interface ITwoWaySyncStateStore
    {
        /// <summary>
        /// The baseline for an item, keyed by forward-slash relative path — empty when there is none yet, or when the
        /// stored one was built for a different <paramref name="endpointKey"/> (then <see cref="TwoWaySyncBaseline.WasReset"/>).
        /// </summary>
        Task<TwoWaySyncBaseline> LoadAsync(int itemId, string endpointKey, CancellationToken cancellationToken = default);

        /// <summary>Atomically replaces the item's baseline (temp file then rename), stamped with <paramref name="endpointKey"/>.</summary>
        Task SaveAsync(int itemId, string endpointKey, IReadOnlyDictionary<string, TwoWaySyncEntry> entries, CancellationToken cancellationToken = default);

        /// <summary>Removes the item's baseline (best-effort; a missing file is not an error).</summary>
        void Delete(int itemId);
    }
}
