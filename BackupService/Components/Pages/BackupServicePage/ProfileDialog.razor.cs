using System.ComponentModel.DataAnnotations;
using BackupService.Components.Controls;
using BackupService.Enumerations;
using BackupService.Profiles;
using BackupService.Scheduling;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.BackupServicePage
{
    /// <summary>
    /// Self-contained modal for creating or editing a backup profile. With no
    /// <see cref="ProfileId"/> it creates; with one it loads that profile and saves changes.
    /// The profile type selects which editor is shown (FolderPair → <see cref="FolderPairControl"/>)
    /// and cannot be changed while editing.
    /// </summary>
    public partial class ProfileDialog : ComponentBase
    {
        [Inject]
        private IProfileService ProfileService { get; set; } = default!;

        /// <summary>When set, the dialog edits this profile; otherwise it creates a new one.</summary>
        [Parameter]
        public int? ProfileId { get; set; }

        [Parameter]
        public EventCallback OnCancel { get; set; }

        [Parameter]
        public EventCallback OnSaved { get; set; }

        private InputModel Input { get; set; } = new();
        private readonly FolderPairModel _folderPair = new();
        private FolderPairControl? _folderPairControl;
        private ScheduleDefinition? _schedule;
        private string? _existingScheduleCron;
        private bool _showSchedule;

        private bool IsEdit => ProfileId.HasValue;

        private string ScheduleText => _schedule is not null
            ? _schedule.ToHumanReadable()
            : ScheduleDefinition.Describe(_existingScheduleCron);

        protected override async Task OnInitializedAsync()
        {
            if (ProfileId is not { } id)
            {
                return;
            }

            var profile = await ProfileService.GetAsync(id);
            if (profile is null)
            {
                return;
            }

            Input.Name = profile.Name;
            Input.Description = profile.Description;
            Input.Type = profile.Type;
            _existingScheduleCron = profile.Schedule;
            // Parse the stored cron back into the builder so the schedule shows human-readable
            // and the schedule dialog opens pre-filled.
            _schedule = ScheduleDefinition.FromCron(profile.Schedule);

            var pair = profile.FolderPairs.FirstOrDefault();
            if (pair is not null)
            {
                _folderPair.SourceFolder = pair.SourceFolder;
                _folderPair.TargetFolder = pair.TargetFolder;
                _folderPair.WatchFolder = pair.WatchFolder;
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
            if (Input.Type == ProfileType.FolderPair)
            {
                if (_folderPairControl is null || !_folderPairControl.Validate())
                {
                    return;
                }

                // Keep the existing schedule when the user hasn't built a new one.
                var scheduleCron = _schedule?.ToCron() ?? _existingScheduleCron;

                if (ProfileId is { } id)
                {
                    await ProfileService.UpdateAsync(
                        id,
                        Input.Name,
                        Input.Description,
                        _folderPair.SourceFolder,
                        _folderPair.TargetFolder,
                        _folderPair.WatchFolder,
                        scheduleCron);
                }
                else
                {
                    await ProfileService.CreateAsync(
                        Input.Name,
                        Input.Description,
                        ProfileType.FolderPair,
                        _folderPair.SourceFolder,
                        _folderPair.TargetFolder,
                        _folderPair.WatchFolder,
                        scheduleCron);
                }
            }

            await OnSaved.InvokeAsync();
        }

        public sealed class InputModel
        {
            [Required]
            public string Name { get; set; } = string.Empty;

            public string? Description { get; set; }

            public ProfileType Type { get; set; } = ProfileType.FolderPair;
        }
    }
}
