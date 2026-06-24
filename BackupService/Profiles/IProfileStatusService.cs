using BackupService.Enumerations;

namespace BackupService.Profiles
{
    /// <summary>
    /// Tracks the live, in-memory run status of each profile (Idle / Running / Error). The status
    /// is deliberately not persisted — it describes the current run, which is only meaningful while
    /// the process is alive (everything starts Idle on boot). The profiles grid reads it per row and
    /// re-renders when <see cref="Changed"/> fires.
    /// </summary>
    public interface IProfileStatusService
    {
        /// <summary>The current status of a profile (Idle when not otherwise set/known).</summary>
        ProfileStatus Get(int profileId);

        /// <summary>Sets a profile's status and raises <see cref="Changed"/>.</summary>
        void Set(int profileId, ProfileStatus status);

        /// <summary>Whether a run is currently in progress for the profile (status is Running).</summary>
        bool IsRunning(int profileId);

        /// <summary>
        /// Atomically begins a run: sets the status to Running and returns true, unless a run is
        /// already in progress (already Running), in which case it makes no change and returns false.
        /// Enforces the "only one run per profile at a time" rule.
        /// </summary>
        bool TryBeginRun(int profileId);

        /// <summary>Drops a profile's tracked status and any lock (call when the profile is deleted).</summary>
        void Remove(int profileId);

        /// <summary>Raised (with the affected profile id) whenever a status changes.</summary>
        event Action<int>? Changed;

        /// <summary>The current run progress (0–100), or null when not running / not yet known.</summary>
        int? GetProgress(int profileId);

        /// <summary>
        /// Records a running profile's progress percentage (clamped 0–100). Raises
        /// <see cref="ProgressChanged"/> only when the integer value actually changes, so a run pushes at
        /// most ~100 cheap UI updates. Cleared automatically when the profile leaves Running.
        /// </summary>
        void SetProgress(int profileId, int percent);

        /// <summary>
        /// Raised (with the affected profile id) when a running profile's percent changes. Distinct from
        /// <see cref="Changed"/> so the grid can update just the cell without reloading the page.
        /// </summary>
        event Action<int>? ProgressChanged;

        /// <summary>
        /// Marks a profile as locked because an admin has it open in a dialog (edit or
        /// delete-confirmation). A locked profile's scheduled run is skipped.
        /// </summary>
        void Lock(int profileId);

        /// <summary>Clears the lock taken by <see cref="Lock"/> (a no-op if not locked).</summary>
        void Unlock(int profileId);

        /// <summary>Whether the profile is currently locked (open in an edit/delete dialog).</summary>
        bool IsLocked(int profileId);
    }
}
