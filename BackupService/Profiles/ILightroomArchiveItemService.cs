using BackupService.Database;

namespace BackupService.Profiles
{
    /// <summary>
    /// Owns the lightroom-archive item data within a profile, kept separate from the generic profile fields
    /// (and the profile-level Lightroom settings) so a profile save and its item changes stay one unit of work
    /// and one operation log. Mirrors <see cref="IInstantSyncItemService"/>. Stateless: it operates on the
    /// tracked <see cref="Profile"/> graph.
    /// </summary>
    public interface ILightroomArchiveItemService
    {
        /// <summary>Builds and adds new items to a freshly created profile.</summary>
        void Add(Profile profile, IReadOnlyList<LightroomArchiveInput> inputs);

        /// <summary>
        /// Reconciles the profile's items with <paramref name="inputs"/> (updates matched ones by id, adds
        /// id-0 ones, removes the rest) and returns human-readable descriptions of the changes for the update
        /// log.
        /// </summary>
        IReadOnlyList<string> Sync(Profile profile, IReadOnlyList<LightroomArchiveInput> inputs);

        /// <summary>The per-item detail lines describing the items on a create log.</summary>
        IReadOnlyList<string> DescribeForCreateLog(IReadOnlyList<LightroomArchiveInput> inputs);
    }
}
