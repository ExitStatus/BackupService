using System.ComponentModel.DataAnnotations;
using BackupService.Components.Controls;
using BackupService.Connections;
using BackupService.Database;
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

        [Inject]
        private IConnectionService ConnectionService { get; set; } = default!;

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
        private readonly List<LightroomArchiveItemModel> _lightroomArchiveItems = [];
        private FolderPairControl? _folderPairControl;
        private InstantSyncControl? _instantSyncControl;
        private ArchiveSyncControl? _archiveSyncControl;
        private LightroomArchiveControl? _lightroomArchiveControl;
        private ScheduleDefinition? _schedule;
        private string? _existingScheduleCron;
        private bool _showSchedule;
        private bool _lightroomFolderError;
        private IReadOnlyDictionary<int, ConnectionType> _connectionTypes = new Dictionary<int, ConnectionType>();

        private static readonly IReadOnlyList<TabBar.TabItem> _tabs =
        [
            new("details", "Details"),
            new("notifications", "Notifications"),
        ];
        private string _activeTab = "details";

        // Both tab panels stay in the DOM (so the type editors' @ref persist); the inactive one is just hidden.
        private string TabStyle(string key) => _activeTab == key ? string.Empty : "display:none";

        // The per-event checkbox label, e.g. "My Backup started" — falls back to "Profile" before a name is typed.
        private string NotifyLabel(string eventName) =>
            $"{(string.IsNullOrWhiteSpace(Input.Name) ? "Profile" : Input.Name)} {eventName}";

        // Shown as a hover tooltip on the "Handle missed sync" checkbox.
        private const string HandleMissedSyncTooltip =
            "If the service was not running at the scheduled time, run this profile immediately when the service next starts.";

        // Shown as a hover tooltip on the LightroomArchive "Raw formats" label/input.
        private const string RawFormatsTooltip =
            "Comma-separated raw file extensions to look for. When a file is copied, the Lightroom folder is searched for a file with the same name and one of these extensions.";

        // Shown as a hover tooltip on the LightroomArchive "RAW folder name" label/input.
        private const string RawFolderNameTooltip =
            "Matching raw files are copied into a sub-folder of this name beside each copied file.";

        private bool IsEdit => ProfileId.HasValue;

        // Watcher-driven types (InstantSync, LightroomArchive) aren't scheduled, so the Schedule and
        // Handle-missed-sync fields are hidden/disabled for them.
        private bool IsWatcherDriven => Input.Type is ProfileType.InstantSync or ProfileType.LightroomArchive;

        // A FolderPair/ArchiveSync profile whose (profile-level) source is a USB connection is run when the
        // device is plugged in, not on a schedule — so the Schedule field is replaced with a note and no cron is
        // saved. Recomputed on render, so it reflects the source connection picked above.
        private bool IsDeviceTriggered =>
            Input.Type is ProfileType.FolderPair or ProfileType.ArchiveSync
            && Input.SourceConnectionId is { } id
            && _connectionTypes.TryGetValue(id, out var type)
            && type == ConnectionType.Usb;

        private string DialogTitle => IsEdit
            ? $"Edit {Input.Type.GetDescription()} Profile"
            : "Create Profile";

        private string IntroText => Input.Type switch
        {
            ProfileType.InstantSync => "An instant sync profile watches each source folder and copies changes to the target as they happen, after a short debounce.",
            ProfileType.ArchiveSync => "An archive sync profile creates a timestamped ZIP of each source folder on a schedule and keeps a retained history in the target folder.",
            ProfileType.LightroomArchive => "A lightroom archive profile watches each source folder like instant sync, and for every copied file also pulls the matching raw originals from the Lightroom folder into a RAW folder beside the copy.",
            _ => "A folder pair profile is a one way copy from the source folder to the target folder for a file oriented backup.",
        };

        // Only shown for scheduled types (watcher-driven types hide the Schedule field entirely).
        private string ScheduleText => _schedule is not null
            ? _schedule.ToHumanReadable()
            : ScheduleDefinition.Describe(_existingScheduleCron);

        protected override async Task OnInitializedAsync()
        {
            // Connection types let the dialog tell when a source is a USB connection (device-triggered).
            _connectionTypes = (await ConnectionService.GetSummariesAsync()).ToDictionary(c => c.Id, c => c.Type);

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
            Input.Type = profile.Type;
            Input.Enabled = profile.Enabled;
            Input.HandleMissedSync = profile.HandleMissedSync;
            Input.SourceConnectionId = profile.SourceConnectionId;
            Input.TargetConnectionId = profile.TargetConnectionId;
            Input.NotificationsEnabled = profile.NotificationsEnabled;
            Input.NotifyOnStart = profile.NotifyOnStart;
            Input.NotifyOnComplete = profile.NotifyOnComplete;
            Input.ShowProgressWindow = profile.ShowProgressWindow;
            Input.LightroomFolder = profile.LightroomFolder ?? string.Empty;
            Input.RawFormats = string.IsNullOrWhiteSpace(profile.RawFormats) ? LightroomArchiveSettings.DefaultRawFormats : profile.RawFormats;
            Input.RawFolderName = string.IsNullOrWhiteSpace(profile.RawFolderName) ? LightroomArchiveSettings.DefaultRawFolderName : profile.RawFolderName;
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
                    Includes = FilterModels(pair.Filters, FilterDirection.Include),
                    Excludes = FilterModels(pair.Filters, FilterDirection.Exclude),
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
                    OnlyCopyOnChange = item.OnlyCopyOnChange,
                    CompressionLevel = item.CompressionLevel,
                    PasswordProtect = item.PasswordProtect,
                    HasExistingPassword = !string.IsNullOrEmpty(item.PasswordEncrypted),
                    EncryptionMethod = item.EncryptionMethod,
                    RetentionMode = item.RetentionMode,
                    RetentionCount = item.RetentionCount,
                    MaxLevels = item.MaxLevels,
                    Includes = FilterModels(item.Filters, FilterDirection.Include),
                    Excludes = FilterModels(item.Filters, FilterDirection.Exclude),
                });
            }

            foreach (var item in profile.LightroomArchiveItems)
            {
                _lightroomArchiveItems.Add(new LightroomArchiveItemModel
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
                ProfileType.LightroomArchive => await SubmitLightroomArchiveAsync(),
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

            // A USB-sourced profile is device-triggered, not scheduled — save it with no cron.
            var scheduleCron = IsDeviceTriggered ? null : _schedule?.ToCron() ?? _existingScheduleCron;

            var folderPairs = _folderPairs
                .Select(p => new FolderPairInput(p.Id, p.Name, p.SourceFolder, p.TargetFolder, p.AllowDeletions, p.IncludeSubFolders, p.OverwriteBehaviour, BuildFilters(p.Includes, p.Excludes)))
                .ToList();

            if (ProfileId is { } id)
            {
                await ProfileService.UpdateAsync(id, Input.Name, scheduleCron, Input.Enabled, folderPairs, handleMissedSync: Input.HandleMissedSync,
                    sourceConnectionId: Input.SourceConnectionId, targetConnectionId: Input.TargetConnectionId,
                    notificationsEnabled: Input.NotificationsEnabled, notifyOnStart: Input.NotifyOnStart, notifyOnComplete: Input.NotifyOnComplete,
                    showProgressWindow: Input.ShowProgressWindow);
            }
            else
            {
                await ProfileService.CreateAsync(Input.Name, ProfileType.FolderPair, scheduleCron, Input.Enabled, folderPairs, handleMissedSync: Input.HandleMissedSync,
                    sourceConnectionId: Input.SourceConnectionId, targetConnectionId: Input.TargetConnectionId,
                    notificationsEnabled: Input.NotificationsEnabled, notifyOnStart: Input.NotifyOnStart, notifyOnComplete: Input.NotifyOnComplete,
                    showProgressWindow: Input.ShowProgressWindow);
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

            // InstantSync isn't scheduled — never carries a cron. Source is local-only (no source connection).
            if (ProfileId is { } id)
            {
                await ProfileService.UpdateAsync(id, Input.Name, scheduleCron: null, Input.Enabled, folderPairs: [], items,
                    targetConnectionId: Input.TargetConnectionId,
                    notificationsEnabled: Input.NotificationsEnabled, notifyOnStart: Input.NotifyOnStart, notifyOnComplete: Input.NotifyOnComplete,
                    showProgressWindow: Input.ShowProgressWindow);
            }
            else
            {
                await ProfileService.CreateAsync(Input.Name, ProfileType.InstantSync, scheduleCron: null, Input.Enabled, folderPairs: [], items,
                    targetConnectionId: Input.TargetConnectionId,
                    notificationsEnabled: Input.NotificationsEnabled, notifyOnStart: Input.NotifyOnStart, notifyOnComplete: Input.NotifyOnComplete,
                    showProgressWindow: Input.ShowProgressWindow);
            }

            return true;
        }

        private async Task<bool> SubmitArchiveSyncAsync()
        {
            if (_archiveSyncControl is null || !_archiveSyncControl.Validate())
            {
                return false;
            }

            // ArchiveSync is scheduled (like FolderPair) unless its source is a USB device (then device-triggered).
            var scheduleCron = IsDeviceTriggered ? null : _schedule?.ToCron() ?? _existingScheduleCron;

            var items = _archiveSyncItems
                .Select(a => new ArchiveSyncInput(a.Id, a.Name, a.SourceFolder, a.TargetFolder, a.FileName, a.IncludeSubFolders, a.OnlyCopyOnChange, a.CompressionLevel, a.PasswordProtect, a.Password, a.EncryptionMethod, a.RetentionMode, a.RetentionCount, a.MaxLevels, BuildFilters(a.Includes, a.Excludes)))
                .ToList();

            if (ProfileId is { } id)
            {
                await ProfileService.UpdateAsync(id, Input.Name, scheduleCron, Input.Enabled, folderPairs: [], instantSyncItems: null, archiveSyncItems: items, handleMissedSync: Input.HandleMissedSync,
                    sourceConnectionId: Input.SourceConnectionId, targetConnectionId: Input.TargetConnectionId,
                    notificationsEnabled: Input.NotificationsEnabled, notifyOnStart: Input.NotifyOnStart, notifyOnComplete: Input.NotifyOnComplete,
                    showProgressWindow: Input.ShowProgressWindow);
            }
            else
            {
                await ProfileService.CreateAsync(Input.Name, ProfileType.ArchiveSync, scheduleCron, Input.Enabled, folderPairs: [], instantSyncItems: null, archiveSyncItems: items, handleMissedSync: Input.HandleMissedSync,
                    sourceConnectionId: Input.SourceConnectionId, targetConnectionId: Input.TargetConnectionId,
                    notificationsEnabled: Input.NotificationsEnabled, notifyOnStart: Input.NotifyOnStart, notifyOnComplete: Input.NotifyOnComplete,
                    showProgressWindow: Input.ShowProgressWindow);
            }

            return true;
        }

        private async Task<bool> SubmitLightroomArchiveAsync()
        {
            // A Lightroom folder is required for this type; surface the error and abort if missing.
            _lightroomFolderError = string.IsNullOrWhiteSpace(Input.LightroomFolder);
            var itemsValid = _lightroomArchiveControl is not null && _lightroomArchiveControl.Validate();
            if (_lightroomFolderError || !itemsValid)
            {
                return false;
            }

            var items = _lightroomArchiveItems
                .Select(i => new LightroomArchiveInput(i.Id, i.Name, i.SourceFolder, i.TargetFolder, i.DebounceMilliseconds, i.IncludeSubFolders, i.AllowDeletions))
                .ToList();

            // LightroomArchive is watcher-driven — never carries a cron. Source is local-only.
            if (ProfileId is { } id)
            {
                await ProfileService.UpdateAsync(id, Input.Name, scheduleCron: null, Input.Enabled,
                    folderPairs: [], instantSyncItems: null, archiveSyncItems: null, lightroomArchiveItems: items,
                    lightroomFolder: Input.LightroomFolder, rawFormats: Input.RawFormats, rawFolderName: Input.RawFolderName,
                    targetConnectionId: Input.TargetConnectionId,
                    notificationsEnabled: Input.NotificationsEnabled, notifyOnStart: Input.NotifyOnStart, notifyOnComplete: Input.NotifyOnComplete,
                    showProgressWindow: Input.ShowProgressWindow);
            }
            else
            {
                await ProfileService.CreateAsync(Input.Name, ProfileType.LightroomArchive, scheduleCron: null, Input.Enabled,
                    folderPairs: [], instantSyncItems: null, archiveSyncItems: null, lightroomArchiveItems: items,
                    lightroomFolder: Input.LightroomFolder, rawFormats: Input.RawFormats, rawFolderName: Input.RawFolderName,
                    targetConnectionId: Input.TargetConnectionId,
                    notificationsEnabled: Input.NotificationsEnabled, notifyOnStart: Input.NotifyOnStart, notifyOnComplete: Input.NotifyOnComplete,
                    showProgressWindow: Input.ShowProgressWindow);
            }

            return true;
        }

        // Split a parent's filter rows into the per-tab UI lists.
        private static List<FilterEntryModel> FilterModels(IEnumerable<FolderPairFilter> filters, FilterDirection direction) =>
            filters.Where(f => f.Direction == direction)
                .Select(f => new FilterEntryModel { Id = f.Id, Kind = f.Kind, Pattern = f.Pattern })
                .ToList();

        private static List<FilterEntryModel> FilterModels(IEnumerable<ArchiveSyncFilter> filters, FilterDirection direction) =>
            filters.Where(f => f.Direction == direction)
                .Select(f => new FilterEntryModel { Id = f.Id, Kind = f.Kind, Pattern = f.Pattern })
                .ToList();

        // Recombine the per-tab UI lists into the flat filter-input list for the service layer.
        private static List<FilterInput> BuildFilters(IEnumerable<FilterEntryModel> includes, IEnumerable<FilterEntryModel> excludes) =>
            includes.Select(e => new FilterInput(e.Id, FilterDirection.Include, e.Kind, e.Pattern))
                .Concat(excludes.Select(e => new FilterInput(e.Id, FilterDirection.Exclude, e.Kind, e.Pattern)))
                .ToList();

        public sealed class InputModel
        {
            [Required]
            public string Name { get; set; } = string.Empty;

            public ProfileType Type { get; set; } = ProfileType.FolderPair;

            public bool Enabled { get; set; } = true;

            /// <summary>Profile-level source connection (null = local). Only meaningful for FolderPair/ArchiveSync.</summary>
            public int? SourceConnectionId { get; set; }

            /// <summary>Profile-level target connection (null = local).</summary>
            public int? TargetConnectionId { get; set; }

            /// <summary>Run immediately on startup if a scheduled run was missed while the service was down.</summary>
            public bool HandleMissedSync { get; set; }

            // Per-profile desktop notifications (master switch + per-event). Defaults match new/migrated profiles.
            public bool NotificationsEnabled { get; set; } = true;

            public bool NotifyOnStart { get; set; }

            public bool NotifyOnComplete { get; set; } = true;

            /// <summary>Show a borderless on-screen progress window while the profile runs (Windows only).</summary>
            public bool ShowProgressWindow { get; set; }

            // LightroomArchive only — the profile-level Lightroom settings shared by all the profile's items.
            public string LightroomFolder { get; set; } = string.Empty;

            public string RawFormats { get; set; } = LightroomArchiveSettings.DefaultRawFormats;

            public string RawFolderName { get; set; } = LightroomArchiveSettings.DefaultRawFolderName;
        }
    }
}
