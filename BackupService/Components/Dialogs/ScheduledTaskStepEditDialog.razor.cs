using BackupService.Components.Controls;
using BackupService.Enumerations;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Dialogs
{
    /// <summary>
    /// Modal for adding or editing a single scheduled-task step: name, a Run-via-shell toggle, the
    /// executable/command-line, arguments (direct mode only) and an optional working directory with Browse.
    /// Edits the <see cref="Model"/> instance passed in (the parent supplies a working copy) and returns it
    /// via <see cref="OnSave"/> when valid. Mirrors <see cref="InstantSyncEditDialog"/>.
    /// </summary>
    public partial class ScheduledTaskStepEditDialog : ComponentBase
    {
        [Parameter]
        public ScheduledTaskStepModel Model { get; set; } = default!;

        [Parameter]
        public EventCallback<ScheduledTaskStepModel> OnSave { get; set; }

        [Parameter]
        public EventCallback OnCancel { get; set; }

        private bool _commandError;
        private bool _scriptError;

        /// <summary>The optional working directory, mapping a blank box to null on the model.</summary>
        private string WorkingDirectoryValue
        {
            get => Model.WorkingDirectory ?? string.Empty;
            set => Model.WorkingDirectory = string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private async Task SaveAsync()
        {
            var powerShell = Model.Kind == ScheduledTaskStepKind.PowerShell;
            _commandError = !powerShell && string.IsNullOrWhiteSpace(Model.Command);
            _scriptError = powerShell && string.IsNullOrWhiteSpace(Model.Script);
            if (_commandError || _scriptError)
            {
                return;
            }

            // Tidy up: a blank name is stored as null, and the unused fields for the chosen kind are cleared.
            Model.Name = string.IsNullOrWhiteSpace(Model.Name) ? null : Model.Name.Trim();
            if (powerShell)
            {
                Model.Command = string.Empty;
                Model.Arguments = null;
                Model.RunViaShell = false;
            }
            else
            {
                Model.Script = null;
                Model.Arguments = string.IsNullOrWhiteSpace(Model.Arguments) ? null : Model.Arguments;
            }

            await OnSave.InvokeAsync(Model);
        }
    }
}
