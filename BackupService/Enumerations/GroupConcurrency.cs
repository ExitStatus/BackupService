using System.ComponentModel;

namespace BackupService.Enumerations
{
    /// <summary>
    /// How the profiles in a <see cref="Database.Group"/> behave when more than one wants to run at
    /// the same time.
    /// </summary>
    public enum GroupConcurrency
    {
        /// <summary>Only one profile in the group runs at a time; the rest queue and run in turn.</summary>
        [Description("Sequential")]
        Sequential = 0,

        /// <summary>Profiles run concurrently (subject only to the usual USB-device serialisation).</summary>
        [Description("Parallel")]
        Parallel = 1,
    }
}
