using System.Diagnostics;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Logging;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Background service that keeps a live <see cref="FileSystemWatcher"/> on each item of every
    /// enabled <see cref="ProfileType.InstantSync"/> profile, mirroring changes into the target after a
    /// per-item debounce window. Also the <see cref="IInstantSyncManager"/> the rest of the app calls to
    /// keep the watchers in step with profile changes. Registered once and shared in all three roles
    /// (singleton, <see cref="IInstantSyncManager"/>, hosted service) — the instant-sync counterpart to
    /// <see cref="BackupSchedulerService"/>.
    ///
    /// Each item batches change events and processes them on a single in-flight pass, so changes that
    /// arrive while a copy is running are never lost and never start a second concurrent pass.
    /// </summary>
    public sealed class InstantSyncWatcherService(
        IDatabaseContextFactory contextFactory,
        IInstantSyncProcessor processor,
        IFolderPairSynchronizer synchronizer,
        IOperationLogFactory operationLogFactory,
        IBackupRunRecorder runRecorder,
        ILogger<InstantSyncWatcherService> logger) : BackgroundService, IInstantSyncManager
    {
        private readonly Dictionary<int, List<ItemWatcher>> _watchers = new();
        private readonly object _lock = new();

        private CancellationToken _stoppingToken;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _stoppingToken = stoppingToken;
            logger.LogInformation("Instant sync watcher service started.");

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
            logger.LogInformation("Instant sync watcher service stopping.");
        }

        public async Task SyncAsync(int profileId, CancellationToken cancellationToken = default)
        {
            var profile = await ReadProfileAsync(profileId, cancellationToken);

            lock (_lock)
            {
                RemoveWatchers(profileId);

                if (profile is { Enabled: true, Type: ProfileType.InstantSync })
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
                    .Include(p => p.InstantSyncItems)
                    .Where(p => p.Enabled && p.Type == ProfileType.InstantSync)
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

            logger.LogInformation("Instant sync watcher service loaded {Count} profile(s).", profiles.Count);
        }

        /// <summary>Creates and starts a watcher per item. Caller holds <see cref="_lock"/>.</summary>
        private void RegisterProfile(Profile profile)
        {
            var list = new List<ItemWatcher>();

            // A source on a remote connection can't be watched (no FileSystemWatcher over SMB) — it
            // only syncs via a manual "Run now". A remote target is fine (the flush reconciles to it).
            // The connection is profile-level, so this gates the whole profile.
            if (profile.SourceConnectionId is not null)
            {
                logger.LogInformation(
                    "Instant sync profile {ProfileId} has a remote source — live watching is not supported; use Run now.",
                    profile.Id);
                return;
            }

            foreach (var item in profile.InstantSyncItems)
            {
                try
                {
                    list.Add(new ItemWatcher(item, profile.Id, profile.TargetConnectionId, processor, synchronizer, operationLogFactory, runRecorder, logger, _stoppingToken));
                }
                catch (Exception ex)
                {
                    // A missing/unreadable source folder must not stop the other items being watched.
                    logger.LogWarning(ex, "Could not watch instant sync item '{Item}' (source '{Source}') for profile {ProfileId}.",
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
                .Include(p => p.InstantSyncItems)
                .FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken);
        }

        public override void Dispose()
        {
            DisposeAllWatchers();
            base.Dispose();
        }

        /// <summary>
        /// Watches a single item's source folder and batches its change events. A debounce timer is
        /// re-armed on every event; when it fires (after the source has been quiet for the debounce
        /// window) the queued changes are processed on a single in-flight pass.
        /// </summary>
        private sealed class ItemWatcher : IDisposable
        {
            private readonly InstantSyncItem _item;
            private readonly int _profileId;
            private readonly int? _targetConnectionId;
            private readonly IInstantSyncProcessor _processor;
            private readonly IFolderPairSynchronizer _synchronizer;
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
                InstantSyncItem item,
                int profileId,
                int? targetConnectionId,
                IInstantSyncProcessor processor,
                IFolderPairSynchronizer synchronizer,
                IOperationLogFactory logFactory,
                IBackupRunRecorder runRecorder,
                ILogger logger,
                CancellationToken stoppingToken)
            {
                _item = item;
                _profileId = profileId;
                _targetConnectionId = targetConnectionId;
                _processor = processor;
                _synchronizer = synchronizer;
                _logFactory = logFactory;
                _runRecorder = runRecorder;
                _logger = logger;
                _stoppingToken = stoppingToken;
                // Guard against a zero/negative debounce (timer requires a non-negative due time).
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
                // Old name disappeared; new name is a fresh change.
                QueueDelete(e.OldFullPath);
                QueueChange(e.FullPath);
            }

            private void OnError(object sender, ErrorEventArgs e) =>
                _logger.LogWarning(e.GetException(),
                    "File watcher error for instant sync item '{Item}' (profile {ProfileId}); some changes may have been missed.",
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
                    _pendingDeletes.Remove(fullPath); // a re-created path is a change, not a delete
                }
                ArmTimer();
            }

            private void QueueDelete(string fullPath)
            {
                if (!_item.AllowDeletions)
                {
                    return; // deletions aren't mirrored for this item
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
                    // A pass is already running — it will pick up these events, or the re-arm in the
                    // pass's finally will trigger another timer fire. Single in-flight pass per item.
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
                    _logger.LogError(ex, "Instant sync flush failed for item '{Item}' (profile {ProfileId}).", _item.Name, _profileId);
                }
                finally
                {
                    bool more;
                    lock (_gate)
                    {
                        _processing = false;
                        more = !_disposed && (_pendingChanges.Count > 0 || _pendingDeletes.Count > 0);
                    }
                    // Events arrived during processing — re-arm so they're flushed after the debounce.
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

                // Deferred: the log is only created once the processor writes its first line, so a flush
                // that turns out to be all no-ops (directory touch-events, files that vanished before the
                // flush ran) leaves no "synced N change(s) — 0 copied" noise behind.
                var log = new DeferredOperationLogger(
                    _logFactory,
                    $"Instant Sync '{_item.Name}' — {changeCount} change(s)",
                    profileId: _profileId);

                BackupResult result;
                try
                {
                    // A remote target can't be written incrementally by the local processor, so reconcile the
                    // whole item through the connection-aware folder-pair engine instead. Local targets keep
                    // the fast incremental path. (The source is always local here — remote sources aren't watched.)
                    result = _targetConnectionId is not null
                        ? await ReconcileViaConnectionAsync(log)
                        : await _processor.ProcessBatchAsync(_item, changes, deletes, log, _stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return; // service shutting down
                }
                catch (Exception ex)
                {
                    await log.ErrorAsync($"Instant sync '{_item.Name}' failed", ex);
                    await RecordRunAsync(startedUtc, stopwatch, new BackupResult { Errors = 1 }, RunOutcome.Failed, log.OperationLogId);
                    await log.SetSummaryAsync($"Instant Sync '{_item.Name}' failed in {FormatDuration(stopwatch.Elapsed)}", OperationLogLevel.Error);
                    return;
                }

                // Nothing was actually copied/deleted and no folders/errors were written — no log exists,
                // so there is nothing to summarise. Leave no entry for a no-op flush (and no run row).
                if (!log.WasCreated)
                {
                    return;
                }

                stopwatch.Stop();

                // Record the run before the summary write (whose ILogWatcher.Notify the dashboard refreshes
                // on) so the new row is visible when the dashboard reloads.
                var outcome = result.Errors > 0 ? RunOutcome.CompletedWithErrors
                    : result.Warnings > 0 ? RunOutcome.CompletedWithWarnings
                    : RunOutcome.Success;
                await RecordRunAsync(startedUtc, stopwatch, result, outcome, log.OperationLogId);

                var duration = FormatDuration(stopwatch.Elapsed);
                var counts = $"{result.Copied} copied, {result.Deleted} deleted";
                await log.SetSummaryAsync(
                    result.Errors == 0
                        ? $"Instant Sync '{_item.Name}' synced {changeCount} change(s) in {duration} — {counts}"
                        : $"Instant Sync '{_item.Name}' completed with {result.Errors} error(s) in {duration} — {counts}",
                    result.Errors == 0 ? OperationLogLevel.Info : OperationLogLevel.Error);
            }

            // Records one BackupRun row for a flush that did real work, so live instant-sync activity shows
            // in the dashboard's Recent Runs (a no-op flush writes nothing and is never recorded).
            private async Task RecordRunAsync(DateTimeOffset startedUtc, Stopwatch stopwatch, BackupResult result, RunOutcome outcome, int operationLogId)
            {
                try
                {
                    await _runRecorder.RecordAsync(
                        _profileId, ProfileType.InstantSync, manual: false, startedUtc, stopwatch.Elapsed.TotalMilliseconds,
                        result, outcome, operationLogId == 0 ? null : operationLogId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not record instant sync run for item '{Item}' (profile {ProfileId}).", _item.Name, _profileId);
                }
            }

            // Full reconcile of the item via the connection-aware folder-pair engine (used when the target
            // is on a connection). Instant sync is source-authoritative → always overwrite.
            private Task<BackupResult> ReconcileViaConnectionAsync(IOperationLogger log)
            {
                var pair = new FolderPair
                {
                    Name = _item.Name,
                    SourceFolder = _item.SourceFolder,
                    TargetFolder = _item.TargetFolder,
                    AllowDeletions = _item.AllowDeletions,
                    IncludeSubFolders = _item.IncludeSubFolders,
                    OverwriteBehaviour = OverwriteBehaviour.AlwaysOverwrite,
                };
                // Source is always local here (a remote source isn't watched); target is the profile connection.
                return _synchronizer.SyncAsync(pair, sourceConnectionId: null, _targetConnectionId, log, _stoppingToken);
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
