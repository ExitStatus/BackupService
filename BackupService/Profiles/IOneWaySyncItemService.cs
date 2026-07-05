using BackupService.Database;
using BackupService.Enumerations;

namespace BackupService.Profiles
{
    /// <summary>
    /// Manages the folder-pair data inside a <see cref="ProfileType.OneWaySync"/> profile:
    /// building new pairs on create, syncing the set on update, and describing both for the
    /// profile's operation log. It operates on the caller's already-tracked <see cref="Profile"/>
    /// entity graph and owns no DbContext of its own, so a profile save and its folder-pair
    /// changes remain a single unit of work (and a single operation log) in
    /// <see cref="IProfileService"/>.
    ///
    /// OneWaySyncItem is just one of several planned profile types; keeping its data logic here (rather
    /// than in <see cref="ProfileService"/>) is what lets a new type add its own equivalent service.
    /// </summary>
    public interface IOneWaySyncItemService
    {
        /// <summary>Adds new one way sync items to a profile being created.</summary>
        void Add(Profile profile, IReadOnlyList<OneWaySyncInput> inputs);

        /// <summary>
        /// Syncs <paramref name="profile"/>'s one way sync items to match <paramref name="inputs"/>
        /// (updates matched ones by id, adds id-0 ones, removes the rest) and returns
        /// human-readable descriptions of what changed, for the update log.
        /// </summary>
        IReadOnlyList<string> Sync(Profile profile, IReadOnlyList<OneWaySyncInput> inputs);

        /// <summary>Detail lines describing <paramref name="inputs"/> for the profile-created log.</summary>
        IReadOnlyList<string> DescribeForCreateLog(IReadOnlyList<OneWaySyncInput> inputs);
    }
}
