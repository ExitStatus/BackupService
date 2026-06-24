using System.Diagnostics;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Logging;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Background service that keeps a live <see cref="FileSystemWatcher"/> on each item of every enabled
    /// <see cref="ProfileType.LightroomArchive"/> profile, mirroring changes into the target (plus the matching
    /// raw sidecars) after a per-item debounce window. Also the <see cref="ILightroomArchiveManager"/> the rest
    /// of the app calls to keep the watchers in step with profile changes. Registered once and shared in all
    /// three roles (singleton, <see cref="ILightroomArchiveManager"/>, hosted service) — the LightroomArchive
    /// counterpart to <see cref="InstantSyncWatcherService"/>.
    ///
    /// Unlike the instant-sync watcher there is no local/remote split: <see cref="ILightroomArchiveProcessor"/>
    /// is endpoint-aware, so the watcher always flushes through it regardless of the target.
    /// </summary>
    public sealed class LightroomArchiveWatcherService(
        IDatabaseContextFactory contextFactory,
        ILightroomArchiveProcessor processor,
        IOperationLogFactory operationLogFactory,
        IBackupRunRecorder runRecorder,
        ILogger<LightroomArchiveWatcherService> logger) : BackgroundService, ILightroomArchiveManager
    {
        private readonly Dictionary<int, List<ItemWatcher>> _watchers = new();
        private readonly object _lock = new();

        private CancellationToken _stoppingToken;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _stoppingToken = stoppingToken;
            logger.LogInformation("Lightroom archive watcher service started.");

            await LoadAllAsync(stoppingToken);

            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }

            DisposeAllWatchers();
            logger.LogInformation("Lightroom archive watcher service stopping.");
        }

        public async Task SyncAsync(int profileId, CancellationToken cancellationToken = default)
        {
            var profile = await ReadProfileAsync(profileId, cancellationToken);

            lock (_lock)
            {
                RemoveWatchers(profileId);

                if (profile is { Enabled: true, Type: ProfileType.LightroomArchive })
                {
                    RegisterProfile(profile);
                }
            }
        }

        /// <summary>Diagnostic/test helper: number of live item watchers for a profile.</summary>
        public int WatcherCount(int profileId)
        {
            lock (_lock)
            {
                return _watchers.TryGetValue(profileId, out var list) ? list.Count : 0;
            }
        }

        private async Task LoadAllAsync(CancellationToken cancellationToken)
        {
            List<Profile> profiles;
            await using (var db = contextFactory.CreateDbContext())
            {
                profiles = await db.Profiles
                    .AsNoTracking()
                    .Include(p => p.LightroomArchiveItems)
                    .Where(p => p.Enabled && p.Type == ProfileType.LightroomArchive)
                    .ToListAsync(cancellationToken);
            }

            lock (_lock)
            {
                DisposeAllWatchers();
                foreach (var profile in profiles)
                {
                    RegisterProfile(profile);
                }
            }

            logger.LogInformation("Lightroom archive watcher service loaded {Count} profile(s).", profiles.Count);
        }

        /// <summary>Creates and starts a watcher per item. Caller holds <see cref="_lock"/>.</summary>
        private void RegisterProfile(Profile profile)
        {
            var settings = LightroomArchiveSettings.FromProfile(profile);
            var list = new List<ItemWatcher>();
            foreach (var item in profile.LightroomArchiveItems)
            {
                try
                {
                    list.Add(new ItemWatcher(item, settings, profile.Id, processor, operationLogFactory, runRecorder, logger, _stoppingToken));
                }
                catch (Exception ex)
                {
                    // A missing/unreadable source folder must not stop the other items being watched.
                    logger.LogWarning(ex, "Could not watch lightroom archive item '{Item}' (source '{Source}') for profile {ProfileId}.",
                        item.Name, item.SourceFolder, profile.Id);
                }
            }

            if (list.Count > 0)
            {
                _watchers[profile.Id] = list;
            }
        }

        /// <summary>Disposes and forgets a profile's watchers. Caller holds <see cref="_lock"/>.</summary>
        private void RemoveWatchers(int profileId)
        {
            if (_watchers.Remove(profileId, out var list))
            {
                foreach (var watcher in list)
                {
                    watcher.Dispose();
                }
            }
        }

        private void DisposeAllWatchers()
        {
            lock (_lock)
            {
                foreach (var list in _watchers.Values)
                {
                    foreach (var watcher in list)
                    {
                        watcher.Dispose();
                    }
                }
                _watchers.Clear();
            }
        }

        private async Task<Profile?> ReadProfileAsync(int profileId, CancellationToken cancellationToken)
        {
            await using var db = contextFactory.CreateDbContext();

            return await db.Profiles
                .AsNoTracking()
                .Include(p => p.LightroomArchiveItems)
                .FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken);
        }

        public override void Dispose()
        {
            DisposeAllWatchers();
            base.Dispose();
        }

        /// <summary>
        /// Watches a single item's source folder and batches its change events. A debounce timer is re-armed
        /// on every event; when it fires (after the source has been quiet for the debounce window) the queued
        /// changes are processed on a single in-flight pass. Mirrors <c>InstantSyncWatcherService.ItemWatcher</c>.
        /// </summary>
        private sealed class ItemWatcher : IDisposable
        {
            private readonly LightroomArchiveItem _item;
            private readonly LightroomArchiveSettings _settings;
            private readonly int _profileId;
            private readonly ILightroomArchiveProcessor _processor;
            private readonly IOperationLogFactory _logFactory;
            private readonly IBackupRunRecorder _runRecorder;
            private readonly ILogger _logger;
            private readonly CancellationToken _stoppingToken;

            private readonly FileSystemWatcher _watcher;
            private readonly Timer _timer;
            private readonly TimeSpan _debounce;

            private readonly object _gate = new();
            private readonly HashSet<string> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _pendingDeletes = new(StringComparer.OrdinalIgnoreCase);
            private bool _processing;
            private bool _disposed;

            public ItemWatcher(
                LightroomArchiveItem item,
                LightroomArchiveSettings settings,
                int profileId,
                ILightroomArchiveProcessor processor,
                IOperationLogFactory logFactory,
                IBackupRunRecorder runRecorder,
                ILogger logger,
                CancellationToken stoppingToken)
            {
                _item = item;
                _settings = settings;
                _profileId = profileId;
                _processor = processor;
                _logFactory = logFactory;
                _runRecorder = runRecorder;
                _logger = logger;
                _stoppingToken = stoppingToken;
                _debounce = TimeSpan.FromMilliseconds(Math.Max(0, item.DebounceMilliseconds));

                _timer = new Timer(OnDebounceElapsed, state: null, Timeout.Infinite, Timeout.Infinite);

                // Throws if the source folder doesn't exist — the caller logs and skips this item.
                _watcher = new FileSystemWatcher(item.SourceFolder)
                {
                    IncludeSubdirectories = item.IncludeSubFolders,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                 | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                _watcher.Created += OnChanged;
                _watcher.Changed += OnChanged;
                _watcher.Deleted += OnDeleted;
                _watcher.Renamed += OnRenamed;
                _watcher.Error += OnError;
                _watcher.EnableRaisingEvents = true;
            }

            private void OnChanged(object sender, FileSystemEventArgs e) => QueueChange(e.FullPath);

            private void OnDeleted(object sender, FileSystemEventArgs e) => QueueDelete(e.FullPath);

            private void OnRenamed(object sender, RenamedEventArgs e)
            {
                QueueDelete(e.OldFullPath);
                QueueChange(e.FullPath);
            }

            private void OnError(object sender, ErrorEventArgs e) =>
                _logger.LogWarning(e.GetException(),
                    "File watcher error for lightroom archive item '{Item}' (profile {ProfileId}); some changes may have been missed.",
                    _item.Name, _profileId);

            private void QueueChange(string fullPath)
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _pendingChanges.Add(fullPath);
                    _pendingDeletes.Remove(fullPath);
                }
                ArmTimer();
            }

            private void QueueDelete(string fullPath)
            {
                if (!_item.AllowDeletions)
                {
                    return;
                }
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _pendingDeletes.Add(fullPath);
                    _pendingChanges.Remove(fullPath);
                }
                ArmTimer();
            }

            private void ArmTimer()
            {
                try
                {
                    _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                    // Disposed concurrently — nothing to schedule.
                }
            }

            private void OnDebounceElapsed(object? state)
            {
                HashSet<string> changes, deletes;
                lock (_gate)
                {
                    if (_processing || _disposed)
                    {
                        return;
                    }
                    if (_pendingChanges.Count == 0 && _pendingDeletes.Count == 0)
                    {
                        return;
                    }

                    changes = new HashSet<string>(_pendingChanges, StringComparer.OrdinalIgnoreCase);
                    deletes = new HashSet<string>(_pendingDeletes, StringComparer.OrdinalIgnoreCase);
                    _pendingChanges.Clear();
                    _pendingDeletes.Clear();
                    _processing = true;
                }

                _ = Task.Run(() => ProcessThenReleaseAsync(changes, deletes));
            }

            private async Task ProcessThenReleaseAsync(HashSet<string> changes, HashSet<string> deletes)
            {
                try
                {
                    await ProcessFlushAsync(changes, deletes);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lightroom archive flush failed for item '{Item}' (profile {ProfileId}).", _item.Name, _profileId);
                }
                finally
                {
                    bool more;
                    lock (_gate)
                    {
                        _processing = false;
                        more = !_disposed && (_pendingChanges.Count > 0 || _pendingDeletes.Count > 0);
                    }
                    if (more)
                    {
                        ArmTimer();
                    }
                }
            }

            private async Task ProcessFlushAsync(HashSet<string> changes, HashSet<string> deletes)
            {
                var startedUtc = DateTimeOffset.UtcNow;
                var stopwatch = Stopwatch.StartNew();
                var changeCount = changes.Count + deletes.Count;

                // Deferred: the log is only created once the processor writes its first line, so an all-no-op
                // flush leaves no noise behind.
                var log = new DeferredOperationLogger(
                    _logFactory,
                    $"Lightroom Archive '{_item.Name}' — {changeCount} change(s)",
                    profileId: _profileId);

                BackupResult result;
                try
                {
                    result = await _processor.ProcessBatchAsync(_item, _settings, changes, deletes, log, progress: null, _stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return; // service shutting down
                }
                catch (Exception ex)
                {
                    await log.ErrorAsync($"Lightroom archive '{_item.Name}' failed", ex);
                    await RecordRunAsync(startedUtc, stopwatch, new BackupResult { Errors = 1 }, RunOutcome.Failed, log.OperationLogId);
                    await log.SetSummaryAsync($"Lightroom Archive '{_item.Name}' failed in {FormatDuration(stopwatch.Elapsed)}", OperationLogLevel.Error);
                    return;
                }

                // No real work (a no-op flush) — leave no log and no run row.
                if (!log.WasCreated)
                {
                    return;
                }

                stopwatch.Stop();

                // Record the run before the summary write (whose ILogWatcher.Notify the dashboard refreshes on).
                var outcome = result.Errors > 0 ? RunOutcome.CompletedWithErrors
                    : result.Warnings > 0 ? RunOutcome.CompletedWithWarnings
                    : RunOutcome.Success;
                await RecordRunAsync(startedUtc, stopwatch, result, outcome, log.OperationLogId);

                var duration = FormatDuration(stopwatch.Elapsed);
                var counts = $"{result.Copied} copied, {result.Updated} updated, {result.Deleted} deleted";
                await log.SetSummaryAsync(
                    result.Errors == 0
                        ? $"Lightroom Archive '{_item.Name}' synced {changeCount} change(s) in {duration} — {counts}"
                        : $"Lightroom Archive '{_item.Name}' completed with {result.Errors} error(s) in {duration} — {counts}",
                    result.Errors == 0 ? OperationLogLevel.Info : OperationLogLevel.Error);
            }

            // Records one BackupRun row for a flush that did real work, so live lightroom-archive activity
            // shows in the dashboard's Recent Runs (a no-op flush writes nothing and is never recorded).
            private async Task RecordRunAsync(DateTimeOffset startedUtc, Stopwatch stopwatch, BackupResult result, RunOutcome outcome, int operationLogId)
            {
                try
                {
                    await _runRecorder.RecordAsync(
                        _profileId, ProfileType.LightroomArchive, manual: false, startedUtc, stopwatch.Elapsed.TotalMilliseconds,
                        result, outcome, operationLogId == 0 ? null : operationLogId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not record lightroom archive run for item '{Item}' (profile {ProfileId}).", _item.Name, _profileId);
                }
            }

            private static string FormatDuration(TimeSpan elapsed) =>
                elapsed.TotalSeconds >= 1
                    ? $"{elapsed.TotalSeconds:0.##}s"
                    : $"{elapsed.TotalMilliseconds:0}ms";

            public void Dispose()
            {
                lock (_gate)
                {
                    _disposed = true;
                }
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _timer.Dispose();
            }
        }
    }
}
