using System.ComponentModel.DataAnnotations;
using BackupService.Components.Controls;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.Profiles;
using BackupService.Scheduling;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Dialogs
{
    /// <summary>
    /// Self-contained modal for creating or editing a backup profile. With no
    /// <see cref="ProfileId"/> it creates; with one it loads that profile and saves changes.
    /// The profile type selects which editor is shown (FolderPair â†’ <see cref="FolderPairControl"/>,
    /// which manages the profile's list of folder pairs) and cannot be changed while editing.
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
        private readonly List<FolderPairModel> _folderPairs = [];
        private readonly List<InstantSyncItemModel> _instantSyncItems = [];
        private readonly List<ArchiveSyncItemModel> _archiveSyncItems = [];
        private FolderPairControl? _folderPairControl;
        private InstantSyncControl? _instantSyncControl;
        private ArchiveSyncControl? _archiveSyncControl;
        private ScheduleDefinition? _schedule;
        private string? _existingScheduleCron;
        private bool _showSchedule;

        private bool IsEdit => ProfileId.HasValue;

        private bool IsInstantSync => Input.Type == ProfileType.InstantSync;

        private string DialogTitle => IsEdit
            ? $"Edit {Input.Type.GetDescription()} Profile"
            : "Create Profile";

        private string IntroText => Input.Type switch
        {
            ProfileType.InstantSync => "An instant sync profile watches each source folder and copies changes to the target as they happen, after a short debounce.",
            ProfileType.ArchiveSync => "An archive sync profile creates a timestamped ZIP of each source folder on a schedule and keeps a retained history in the target folder.",
            _ => "A folder pair profile is a one way copy from the source folder to the target folder for a file oriented backup.",
        };

        // InstantSync profiles are watcher-driven, not scheduled.
        private string ScheduleText => IsInstantSync
            ? "Not used for Instant Sync"
            : _schedule is not null
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
            Input.Enabled = profile.Enabled;
            _existingScheduleCron = profile.Schedule;
            // Parse the stored cron back into the builder so the schedule shows human-readable
            // and the schedule dialog opens pre-filled.
            _schedule = ScheduleDefinition.FromCron(profile.Schedule);

            foreach (var pair in profile.FolderPairs)
            {
                _folderPairs.Add(new FolderPairModel
                {
                    Id = pair.Id,
                    Name = pair.Name,
                    SourceFolder = pair.SourceFolder,
                    TargetFolder = pair.TargetFolder,
                    AllowDeletions = pair.AllowDeletions,
                    IncludeSubFolders = pair.IncludeSubFolders,
                    OverwriteBehaviour = pair.OverwriteBehaviour,
                });
            }

            foreach (var item in profile.InstantSyncItems)
            {
                _instantSyncItems.Add(new InstantSyncItemModel
                {
                    Id = item.Id,
                    Name = item.Name,
                    SourceFolder = item.SourceFolder,
                    TargetFolder = item.TargetFolder,
                    DebounceMilliseconds = item.DebounceMilliseconds,
                    IncludeSubFolders = item.IncludeSubFolders,
                    AllowDeletions = item.AllowDeletions,
                });
            }

            foreach (var item in profile.ArchiveSyncItems)
            {
                _archiveSyncItems.Add(new ArchiveSyncItemModel
                {
                    Id = item.Id,
                    Name = item.Name,
                    SourceFolder = item.SourceFolder,
                    TargetFolder = item.TargetFolder,
                    FileName = item.FileName,
                    IncludeSubFolders = item.IncludeSubFolders,
                    RetentionMode = item.RetentionMode,
                    RetentionCount = item.RetentionCount,
                    MaxLevels = item.MaxLevels,
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
            var saved = Input.Type switch
            {
                ProfileType.InstantSync => await SubmitInstantSyncAsync(),
                ProfileType.ArchiveSync => await SubmitArchiveSyncAsync(),
                _ => await SubmitFolderPairAsync(),
            };

            if (!saved)
            {
                return;
            }

            await OnSaved.InvokeAsync();
        }

        private async Task<bool> SubmitFolderPairAsync()
        {
            if (_folderPairControl is null || !_folderPairControl.Validate())
            {
                return false;
            }

            // Keep the existing schedule when the user hasn't built a new one.
            var scheduleCron = _schedule?.ToCron() ?? _existingScheduleCron;

            var folderPairs = _folderPairs
                .Select(p => new FolderPairInput(p.Id, p.Name, p.SourceFolder, p.TargetFolder, p.AllowDeletions, p.IncludeSubFolders, p.OverwriteBehaviour))
                .ToList();

            if (ProfileId is { } id)
            {
                await ProfileService.UpdateAsync(id, Input.Name, Input.Description, scheduleCron, Input.Enabled, folderPairs);
            }
            else
            {
                await ProfileService.CreateAsync(Input.Name, Input.Description, ProfileType.FolderPair, scheduleCron, Input.Enabled, folderPairs);
            }

            return true;
        }

        private async Task<bool> SubmitInstantSyncAsync()
        {
            if (_instantSyncControl is null || !_instantSyncControl.Validate())
            {
                return false;
            }

            var items = _instantSyncItems
                .Select(i => new InstantSyncInput(i.Id, i.Name, i.SourceFolder, i.TargetFolder, i.DebounceMilliseconds, i.IncludeSubFolders, i.AllowDeletions))
                .ToList();

            // InstantSync isn't scheduled — never carries a cron.
            if (ProfileId is { } id)
            {
                await ProfileService.UpdateAsync(id, Input.Name, Input.Description, scheduleCron: null, Input.Enabled, folderPairs: [], items);
            }
            else
            {
                await ProfileService.CreateAsync(Input.Name, Input.Description, ProfileType.InstantSync, scheduleCron: null, Input.Enabled, folderPairs: [], items);
            }

            return true;
        }

        private async Task<bool> SubmitArchiveSyncAsync()
        {
            if (_archiveSyncControl is null || !_archiveSyncControl.Validate())
            {
                return false;
            }

            // ArchiveSync is scheduled (like FolderPair): keep the existing schedule when unchanged.
            var scheduleCron = _schedule?.ToCron() ?? _existingScheduleCron;

            var items = _archiveSyncItems
                .Select(a => new ArchiveSyncInput(a.Id, a.Name, a.SourceFolder, a.TargetFolder, a.FileName, a.IncludeSubFolders, a.RetentionMode, a.RetentionCount, a.MaxLevels))
                .ToList();

            if (ProfileId is { } id)
            {
                await ProfileService.UpdateAsync(id, Input.Name, Input.Description, scheduleCron, Input.Enabled, folderPairs: [], instantSyncItems: null, archiveSyncItems: items);
            }
            else
            {
                await ProfileService.CreateAsync(Input.Name, Input.Description, ProfileType.ArchiveSync, scheduleCron, Input.Enabled, folderPairs: [], instantSyncItems: null, archiveSyncItems: items);
            }

            return true;
        }

        public sealed class InputModel
        {
            [Required]
            public string Name { get; set; } = string.Empty;

            public string? Description { get; set; }

            public ProfileType Type { get; set; } = ProfileType.FolderPair;

            public bool Enabled { get; set; } = true;
        }
    }
}
