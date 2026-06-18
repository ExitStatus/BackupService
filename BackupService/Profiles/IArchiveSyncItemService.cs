using BackupService.Database;

namespace BackupService.Profiles
{
    /// <summary>
    /// Owns the archive-sync item data within a profile, kept separate from the generic profile
    /// fields (name/description/type/enabled) so a profile save and its item changes stay one unit of
    /// work and one operation log. Mirrors <see cref="IFolderPairService"/> /
    /// <see cref="IInstantSyncItemService"/> — each profile type's item logic lives in its own service
    /// rather than growing <see cref="ProfileService"/>. Stateless: it operates on the tracked
    /// <see cref="Profile"/> graph.
    /// </summary>
    public interface IArchiveSyncItemService
    {
        /// <summary>Builds and adds new items to a freshly created profile.</summary>
        void Add(Profile profile, IReadOnlyList<ArchiveSyncInput> inputs);

        /// <summary>
        /// Reconciles the profile's items with <paramref name="inputs"/> (updates matched ones by id,
        /// adds id-0 ones, removes the rest) and returns human-readable descriptions of the changes
        /// for the update log. The per-item <c>RunCount</c> is preserved across updates.
        /// </summary>
        IReadOnlyList<string> Sync(Profile profile, IReadOnlyList<ArchiveSyncInput> inputs);

        /// <summary>The per-item detail lines describing the items on a create log.</summary>
        IReadOnlyList<string> DescribeForCreateLog(IReadOnlyList<ArchiveSyncInput> inputs);
    }
}
