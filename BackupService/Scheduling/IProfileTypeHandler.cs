using BackupService.Database;
using BackupService.Enumerations;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Per-<see cref="ProfileType"/> executor invoked when a profile's schedule fires. The
    /// dispatcher (<see cref="IBackupRunner"/>) selects the handler whose <see cref="Type"/>
    /// matches the profile and calls <see cref="HandleAsync"/>. Add a profile type =
    /// implement one of these and register it; no scheduler/runner change is needed.
    /// </summary>
    public interface IProfileTypeHandler
    {
        /// <summary>The profile type this handler services.</summary>
        ProfileType Type { get; }

        /// <summary>
        /// Runs the backup work for <paramref name="profile"/>. The profile is supplied with its
        /// <see cref="Profile.FolderPairs"/> already loaded, so the handler has the collection it needs.
        /// </summary>
        Task HandleAsync(Profile profile, CancellationToken cancellationToken);
    }
}
