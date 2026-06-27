using BackupService.Components.Dialogs;
using BackupService.Enumerations;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// Editor for a scheduled task's ordered list of steps: shows each step (name + command preview) with
    /// up/down reorder, edit and delete actions, plus an Add button that opens
    /// <see cref="ScheduledTaskStepEditDialog"/>. Bound to the <see cref="List{T}"/> it mutates in place;
    /// the list order is the run order. Mirrors <see cref="InstantSyncControl"/>.
    /// </summary>
    public partial class ScheduledTaskStepsControl : ComponentBase
    {
        [Parameter]
        public List<ScheduledTaskStepModel> Items { get; set; } = default!;

        private ScheduledTaskStepModel? _editing;
        private int _editIndex = -1; // -1 when adding a new step
        private bool _error;

        private void AddNew()
        {
            _editIndex = -1;
            _editing = new ScheduledTaskStepModel();
        }

        private void Edit(int index)
        {
            _editIndex = index;
            _editing = Clone(Items[index]);
        }

        private void Delete(int index)
        {
            Items.RemoveAt(index);
        }

        private void MoveUp(int index)
        {
            if (index > 0)
            {
                (Items[index - 1], Items[index]) = (Items[index], Items[index - 1]);
            }
        }

        private void MoveDown(int index)
        {
            if (index < Items.Count - 1)
            {
                (Items[index + 1], Items[index]) = (Items[index], Items[index + 1]);
            }
        }

        private void OnEditSaved(ScheduledTaskStepModel model)
        {
            if (_editIndex >= 0)
            {
                Items[_editIndex] = model;
            }
            else
            {
                Items.Add(model);
            }

            _editing = null;
            _error = false;
        }

        /// <summary>Requires at least one step; surfaces an inline message otherwise.</summary>
        public bool Validate()
        {
            _error = Items.Count == 0;
            StateHasChanged();
            return !_error;
        }

        private static string CommandPreview(ScheduledTaskStepModel item) =>
            item.Kind == ScheduledTaskStepKind.PowerShell
                ? "PowerShell script"
                : item.RunViaShell
                    ? $"shell: {item.Command}"
                    : string.IsNullOrWhiteSpace(item.Arguments) ? item.Command : $"{item.Command} {item.Arguments}";

        private static ScheduledTaskStepModel Clone(ScheduledTaskStepModel source) => new()
        {
            Id = source.Id,
            Name = source.Name,
            Kind = source.Kind,
            Command = source.Command,
            Arguments = source.Arguments,
            Script = source.Script,
            WorkingDirectory = source.WorkingDirectory,
            RunViaShell = source.RunViaShell,
        };
    }

    /// <summary>Editable values for a scheduled-task step within a task.</summary>
    public sealed class ScheduledTaskStepModel
    {
        /// <summary>Existing step id, or 0 for a newly added one.</summary>
        public int Id { get; set; }

        public string? Name { get; set; }

        /// <summary>How the step runs (a command/executable, or an inline PowerShell script).</summary>
        public ScheduledTaskStepKind Kind { get; set; } = ScheduledTaskStepKind.Command;

        /// <summary>The executable path, or the full command line when <see cref="RunViaShell"/> is set (Command kind).</summary>
        public string Command { get; set; } = string.Empty;

        public string? Arguments { get; set; }

        /// <summary>The inline PowerShell script body (PowerShell kind).</summary>
        public string? Script { get; set; }

        public string? WorkingDirectory { get; set; }

        public bool RunViaShell { get; set; }
    }
}
