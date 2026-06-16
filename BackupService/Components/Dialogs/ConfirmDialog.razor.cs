using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Dialogs
{
    /// <summary>
    /// Reusable modal confirmation. Set <see cref="Title"/>/<see cref="Message"/> and handle
    /// <see cref="OnConfirm"/>/<see cref="OnCancel"/>. The confirm button uses the danger style.
    /// </summary>
    public partial class ConfirmDialog : ComponentBase
    {
        [Parameter, EditorRequired]
        public string Title { get; set; } = string.Empty;

        [Parameter, EditorRequired]
        public string Message { get; set; } = string.Empty;

        [Parameter]
        public string ConfirmText { get; set; } = "Delete";

        [Parameter]
        public string CancelText { get; set; } = "Cancel";

        [Parameter]
        public EventCallback OnConfirm { get; set; }

        [Parameter]
        public EventCallback OnCancel { get; set; }
    }
}
