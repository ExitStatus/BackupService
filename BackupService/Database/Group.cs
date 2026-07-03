using System.ComponentModel.DataAnnotations;
using BackupService.Enumerations;

namespace BackupService.Database
{
    /// <summary>
    /// A named group of backup profiles with a shared <see cref="GroupConcurrency"/> that governs how
    /// its members behave when more than one wants to run at once. A profile optionally belongs to a
    /// group (<see cref="Profile.GroupId"/>); deleting a group ungroups its profiles rather than
    /// deleting them (FK <c>ON DELETE SET NULL</c>).
    /// </summary>
    public class Group
    {
        public int Id { get; set; }

        [MaxLength(256)]
        public required string Name { get; set; }

        public GroupConcurrency Concurrency { get; set; }

        public DateTimeOffset DateCreated { get; set; }
    }
}
