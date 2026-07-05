using BackupService.Database;
using BackupService.Enumerations;

namespace BackupService.Profiles
{
    /// <summary>
    /// Manages the two-way-sync item data inside a <see cref="ProfileType.TwoWaySync"/> profile — the
    /// counterpart of <see cref="IFolderPairService"/>. Operates on the caller's already-tracked
    /// <see cref="Profile"/> graph (no DbContext) so a profile save and its item changes stay one unit of work.
    /// </summary>
    public interface ITwoWaySyncItemService
    {
        /// <summary>Adds new two-way sync items to a profile being created.</summary>
        void Add(Profile profile, IReadOnlyList<TwoWaySyncInput> inputs);

        /// <summary>
        /// Syncs <paramref name="profile"/>'s two-way sync items to match <paramref name="inputs"/> (updates
        /// matched by id, adds id-0, removes the rest) and returns human-readable change descriptions.
        /// </summary>
        IReadOnlyList<string> Sync(Profile profile, IReadOnlyList<TwoWaySyncInput> inputs);

        /// <summary>Detail lines describing <paramref name="inputs"/> for the profile-created log.</summary>
        IReadOnlyList<string> DescribeForCreateLog(IReadOnlyList<TwoWaySyncInput> inputs);
    }
}
