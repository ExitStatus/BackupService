using System.ComponentModel.DataAnnotations;
using BackupService.Enumerations;

namespace BackupService.Database
{
    /// <summary>
    /// One step within a <see cref="ScheduledTask"/>. Steps run in ascending <see cref="Order"/>; the run
    /// stops at the first step that exits non-zero. A step is either a <see cref="ScheduledTaskStepKind.Command"/>
    /// (an executable, optionally via the shell) or a <see cref="ScheduledTaskStepKind.PowerShell"/> script.
    /// </summary>
    public class ScheduledTaskStep
    {
        public int Id { get; set; }

        public int ScheduledTaskId { get; set; }

        public ScheduledTask? ScheduledTask { get; set; }

        /// <summary>Position of this step within the task (0-based, ascending = run order).</summary>
        public int Order { get; set; }

        /// <summary>Optional friendly label shown in the UI and prefixed onto the step's log lines.</summary>
        [MaxLength(256)]
        public string? Name { get; set; }

        /// <summary>How this step is executed (a command/executable, or an inline PowerShell script).</summary>
        public ScheduledTaskStepKind Kind { get; set; }

        /// <summary>
        /// When true (Command kind only), <see cref="Command"/> is a single command line run through the OS
        /// shell (<c>cmd.exe /c</c> on Windows, <c>/bin/sh -c</c> elsewhere) and <see cref="Arguments"/> is
        /// ignored. When false, <see cref="Command"/> is an executable path run directly with <see cref="Arguments"/>.
        /// </summary>
        public bool RunViaShell { get; set; }

        /// <summary>
        /// The executable path, or the full command line when <see cref="RunViaShell"/> is set. Null for a
        /// <see cref="ScheduledTaskStepKind.PowerShell"/> step (which uses <see cref="Script"/> instead).
        /// </summary>
        [MaxLength(1024)]
        public string? Command { get; set; }

        /// <summary>Arguments passed to <see cref="Command"/> (ignored when <see cref="RunViaShell"/> or PowerShell).</summary>
        [MaxLength(4000)]
        public string? Arguments { get; set; }

        /// <summary>
        /// The inline PowerShell script body for a <see cref="ScheduledTaskStepKind.PowerShell"/> step (written
        /// to a temp <c>.ps1</c> and run via <c>pwsh</c> or <c>powershell</c>). Null for a Command step.
        /// </summary>
        public string? Script { get; set; }

        /// <summary>Working directory the process runs in; null = inherit the service's.</summary>
        [MaxLength(1024)]
        public string? WorkingDirectory { get; set; }
    }
}
