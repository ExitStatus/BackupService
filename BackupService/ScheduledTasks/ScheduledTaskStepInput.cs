using BackupService.Enumerations;

namespace BackupService.ScheduledTasks
{
    /// <summary>
    /// A scheduled-task step supplied when creating or updating a task. <see cref="Id"/> is 0 for a new
    /// step, or the existing <c>ScheduledTaskStep.Id</c> when updating one. A Command step uses
    /// <see cref="Command"/>/<see cref="Arguments"/>/<see cref="RunViaShell"/>; a PowerShell step uses
    /// <see cref="Script"/>.
    /// </summary>
    public sealed record ScheduledTaskStepInput(
        int Id,
        int Order,
        string? Name,
        string? Command,
        string? Arguments,
        string? WorkingDirectory,
        bool RunViaShell,
        ScheduledTaskStepKind Kind = ScheduledTaskStepKind.Command,
        string? Script = null);
}
