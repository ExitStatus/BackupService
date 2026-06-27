using System.ComponentModel.DataAnnotations;
using BackupService.Components.Controls;
using BackupService.Enumerations;
using BackupService.ScheduledTasks;
using BackupService.Scheduling;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Dialogs
{
    /// <summary>
    /// Self-contained modal for creating or editing a scheduled task. With no <see cref="TaskId"/> it
    /// creates; with one it loads that task and saves changes. Hosts the shared <see cref="ScheduleDialog"/>
    /// and the <see cref="ScheduledTaskStepsControl"/> step editor. Mirrors <see cref="ProfileDialog"/>.
    /// </summary>
    public partial class ScheduledTaskDialog : ComponentBase
    {
        [Inject]
        private IScheduledTaskService TaskService { get; set; } = default!;

        /// <summary>When set, the dialog edits this task; otherwise it creates a new one.</summary>
        [Parameter]
        public int? TaskId { get; set; }

        [Parameter]
        public EventCallback OnCancel { get; set; }

        [Parameter]
        public EventCallback OnSaved { get; set; }

        private InputModel Input { get; set; } = new();
        private readonly List<ScheduledTaskStepModel> _steps = [];
        private ScheduledTaskStepsControl? _stepsControl;
        private ScheduleDefinition? _schedule;
        private string? _existingScheduleCron;
        private bool _showSchedule;

        private const string HandleMissedSyncTooltip =
            "If the service was not running at the scheduled time, run this task immediately when the service next starts.";

        private bool IsEdit => TaskId.HasValue;

        private string DialogTitle => IsEdit ? "Edit Scheduled Task" : "Create Scheduled Task";

        private string ScheduleText => _schedule is not null
            ? _schedule.ToHumanReadable()
            : ScheduleDefinition.Describe(_existingScheduleCron);

        protected override async Task OnInitializedAsync()
        {
            if (TaskId is not { } id)
            {
                return;
            }

            var task = await TaskService.GetAsync(id);
            if (task is null)
            {
                return;
            }

            Input.Name = task.Name;
            Input.Description = task.Description;
            Input.Enabled = task.Enabled;
            Input.HandleMissedSync = task.HandleMissedSync;
            _existingScheduleCron = task.Schedule;
            _schedule = ScheduleDefinition.FromCron(task.Schedule);

            foreach (var step in task.Steps.OrderBy(s => s.Order))
            {
                _steps.Add(new ScheduledTaskStepModel
                {
                    Id = step.Id,
                    Name = step.Name,
                    Kind = step.Kind,
                    Command = step.Command ?? string.Empty,
                    Arguments = step.Arguments,
                    Script = step.Script,
                    WorkingDirectory = step.WorkingDirectory,
                    RunViaShell = step.RunViaShell,
                });
            }
        }

        private void OpenSchedule() => _showSchedule = true;

        private void OnScheduleApplied(ScheduleDefinition definition)
        {
            _schedule = definition;
            _showSchedule = false;
        }

        private async Task SubmitAsync()
        {
            if (_stepsControl is null || !_stepsControl.Validate())
            {
                return;
            }

            // Keep the existing schedule when the user hasn't built a new one.
            var scheduleCron = _schedule?.ToCron() ?? _existingScheduleCron;

            // The list order is the run order — number the steps from their position.
            var steps = _steps
                .Select((s, index) => new ScheduledTaskStepInput(
                    s.Id, index, s.Name,
                    s.Kind == ScheduledTaskStepKind.PowerShell ? null : s.Command,
                    s.Arguments, s.WorkingDirectory, s.RunViaShell, s.Kind, s.Script))
                .ToList();

            if (TaskId is { } id)
            {
                await TaskService.UpdateAsync(id, Input.Name, Input.Description, scheduleCron, Input.Enabled, steps, Input.HandleMissedSync);
            }
            else
            {
                await TaskService.CreateAsync(Input.Name, Input.Description, scheduleCron, Input.Enabled, steps, Input.HandleMissedSync);
            }

            await OnSaved.InvokeAsync();
        }

        public sealed class InputModel
        {
            [Required]
            public string Name { get; set; } = string.Empty;

            public string? Description { get; set; }

            public bool Enabled { get; set; } = true;

            /// <summary>Run immediately on startup if a scheduled run was missed while the service was down.</summary>
            public bool HandleMissedSync { get; set; }
        }
    }
}
