using System.ComponentModel;

namespace BackupService.Enumerations
{
    /// <summary>
    /// How a <c>Database.ScheduledTaskStep</c> is executed. <see cref="Command"/> is 0 so existing rows
    /// default to it when the column is added.
    /// </summary>
    public enum ScheduledTaskStepKind
    {
        /// <summary>Run an executable (optionally through the OS shell).</summary>
        [Description("Command")]
        Command = 0,

        /// <summary>Run an inline PowerShell script (written to a temp file and run via pwsh/powershell).</summary>
        [Description("PowerShell")]
        PowerShell = 1,
    }
}
