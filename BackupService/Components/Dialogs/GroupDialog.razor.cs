using System.ComponentModel.DataAnnotations;
using BackupService.Enumerations;
using BackupService.Groups;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Dialogs
{
    /// <summary>
    /// Self-contained modal for creating or editing a profile group. With no <see cref="GroupId"/> it
    /// creates; with one it loads that group and saves changes (mirrors <see cref="ConnectionDialog"/>).
    /// </summary>
    public partial class GroupDialog : ComponentBase
    {
        [Inject]
        private IGroupService GroupService { get; set; } = default!;

        /// <summary>When set, the dialog edits this group; otherwise it creates a new one.</summary>
        [Parameter]
        public int? GroupId { get; set; }

        [Parameter]
        public EventCallback OnCancel { get; set; }

        [Parameter]
        public EventCallback OnSaved { get; set; }

        private InputModel Input { get; set; } = new();
        private bool _saving;

        private bool IsEdit => GroupId.HasValue;

        private static readonly IReadOnlyList<GroupConcurrency> ConcurrencyOptions =
            Enum.GetValues<GroupConcurrency>();

        protected override async Task OnInitializedAsync()
        {
            if (GroupId is not { } id)
            {
                return;
            }

            var group = await GroupService.GetAsync(id);
            if (group is null)
            {
                return;
            }

            Input.Name = group.Name;
            Input.Concurrency = group.Concurrency;
        }

        private async Task SubmitAsync()
        {
            // A second submit (double-click) arrives while the first awaits the database — it would create the group twice.
            if (_saving)
            {
                return;
            }

            var name = Input.Name.Trim();
            _saving = true;
            try
            {
                if (GroupId is { } id)
                {
                    await GroupService.UpdateAsync(id, name, Input.Concurrency);
                }
                else
                {
                    await GroupService.CreateAsync(name, Input.Concurrency);
                }

                await OnSaved.InvokeAsync();
            }
            finally
            {
                _saving = false;
            }
        }

        public sealed class InputModel
        {
            [Required]
            public string Name { get; set; } = string.Empty;

            public GroupConcurrency Concurrency { get; set; } = GroupConcurrency.Sequential;
        }
    }
}
