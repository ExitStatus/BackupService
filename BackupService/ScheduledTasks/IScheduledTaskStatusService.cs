using BackupService.Enumerations;

namespace BackupService.ScheduledTasks
{
    /// <summary>
    /// Tracks the live, in-memory run status of each scheduled task (Idle / Running / Error). A separate
    /// instance from <c>IProfileStatusService</c> so task ids and profile ids don't collide. Status is not
    /// persisted — everything starts Idle on boot. The grid reads it per row and re-renders on <see cref="Changed"/>.
    /// </summary>
    public interface IScheduledTaskStatusService
    {
        /// <summary>The current status of a task (Idle when not otherwise set/known).</summary>
        ProfileStatus Get(int taskId);

        /// <summary>Sets a task's status and raises <see cref="Changed"/>.</summary>
        void Set(int taskId, ProfileStatus status);

        /// <summary>Whether a run is currently in progress for the task (status is Running).</summary>
        bool IsRunning(int taskId);

        /// <summary>
        /// Atomically begins a run: sets the status to Running and returns true, unless a run is already in
        /// progress, in which case it makes no change and returns false. Enforces "one run per task at a time".
        /// </summary>
        bool TryBeginRun(int taskId);

        /// <summary>Drops a task's tracked status and any lock (call when the task is deleted).</summary>
        void Remove(int taskId);

        /// <summary>Raised (with the affected task id) whenever a status changes.</summary>
        event Action<int>? Changed;

        /// <summary>
        /// Marks a task as locked because an admin has it open in an edit/delete dialog. A locked task's
        /// scheduled run is skipped.
        /// </summary>
        void Lock(int taskId);

        /// <summary>Clears the lock taken by <see cref="Lock"/> (a no-op if not locked).</summary>
        void Unlock(int taskId);

        /// <summary>Whether the task is currently locked (open in an edit/delete dialog).</summary>
        bool IsLocked(int taskId);
    }
}
